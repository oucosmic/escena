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
        var cues = BulkSelection();
        bool bulk = cues.Count > 1;
        BulkScroll.Visibility = bulk ? Visibility.Visible : Visibility.Collapsed;
        SingleScroll.Visibility = bulk ? Visibility.Collapsed : Visibility.Visible;
        BulkPanel.Children.Clear();
        if (!bulk) return;

        var p = BulkPanel;
        p.Children.Add(Ui.Header(L.T("PROPIETATS") + " · " + cues.Count + " " + L.T("CUES")));
        var kinds = cues.GroupBy(c => c.TypeName).Select(g => $"{g.Count()} × {g.Key}");
        p.Children.Add(Ui.Note(string.Join(" · ", kinds)));
        p.Children.Add(Ui.Note(L.T("Només surten les propietats que tenen totes. Els camps en blanc tenen valors diferents a cada cue; el que hi posis s'aplica a totes.")));

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

        var fields = BulkEdit.FieldsFor(cues, _show.Outputs, _show.VideoAudioChoices);
        foreach (var field in fields) p.Children.Add(BuildBulkField(field));
        if (fields.Count == 0) p.Children.Add(Ui.Note(L.T("Aquestes cues no tenen cap altra propietat en comú.")));
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
