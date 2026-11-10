using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Escena;

/// <summary>Ajudes per construir finestres en codi amb l'estil fosc d'Escena.</summary>
internal static class Ui
{
    public static Brush Brush(string key) => (Brush)Application.Current.FindResource(key);

    public static void StyleWindow(Window w, string title, double width, double height = double.NaN)
    {
        w.Title = title;
        w.Width = width;
        if (double.IsNaN(height)) w.SizeToContent = SizeToContent.Height; else w.Height = height;
        w.Background = Brush("BgBrush");
        w.Foreground = Brush("TextBrush");
        w.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        w.ShowInTaskbar = false;
        try { w.Icon = Application.Current.MainWindow?.Icon; } catch { /* sense icona */ }
    }

    public static TextBlock Header(string text) =>
        new() { Text = text, Style = (Style)Application.Current.FindResource("SectionHeader") };

    public static TextBlock Label(string text) =>
        new() { Text = text, Style = (Style)Application.Current.FindResource("FieldLabel"), TextWrapping = TextWrapping.Wrap };

    public static TextBlock Note(string text) =>
        new() { Text = text, Foreground = Brush("MutedBrush"), FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };

    public static Button Button(string text, RoutedEventHandler click, double width = double.NaN)
    {
        var b = new Button { Content = text, Width = width };
        b.Click += click;
        return b;
    }

    public static StackPanel Buttons(params Button[] buttons)
    {
        var p = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        foreach (var b in buttons) p.Children.Add(b);
        return p;
    }
}
