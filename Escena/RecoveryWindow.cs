using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Escena;

public enum RecoveryChoice { None, Continue, FromStart }

/// <summary>Pregunta què fer quan Escena no es va tancar bé la vegada anterior.</summary>
public sealed class RecoveryWindow : Window
{
    public RecoveryChoice Choice { get; private set; } = RecoveryChoice.None;

    public RecoveryWindow(SessionState state)
    {
        Title = "Recuperació";
        Width = 560;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = (Brush)Application.Current.FindResource("BgBrush");
        Foreground = (Brush)Application.Current.FindResource("TextBrush");

        string show = state.ShowPath != null ? $"«{Path.GetFileNameWithoutExtension(state.ShowPath)}»" : "sense títol";
        string text = $"Escena no es va tancar correctament ({state.Updated:HH:mm}).\n\n" +
                      $"Espectacle: {show}" + (state.HasUnsavedCopy ? " — amb canvis sense desar que es poden recuperar." : ".") +
                      (state.LastFiredLabel != null ? $"\nÚltima cue disparada: {state.LastFiredLabel}" : "") +
                      (state.StandbyLabel != null ? $"\nEn espera: {state.StandbyLabel}" : "");

        var panel = new StackPanel { Margin = new Thickness(18) };
        panel.Children.Add(new TextBlock { Text = "ESPECTACLE INTERROMPUT", Style = (Style)Application.Current.FindResource("SectionHeader") });
        panel.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 14, Margin = new Thickness(0, 4, 0, 16) });

        Button Make(string label, RecoveryChoice choice, bool primary = false)
        {
            var b = new Button { Content = label, Padding = new Thickness(12, 8, 12, 8), Margin = new Thickness(0, 4, 0, 0), HorizontalContentAlignment = HorizontalAlignment.Left };
            if (primary) b.Background = new SolidColorBrush(Color.FromRgb(0x2E, 0x9E, 0x4F));
            b.Click += (_, _) => { Choice = choice; DialogResult = true; };
            return b;
        }

        if (state.StandbyLabel != null)
            panel.Children.Add(Make($"Recupera i continua: en espera {state.StandbyLabel}", RecoveryChoice.Continue, primary: true));
        panel.Children.Add(Make("Recupera l'espectacle des del principi", RecoveryChoice.FromStart, primary: state.StandbyLabel == null));
        panel.Children.Add(Make("No recuperis res (espectacle nou)", RecoveryChoice.None));
        Content = panel;
    }
}
