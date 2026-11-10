using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using Escena.Model;

namespace Escena;

/// <summary>
/// Edita la matriu d'una cue cap a una sortida: files = canals de la cue, columnes = canals de la sortida.
/// Cada casella és el nivell en dB; buida = no hi envia res.
/// </summary>
public sealed class MatrixWindow : Window
{
    private readonly TextBox[,] _cells;
    private readonly int _in, _out;

    /// <summary>Punts de creu resultants; buit = encaminament per defecte.</summary>
    public List<CrossPoint> Result { get; private set; } = new();

    public MatrixWindow(string title, int inChannels, int outChannels, IReadOnlyList<CrossPoint> current, Action<int>? testChannel)
    {
        _in = Math.Max(1, inChannels);
        _out = Math.Max(1, outChannels);
        Ui.StyleWindow(this, "Matriu — " + title, Math.Clamp(220 + _out * 70, 560, 1100));
        ResizeMode = ResizeMode.NoResize;

        var root = new StackPanel { Margin = new Thickness(16) };
        root.Children.Add(Ui.Header("MATRIU DE SORTIDA"));
        root.Children.Add(Ui.Note("Cada fila és un canal de la cue i cada columna un canal de la sortida. Escriu el nivell en dB (0 = igual, −6 = la meitat…); deixa-la buida perquè no hi vagi. 🔊 fa sonar un to pel canal de sortida."));

        var grid = new Grid { Margin = new Thickness(0, 12, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(130) });
        for (int o = 0; o < _out; o++) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(66) });
        for (int r = 0; r < _in + 1; r++) grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        for (int o = 0; o < _out; o++)
        {
            var head = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
            head.Children.Add(new TextBlock { Text = $"Sortida {o + 1}", FontSize = 11, Foreground = Ui.Brush("MutedBrush"), HorizontalAlignment = HorizontalAlignment.Center });
            if (testChannel != null)
            {
                int ch = o;
                head.Children.Add(Ui.Button("🔊", (_, _) => testChannel(ch)));
            }
            Grid.SetColumn(head, o + 1);
            grid.Children.Add(head);
        }

        var matrix = AudioRouting.BuildMatrix(current, _in, _out);
        _cells = new TextBox[_in, _out];
        for (int i = 0; i < _in; i++)
        {
            string name = _in == 1 ? "Mono" : _in == 2 ? (i == 0 ? "Canal 1 (E)" : "Canal 2 (D)") : $"Canal {i + 1}";
            var label = new TextBlock { Text = name, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 4, 8, 4) };
            Grid.SetRow(label, i + 1);
            grid.Children.Add(label);
            for (int o = 0; o < _out; o++)
            {
                float g = matrix[i, o];
                var box = new TextBox
                {
                    Text = g > 0 ? (20 * Math.Log10(g)).ToString("0.#", CultureInfo.CurrentCulture) : "",
                    Margin = new Thickness(3), TextAlignment = TextAlignment.Center,
                };
                _cells[i, o] = box;
                Grid.SetRow(box, i + 1);
                Grid.SetColumn(box, o + 1);
                grid.Children.Add(box);
            }
        }
        root.Children.Add(new ScrollViewer { Content = grid, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled });
        if (inChannels <= 0) root.Children.Add(Ui.Note("No se sap quants canals té la font (encara no s'ha obert): es mostren 2."));

        root.Children.Add(Ui.Buttons(
            Ui.Button("Per defecte", (_, _) => { Result = new(); DialogResult = true; }),
            Ui.Button("Treu-ho tot", (_, _) => { foreach (var c in _cells) c.Text = ""; }),
            Ui.Button("D'acord", Ok, 100),
            Ui.Button("Cancel·la", (_, _) => DialogResult = false, 100)));
        Content = root;
    }

    private void Ok(object sender, RoutedEventArgs e)
    {
        var points = new List<CrossPoint>();
        for (int i = 0; i < _in; i++)
        for (int o = 0; o < _out; o++)
        {
            string text = _cells[i, o].Text.Trim().Replace("dB", "").Trim();
            if (text.Length == 0) continue;
            if (!double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out double db)
                && !double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out db))
            {
                MessageBox.Show(this, $"«{text}» no és un número (fila {i + 1}, sortida {o + 1}).", "Escena");
                return;
            }
            points.Add(new CrossPoint { In = i, Out = o, Db = Math.Clamp(db, AudioMath.SilenceDb, 12) });
        }
        // Si és igual que l'encaminament per defecte, es desa buida (així s'adapta si canvia la sortida).
        var def = AudioRouting.BuildMatrix(Array.Empty<CrossPoint>(), _in, _out);
        var mine = AudioRouting.BuildMatrix(points, _in, _out);
        bool same = true;
        for (int i = 0; i < _in && same; i++)
        for (int o = 0; o < _out; o++)
            if (Math.Abs(def[i, o] - mine[i, o]) > 1e-4) { same = false; break; }
        // Una matriu sense cap punt no és «per defecte»: és silenci. Es desa un punt a −60 perquè no quedi buida.
        if (points.Count == 0) points.Add(new CrossPoint { In = 0, Out = 0, Db = AudioMath.SilenceDb });
        Result = same ? new() : points;
        DialogResult = true;
    }
}
