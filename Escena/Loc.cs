using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;

namespace Escena;

/// <summary>
/// Traduccions de la interfície. El català és l'idioma de partida: el text català fa de clau i cada idioma
/// té un fitxer Lang/xx.json (incrustat al programa) amb «text català» → «traducció». Si un text no hi és,
/// es queda en català. Per afegir un idioma n'hi ha prou amb un fitxer nou i una línia a Options.Languages.
/// Els textos de les finestres es tradueixen sols quan es carreguen (no cal tocar cada XAML).
/// </summary>
public static class L
{
    private static Dictionary<string, string>? _map;

    public static string Language { get; private set; } = "ca";

    public static void Init(string language)
    {
        Language = string.IsNullOrWhiteSpace(language) ? "ca" : language;
        _map = null;
        if (Language == "ca") return;
        try
        {
            using var stream = typeof(L).Assembly.GetManifestResourceStream($"Escena.Lang.{Language}.json");
            if (stream == null) return;
            _map = JsonSerializer.Deserialize<Dictionary<string, string>>(stream);
            EventManager.RegisterClassHandler(typeof(FrameworkElement), FrameworkElement.LoadedEvent, new RoutedEventHandler(OnLoaded), true);
        }
        catch { _map = null; }
    }

    /// <summary>Tradueix un text català (si no hi ha traducció, el torna igual).</summary>
    public static string T(string catalan) =>
        _map != null && catalan != null && _map.TryGetValue(catalan, out var t) ? t : catalan!;

    private static void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is DependencyObject d) Translate(d);
    }

    private static bool IsBound(DependencyObject d, DependencyProperty p) => BindingOperations.GetBindingBase(d, p) != null;

    private static void Translate(DependencyObject d)
    {
        switch (d)
        {
            case Window w when !IsBound(w, Window.TitleProperty):
                w.Title = T(w.Title);
                break;
            case TextBlock tb when !IsBound(tb, TextBlock.TextProperty):
                if (tb.Inlines.Count > 1 || tb.Inlines.FirstInline is not Run)
                {
                    foreach (var run in tb.Inlines.OfType<Run>().ToList())
                        if (!IsBound(run, Run.TextProperty)) run.Text = T(run.Text);
                }
                else if (tb.Text.Length > 0) tb.Text = T(tb.Text);
                break;
            case HeaderedItemsControl h when h.Header is string s && !IsBound(h, HeaderedItemsControl.HeaderProperty):
                h.Header = T(s);
                break;
            case ContentControl c when c.Content is string s && !IsBound(c, ContentControl.ContentProperty):
                c.Content = T(s);
                break;
        }
        if (d is FrameworkElement fe)
        {
            if (fe.ToolTip is string tip && !IsBound(fe, FrameworkElement.ToolTipProperty)) fe.ToolTip = T(tip);
            if (fe.ContextMenu != null) foreach (var item in fe.ContextMenu.Items.OfType<MenuItem>()) if (item.Header is string mh) item.Header = T(mh);
        }
        if (d is DataGrid grid)
            foreach (var col in grid.Columns) if (col.Header is string h) col.Header = T(h);
        if (d is ListView { View: GridView view })
            foreach (var col in view.Columns) if (col.Header is string h) col.Header = T(h);
    }
}
