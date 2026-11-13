using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Escena.Model;

namespace Escena;

/// <summary>Inspector: les propietats de la cue seleccionada, a sota de la llista i repartides en pestanyes.</summary>
public partial class MainWindow
{
    /// <summary>Una pestanya: el títol i la clau de la plantilla (a MainWindow.xaml) que la dibuixa.</summary>
    internal sealed record InspectorTab(string Title, string TemplateKey);

    private static readonly InspectorTab General = new("General", "Tab.General");

    /// <summary>Pestanyes de cada tipus de cue. La primera sempre és «General».</summary>
    internal static IReadOnlyList<InspectorTab> TabsFor(Cue cue) => cue switch
    {
        AudioCue => new[] { General, new("Fitxer i temps", "Audio.File"), new("Nivells i foses", "Audio.Levels"), new("Sortides", "Tab.Routing") },
        MicCue => new[] { General, new("Entrada i nivells", "Mic.Levels"), new("Sortides", "Tab.Routing") },
        VideoCue => new[] { General, new("Vídeo", "Video.File"), new("Nivells i foses", "Video.Levels") },
        ImageCue => new[] { General, new("Imatge", "Image.Main") },
        TextCue => new[] { General, new("Text", "Text.Main") },
        FadeCue => new[] { General, new("Fosa", "Fade.Main") },
        ControlCue => new[] { General, new("Acció", "Control.Main") },
        WaitCue => new[] { General, new("Espera", "Wait.Main") },
        GroupCue => new[] { General, new("Grup", "Group.Main") },
        _ => new[] { General },
    };

    /// <summary>L'última pestanya triada de cada tipus: en tornar a una cue d'aquell tipus, s'hi torna.</summary>
    private readonly Dictionary<Type, int> _tabByType = new();
    private Type? _inspectorType;
    private double _inspectorHeight = 250;

    /// <summary>Posa a l'inspector la cue seleccionada, amb les pestanyes del seu tipus.</summary>
    private void RefreshInspector()
    {
        var cue = _show.SelectedCue;
        bool bulk = BulkScroll.Visibility == Visibility.Visible;
        InspectorTitle.Visibility = cue != null && !bulk ? Visibility.Visible : Visibility.Collapsed;
        InspectorButtons.Visibility = InspectorTitle.Visibility;
        InspectorCaption.Visibility = cue == null || bulk ? Visibility.Visible : Visibility.Collapsed;
        InspectorTabs.Visibility = InspectorTitle.Visibility;
        InspectorEmpty.Visibility = cue == null ? Visibility.Visible : Visibility.Collapsed;

        if (cue == null || bulk || _show.IsShowMode)
        {
            // Res a editar: es buida perquè cap camp quedi lligat a una cue que no es veu.
            InspectorContent.Content = null;
            return;
        }

        var type = cue.GetType();
        if (type != _inspectorType)
        {
            _inspectorType = type;
            var tabs = TabsFor(cue);
            int selected = Math.Clamp(_tabByType.GetValueOrDefault(type), 0, tabs.Count - 1);
            InspectorTabs.Children.Clear();
            for (int i = 0; i < tabs.Count; i++)
            {
                int index = i;
                var tab = tabs[i];
                var button = new RadioButton
                {
                    Content = L.T(tab.Title),
                    Style = (Style)FindResource("TabRadio"),
                    GroupName = "InspectorTabs",
                    IsChecked = i == selected,
                    Tag = tab,
                };
                button.Checked += (_, _) =>
                {
                    _tabByType[type] = index;
                    ShowTab(tab);
                };
                InspectorTabs.Children.Add(button);
            }
            // Primer la plantilla i després la cue: cap camp de la plantilla vella veu la cue nova.
            InspectorContent.Content = null;
            InspectorContent.ContentTemplate = (DataTemplate)FindResource(tabs[selected].TemplateKey);
        }
        if (!ReferenceEquals(InspectorContent.Content, cue)) InspectorContent.Content = cue;
    }

    private void ShowTab(InspectorTab tab)
    {
        CommitFocusedField();
        var cue = _show.SelectedCue;
        InspectorContent.Content = null;
        InspectorContent.ContentTemplate = (DataTemplate)FindResource(tab.TemplateKey);
        InspectorContent.Content = cue;
        SingleScroll.ScrollToTop();
    }

    /// <summary>Un camp a mig escriure es confirma abans de canviar el que es veu.</summary>
    private static void CommitFocusedField()
    {
        if (System.Windows.Input.Keyboard.FocusedElement is TextBox box)
            box.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
    }

    /// <summary>Passa a la pestanya del costat (Ctrl+Tab endavant, Ctrl+Maj+Tab enrere).</summary>
    private void NextInspectorTab(int delta)
    {
        var buttons = InspectorTabs.Children.OfType<RadioButton>().ToList();
        if (buttons.Count < 2 || InspectorTabs.Visibility != Visibility.Visible) return;
        int current = buttons.FindIndex(b => b.IsChecked == true);
        buttons[((current + delta) % buttons.Count + buttons.Count) % buttons.Count].IsChecked = true;
    }

    /// <summary>
    /// Edició: llista a dalt i inspector a sota. Funció: l'inspector desapareix (la llista guanya alçada)
    /// i a la dreta s'obre el panell gran de la funció.
    /// </summary>
    private void ApplyModeLayout()
    {
        bool showMode = _show.IsShowMode;
        ShowSplitColumn.Width = new GridLength(showMode ? 6 : 0);
        ShowColumn.Width = new GridLength(showMode ? 370 : 0);
        ShowColumn.MinWidth = showMode ? 280 : 0;
        if (showMode)
        {
            if (InspectorRow.ActualHeight > 0) _inspectorHeight = InspectorRow.ActualHeight;
            InspectorRow.MinHeight = 0;
            InspectorRow.Height = new GridLength(0);
            InspectorSplitRow.Height = new GridLength(0);
        }
        else
        {
            InspectorRow.MinHeight = 110;
            InspectorRow.Height = new GridLength(_inspectorHeight);
            InspectorSplitRow.Height = new GridLength(6);
        }
    }

    private void InspectorSplitter_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        _inspectorHeight = InspectorRow.ActualHeight;
        _show.MachineSettings.InspectorHeight = Math.Round(_inspectorHeight);
        _show.MachineSettings.Save();
    }
}
