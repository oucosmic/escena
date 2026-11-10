using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Escena.Model;
using Escena.Video;
using Microsoft.Win32;

namespace Escena;

/// <summary>«Comença des de…»: tria la cue i el temps dins d'aquella cue.</summary>
public sealed class StartFromWindow : Window
{
    private readonly ComboBox _cue = new() { DisplayMemberPath = nameof(Cue.DisplayName) };
    private readonly TextBox _time = new() { Text = "0:00", Width = 120, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly CheckBox _earlier = new() { Content = "Engega també les cues anteriors que encara sonarien (bucles, imatges fixes, micròfons)", IsChecked = true };

    public Cue? ChosenCue => _cue.SelectedItem as Cue;
    public double Seconds { get; private set; }
    public bool IncludeEarlier => _earlier.IsChecked == true;

    public StartFromWindow(ShowController show)
    {
        Ui.StyleWindow(this, "Comença des de…", 560);
        ResizeMode = ResizeMode.NoResize;
        var root = new StackPanel { Margin = new Thickness(16) };
        root.Children.Add(Ui.Header("COMENÇA DES DE… (ASSAIG)"));
        root.Children.Add(Ui.Note("Posa l'espectacle com estaria un temps després del GO d'una cue: les cues encadenades que ja sonarien comencen a mitges, les foses ja fetes s'apliquen i queda en espera la cue que tocaria."));
        root.Children.Add(Ui.Label("Cue"));
        _cue.ItemsSource = show.Cues;
        _cue.SelectedItem = show.SelectedCue ?? show.StandbyCue ?? show.Cues.FirstOrDefault();
        root.Children.Add(_cue);
        root.Children.Add(Ui.Label("Temps des del GO d'aquesta cue (m:ss o segons)"));
        root.Children.Add(_time);
        root.Children.Add(_earlier);
        root.Children.Add(Ui.Buttons(Ui.Button("Comença", Ok, 110), Ui.Button("Cancel·la", (_, _) => DialogResult = false, 100)));
        Content = root;
        Loaded += (_, _) => { _time.Focus(); _time.SelectAll(); };
    }

    public static bool TryParseTime(string text, out double seconds)
    {
        text = text.Trim().Replace(',', '.');
        seconds = 0;
        var parts = text.Split(':');
        double total = 0;
        foreach (var p in parts)
        {
            if (!double.TryParse(p, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) || v < 0) return false;
            total = total * 60 + v;
        }
        seconds = total;
        return parts.Length is >= 1 and <= 3;
    }

    private void Ok(object sender, RoutedEventArgs e)
    {
        if (ChosenCue == null) return;
        if (!TryParseTime(_time.Text, out double s))
        {
            MessageBox.Show(this, "Escriu el temps com 2:30 o 150.", "Escena");
            return;
        }
        Seconds = s;
        DialogResult = true;
    }
}

/// <summary>Registre d'assaig: quan s'ha disparat cada cue i quant temps hi ha entre una i la següent.</summary>
public sealed class LogWindow : Window
{
    private readonly ShowController _show;
    private readonly DataGrid _grid = new()
    {
        AutoGenerateColumns = false, IsReadOnly = true, SelectionMode = DataGridSelectionMode.Single,
        CanUserSortColumns = false, RowHeight = 26,
    };
    private readonly TextBlock _status = new() { Foreground = Ui.Brush("MutedBrush"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };

    public LogWindow(ShowController show)
    {
        _show = show;
        Ui.StyleWindow(this, "Registre d'assaig", 760, 560);
        ShowInTaskbar = true;
        var root = new DockPanel { Margin = new Thickness(14) };
        var top = new StackPanel();
        top.Children.Add(Ui.Header("REGISTRE D'ASSAIG"));
        top.Children.Add(Ui.Note("Cada cue disparada, amb l'hora i el temps des de l'anterior. Per fer automàtic un GO que sempre ve al mateix temps, selecciona'l i prem «Encadena amb l'anterior»."));
        DockPanel.SetDock(top, Dock.Top);
        root.Children.Add(top);

        var bottom = new StackPanel();
        bottom.Children.Add(_status);
        bottom.Children.Add(Ui.Buttons(
            Ui.Button("Encadena amb l'anterior", ApplyTiming),
            Ui.Button("Exporta CSV…", Export),
            Ui.Button("Obre la carpeta", (_, _) =>
            {
                Directory.CreateDirectory(RehearsalLog.Folder);
                System.Diagnostics.Process.Start("explorer.exe", RehearsalLog.Folder);
            }),
            Ui.Button("Buida", (_, _) => _show.Log.Clear()),
            Ui.Button("Tanca", (_, _) => Close(), 90)));
        DockPanel.SetDock(bottom, Dock.Bottom);
        root.Children.Add(bottom);

        _grid.Columns.Add(new DataGridTextColumn { Header = "Hora", Binding = new Binding(nameof(LogEntry.TimeText)), Width = 100 });
        _grid.Columns.Add(new DataGridTextColumn { Header = "#", Binding = new Binding(nameof(LogEntry.Number)), Width = 60 });
        _grid.Columns.Add(new DataGridTextColumn { Header = "Cue", Binding = new Binding(nameof(LogEntry.Name)), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        _grid.Columns.Add(new DataGridTextColumn { Header = "Com", Binding = new Binding(nameof(LogEntry.Source)), Width = 90 });
        _grid.Columns.Add(new DataGridTextColumn { Header = "Des de l'anterior", Binding = new Binding(nameof(LogEntry.SincePreviousText)), Width = 130 });
        _grid.ItemsSource = show.Log.Entries;
        show.Log.Entries.CollectionChanged += OnEntries;
        Closed += (_, _) => show.Log.Entries.CollectionChanged -= OnEntries;
        root.Children.Add(_grid);
        Content = root;
        PreviewKeyDown += (_, e) =>
        {
            // Espai, Esc… continuen controlant l'espectacle encara que aquesta finestra tingui el focus.
            if (Keyboard.FocusedElement is not TextBox) (Application.Current.MainWindow as MainWindow)?.HandleShortcut(e);
        };
    }

    private void OnEntries(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        if (_show.Log.Entries.Count > 0) _grid.ScrollIntoView(_show.Log.Entries[^1]);
    }

    private void ApplyTiming(object sender, RoutedEventArgs e)
    {
        if (_grid.SelectedItem is not LogEntry entry)
        {
            _status.Text = "Selecciona primer la cue que vols que es dispari sola.";
            return;
        }
        _status.Text = _show.ApplyLoggedTiming(entry);
    }

    private void Export(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog { Filter = "CSV (*.csv)|*.csv", FileName = $"registre-{DateTime.Now:yyyy-MM-dd-HHmm}.csv" };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            _show.Log.Export(dialog.FileName);
            _status.Text = "Exportat: " + dialog.FileName;
        }
        catch (Exception ex) { _status.Text = "No s'ha pogut exportar: " + ex.Message; }
    }
}

/// <summary>
/// Vista de regidor: finestra simplificada amb lletra gran (cue en curs, temps que li queda, següent i notes).
/// F11 = pantalla completa · la tecla Pantalla canvia de monitor. Les altres tecles van a Escena (Espai = GO).
/// </summary>
public sealed class StageWindow : Window
{
    private int _monitorIndex = -1;
    private WindowState _restoreState;

    public StageWindow(ShowController show)
    {
        Ui.StyleWindow(this, "Escena — Regidor", 1000, 640);
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ShowInTaskbar = true;
        DataContext = show;
        Background = Brushes.Black;

        var root = new Grid { Margin = new Thickness(30) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var top = new DockPanel();
        var clock = Big(nameof(ShowController.StageClock), 34, "#9BA0A8");
        DockPanel.SetDock(clock, Dock.Right);
        top.Children.Add(clock);
        top.Children.Add(new TextBlock { Text = "REGIDOR", Foreground = Ui.Brush("MutedBrush"), FontSize = 20, FontWeight = FontWeights.Bold, VerticalAlignment = VerticalAlignment.Center });
        root.Children.Add(top);

        var now = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        now.Children.Add(Caption("ARA"));
        now.Children.Add(Fit(Big(nameof(ShowController.StageCurrent), 80, "#FFFFFF")));
        now.Children.Add(Big(nameof(ShowController.StageRemaining), 90, "#7BE495", mono: true));
        Grid.SetRow(now, 1);
        root.Children.Add(now);

        var next = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        next.Children.Add(Caption("SEGÜENT (GO)"));
        var nextName = Big("StandbyCue.DisplayName", 64, "#F0B429");
        nextName.SetBinding(TextBlock.TextProperty, new Binding("StandbyCue.DisplayName") { FallbackValue = "— final de la llista —", TargetNullValue = "— final de la llista —" });
        next.Children.Add(Fit(nextName));
        var notes = Big("StandbyCue.Notes", 34, "#D8DCE2");
        notes.TextWrapping = TextWrapping.Wrap;
        next.Children.Add(notes);
        Grid.SetRow(next, 2);
        root.Children.Add(next);

        var hint = new TextBlock
        {
            Text = "Espai: GO · F11: pantalla completa · F10: canvia de pantalla · les altres tecles d'Escena funcionen igual",
            Foreground = Ui.Brush("MutedBrush"), FontSize = 13, HorizontalAlignment = HorizontalAlignment.Center,
        };
        Grid.SetRow(hint, 3);
        root.Children.Add(hint);
        Content = root;

        PreviewKeyDown += OnKey;
    }

    private static TextBlock Caption(string text) =>
        new() { Text = text, Foreground = Ui.Brush("MutedBrush"), FontSize = 22, FontWeight = FontWeights.Bold };

    private static TextBlock Big(string path, double size, string color, bool mono = false)
    {
        var t = new TextBlock
        {
            FontSize = size, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color)),
        };
        if (mono) t.FontFamily = new FontFamily("Consolas");
        t.SetBinding(TextBlock.TextProperty, new Binding(path));
        return t;
    }

    /// <summary>Els noms llargs s'encongeixen en lloc de tallar-se.</summary>
    private static Viewbox Fit(TextBlock t)
    {
        t.TextTrimming = TextTrimming.None;
        return new Viewbox { Child = t, Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly, HorizontalAlignment = HorizontalAlignment.Left };
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.F11)
        {
            ToggleFullscreen();
            e.Handled = true;
        }
        else if (key == Key.F10)
        {
            NextMonitor();
            e.Handled = true;
        }
        else (Application.Current.MainWindow as MainWindow)?.HandleShortcut(e);
    }

    private void ToggleFullscreen()
    {
        if (WindowStyle == WindowStyle.None)
        {
            WindowStyle = WindowStyle.SingleBorderWindow;
            ResizeMode = ResizeMode.CanResize;
            WindowState = _restoreState;
            Topmost = false;
        }
        else
        {
            _restoreState = WindowState;
            WindowState = WindowState.Normal;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            WindowState = WindowState.Maximized;
        }
    }

    /// <summary>Mou la finestra a la pantalla següent (útil per posar-la al monitor del regidor).</summary>
    private void NextMonitor()
    {
        var monitors = Monitors.List();
        if (monitors.Count == 0) return;
        _monitorIndex = (_monitorIndex + 1) % monitors.Count;
        var m = monitors[_monitorIndex];
        bool full = WindowStyle == WindowStyle.None;
        WindowState = WindowState.Normal;
        var dpi = VisualTreeHelper.GetDpi(this);
        Left = m.Left / dpi.DpiScaleX + 40;
        Top = m.Top / dpi.DpiScaleY + 40;
        if (full) WindowState = WindowState.Maximized;
    }
}
