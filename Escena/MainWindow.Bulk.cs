using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Escena.Model;

namespace Escena;

/// <summary>Panell de propietats quan hi ha diverses cues seleccionades: només les comunes, i s'apliquen a totes.</summary>
public partial class MainWindow
{
    private bool _bulkRefreshQueued;

    private void CueGrid_SelectionChanged(object sender, SelectionChangedEventArgs e) => QueueBulkRefresh();

    // ---------------- Mode funció ----------------

    private void ShowMode_Click(object sender, RoutedEventArgs e) => ToggleShowMode();

    /// <summary>Entra o surt del mode funció (F9): bloqueja l'edició i mostra el panell gran.</summary>
    private void ToggleShowMode()
    {
        // Un camp a mig escriure es confirma abans de bloquejar.
        if (Keyboard.FocusedElement is TextBox box) box.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
        _show.IsShowMode = !_show.IsShowMode;
        ApplyModeLayout();
        FocusCues();
        RefreshBulkPanel();
    }

    /// <summary>El menú del botó dret de la llista és tot d'edició: en mode funció no s'obre.</summary>
    private void CueGrid_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (_show.IsShowMode) e.Handled = true;
    }

    /// <summary>Refà el panell un sol cop encara que la selecció canviï moltes vegades seguides.</summary>
    private void QueueBulkRefresh()
    {
        if (_bulkRefreshQueued) return;
        _bulkRefreshQueued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            _bulkRefreshQueued = false;
            RefreshBulkPanel();
        });
    }

    private List<Cue> BulkSelection() =>
        _show.IsListLayout
            ? CueGrid.SelectedItems.OfType<Cue>().Where(_show.Cues.Contains).OrderBy(_show.Cues.IndexOf).ToList()
            : new List<Cue>();

    private void RefreshBulkPanel()
    {
        // En mode funció no hi ha propietats: el seu lloc l'ocupa el panell gran de la funció.
        if (_show.IsShowMode)
        {
            BulkScroll.Visibility = SingleScroll.Visibility = Visibility.Collapsed;
            BulkPanel.Children.Clear();
            RefreshInspector();
            return;
        }
        var cues = BulkSelection();
        bool bulk = cues.Count > 1;
        BulkScroll.Visibility = bulk ? Visibility.Visible : Visibility.Collapsed;
        SingleScroll.Visibility = bulk ? Visibility.Collapsed : Visibility.Visible;
        BulkPanel.Children.Clear();
        InspectorCaption.Text = bulk ? L.T("PROPIETATS") + " · " + cues.Count + " " + L.T("CUES") : L.T("PROPIETATS");
        RefreshInspector();
        if (!bulk) return;

        var top = BulkPanel;
        var kinds = cues.GroupBy(c => c.TypeName).Select(g => $"{g.Count()} × {g.Key}");
        top.Children.Add(Ui.Note(string.Join(" · ", kinds) + "  —  " +
            L.T("Només surten les propietats que tenen totes. Els camps en blanc tenen valors diferents a cada cue; el que hi posis s'aplica a totes.")));

        // Els camps es posen de costat i salten de fila quan no hi caben.
        var wrap = new WrapPanel();
        top.Children.Add(wrap);
        var p = new StackPanel { Width = 300, Margin = new Thickness(0, 0, 24, 6) };
        wrap.Children.Add(p);

        // Numerar
        p.Children.Add(Ui.Label(L.T("Numera per ordre de llista (inici i pas)")));
        var start = new TextBox { Text = FirstNumber(cues), Width = 70, Margin = new Thickness(0, 0, 6, 0) };
        var step = new TextBox { Text = "1", Width = 50, Margin = new Thickness(0, 0, 6, 0) };
        var renumber = Ui.Button(L.T("Numera"), (_, _) =>
        {
            if (!BulkField.TryParseNumber(start.Text, out double s) || !BulkField.TryParseNumber(step.Text, out double st) || st <= 0)
            {
                _show.Status = L.T("Escriu un número d'inici i un pas més gran que 0.");
                return;
            }
            var numbers = BulkEdit.Renumber(cues, s, st);
            _show.Status = $"{L.T("Numerades")} {numbers.First()}–{numbers.Last()}.";
        });
        renumber.Padding = new Thickness(10, 2, 10, 2);
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(start);
        row.Children.Add(step);
        row.Children.Add(renumber);
        p.Children.Add(row);

        // Igualar el volum (només si totes són àudio o vídeo)
        if (cues.All(c => c is AudioCue or VideoCue))
        {
            p.Children.Add(Ui.Label(L.T("Iguala el volum (nivell mitjà en dB)")));
            var target = new TextBox { Text = "-20", Width = 70, Margin = new Thickness(0, 0, 6, 0),
                ToolTip = L.T("Nivell al qual sonaran totes: -20 és un nivell còmode per a música i efectes; -16 sona més fort.") };
            Button? normalize = null;
            normalize = Ui.Button(L.T("Iguala"), async (_, _) =>
            {
                if (!BulkField.TryParseNumber(target.Text, out double t) || t > 0 || t < -60)
                {
                    _show.Status = L.T("Escriu un nivell entre -60 i 0 dB.");
                    return;
                }
                normalize!.IsEnabled = false;
                _show.Status = L.T("Mesurant el volum dels fitxers…");
                var list = cues.OfType<PlayableCue>().ToList();
                var plan = await Task.Run(() => Escena.Audio.Loudness.Plan(list, t));
                foreach (var r in plan.Where(r => r.Error == null)) r.Cue.VolumeDb = r.NewVolumeDb;
                normalize.IsEnabled = true;
                int ok = plan.Count(r => r.Error == null), limited = plan.Count(r => r.Error == null && r.Limited);
                var failed = plan.Where(r => r.Error != null).ToList();
                string msg = $"{L.T("Volum igualat a")} {ok} {L.T("cues")}.";
                if (limited > 0) msg += $" {limited} {L.T("han quedat més baixes per no saturar.")}";
                if (failed.Count > 0) msg += $" {L.T("No s'han pogut mesurar:")} " + string.Join(", ", failed.Select(f => $"{f.Cue.DisplayName} ({f.Error})"));
                _show.Status = msg;
                QueueBulkRefresh();
            });
            normalize.Padding = new Thickness(10, 2, 10, 2);
            normalize.ToolTip = L.T("Mesura cada fitxer (entre el seu inici i final) i posa a cada cue el volum que la deixa a aquest nivell. Si una cue saturaria, es queda més baixa. Es pot desfer amb Ctrl+Z.");
            var nrow = new StackPanel { Orientation = Orientation.Horizontal };
            nrow.Children.Add(target);
            nrow.Children.Add(normalize);
            p.Children.Add(nrow);
        }

        var fields = BulkEdit.FieldsFor(cues, _show.Outputs, _show.VideoAudioChoices);
        foreach (var field in fields)
        {
            var cell = BuildBulkField(field);
            cell.Width = 300;
            cell.Margin = new Thickness(0, 0, 24, 6);
            wrap.Children.Add(cell);
        }
        if (fields.Count == 0) top.Children.Add(Ui.Note(L.T("Aquestes cues no tenen cap altra propietat en comú.")));
    }

    private static string FirstNumber(List<Cue> cues)
    {
        var first = cues[0].Number;
        return BulkField.TryParseNumber(first, out _) ? first : "1";
    }

    private FrameworkElement BuildBulkField(BulkField field)
    {
        var panel = new StackPanel();
        string mixedTip = L.T("Les cues seleccionades tenen valors diferents");
        switch (field.Kind)
        {
            case BulkKind.Bool:
            {
                var cb = new CheckBox
                {
                    Content = field.Label,
                    IsChecked = field.IsMixed ? null : (bool?)field.Common,
                    Margin = new Thickness(0, 10, 0, 0),
                    ToolTip = field.IsMixed ? mixedTip : null,
                };
                cb.Click += (_, _) =>
                {
                    bool value = cb.IsChecked == true;
                    cb.IsChecked = value;
                    field.Apply(value);
                    QueueBulkRefresh();
                };
                panel.Children.Add(cb);
                break;
            }
            case BulkKind.Choice:
            {
                panel.Children.Add(Ui.Label(field.Label));
                var combo = new ComboBox { ItemsSource = field.Options, ToolTip = field.IsMixed ? mixedTip : null };
                if (!field.IsMixed) combo.SelectedItem = field.Options.FirstOrDefault(o => Equals(o.Value, field.Common));
                combo.SelectionChanged += (_, _) =>
                {
                    // El valor inicial es posa abans d'enganxar aquest gestor: aquí només arriben canvis de l'usuari.
                    if (combo.SelectedItem is BulkOption o)
                    {
                        field.Apply(o.Value);
                        QueueBulkRefresh();
                    }
                };
                panel.Children.Add(combo);
                break;
            }
            default:
            {
                panel.Children.Add(Ui.Label(field.Label));
                string original = field.CommonText;
                var box = new TextBox { Text = original, Tag = "bulk", ToolTip = field.IsMixed ? mixedTip : null };
                if (field.Property == nameof(Cue.Notes)) { box.AcceptsReturn = true; box.TextWrapping = TextWrapping.Wrap; box.MinHeight = 50; }
                box.LostKeyboardFocus += (_, _) =>
                {
                    if (box.Text == original) return;
                    if (field.Kind == BulkKind.Number && box.Text.Trim() == "") { box.Text = original; return; }
                    if (!field.Apply(box.Text))
                    {
                        _show.Status = L.T("Valor no vàlid:") + " " + box.Text;
                        box.Text = original;
                        return;
                    }
                    original = box.Text;
                    QueueBulkRefresh();
                };
                panel.Children.Add(box);
                break;
            }
        }
        return panel;
    }
}
