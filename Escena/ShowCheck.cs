using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Escena.Audio;
using Escena.Model;

namespace Escena;

public enum CheckLevel { Error, Warning, Info }

public sealed record CheckIssue(CheckLevel Level, Cue? Cue, string Message)
{
    public string Icon => Level switch { CheckLevel.Error => "⛔", CheckLevel.Warning => "⚠", _ => "ℹ" };
    public string CueText => Cue?.DisplayName ?? "Espectacle";
}

/// <summary>Comprovació abans de la funció: fitxers, sortides, cues buides i encadenaments perillosos.</summary>
public static class ShowCheck
{
    public static List<CheckIssue> Run(ShowController show)
    {
        var issues = new List<CheckIssue>();
        void Add(CheckLevel level, Cue? cue, string message) => issues.Add(new CheckIssue(level, cue, message));

        foreach (var w in show.OutputWarnings) Add(CheckLevel.Error, null, "Sortida: " + w);

        var cues = show.Cues.ToList();
        var ids = cues.Select(c => c.Id).ToHashSet();
        var outputIds = show.Outputs.Select(o => o.Id).ToHashSet();
        var inputs = AudioDevices.Inputs().Select(d => d.Id).ToHashSet();

        for (int i = 0; i < cues.Count; i++)
        {
            var c = cues[i];
            // Els encadenaments van a la germana següent (dins del mateix grup).
            var siblings = ShowTree.Siblings(cues, c);
            int si = siblings.IndexOf(c);
            var next = si + 1 < siblings.Count ? siblings[si + 1] : null;

            // Fitxers
            if (c is MediaCue media and not MicCue and not TextCue)
            {
                if (string.IsNullOrWhiteSpace(media.FilePath)) Add(CheckLevel.Error, c, "No té cap fitxer.");
                else if (!File.Exists(media.FilePath)) Add(CheckLevel.Error, c, "No es troba el fitxer: " + media.FilePath);
                else if (media.ErrorMessage != null) Add(CheckLevel.Error, c, media.ErrorMessage);
            }
            if (c is MediaCue { OutputId: { } oid } && !outputIds.Contains(oid))
                Add(CheckLevel.Warning, c, "La sortida triada ja no existeix: sonarà per la primera adequada.");
            if (c is VideoCue { AudioOutputId: { } aid } && !outputIds.Contains(aid))
                Add(CheckLevel.Warning, c, "La sortida d'àudio del vídeo ja no existeix: el so sortirà pel predeterminat de Windows.");
            if (c is TextCue t && string.IsNullOrWhiteSpace(t.Text)) Add(CheckLevel.Warning, c, "El text és buit.");
            if (c is MicCue mic && mic.InputDeviceId.Length > 0 && !inputs.Contains(mic.InputDeviceId))
                Add(CheckLevel.Error, c, "L'entrada de micròfon triada no està connectada.");

            // Encaminament
            if (c is IAudioSource src)
            {
                foreach (var s in src.Routing.Sends)
                {
                    if (s.OutputId == null || !outputIds.Contains(s.OutputId.Value))
                        Add(CheckLevel.Error, c, "Un enviament apunta a una sortida que no existeix.");
                    else if (s.OutputId == src.OutputId)
                        Add(CheckLevel.Warning, c, "Un enviament va a la mateixa sortida que la principal: sonarà doble.");
                }
                if (src.VolumeDb > 6) Add(CheckLevel.Warning, c, $"Volum molt alt ({src.VolumeDb:+0.0} dB).");
                if (src.Routing.Crosspoints.Count > 0 && src.Routing.Crosspoints.All(p => p.Db <= AudioMath.SilenceDb))
                    Add(CheckLevel.Error, c, "La matriu de la sortida principal no envia res: no sonarà.");
            }

            // Cues buides o amb el destí perdut
            switch (c)
            {
                case FadeCue f:
                    var target = cues.FirstOrDefault(x => x.Id == f.TargetId);
                    if (target == null) Add(CheckLevel.Error, c, "La fosa no té cue destí (o s'ha esborrat).");
                    else if (target is not MediaCue) Add(CheckLevel.Error, c, "La cue destí de la fosa no és de fitxer, micròfon ni text.");
                    else if (cues.IndexOf(target) > i) Add(CheckLevel.Warning, c, $"La fosa va abans de la seva cue destí ({target.DisplayName}): quan es dispari, encara no sonarà.");
                    break;
                case ControlCue k:
                    if (!k.AllCues && (k.TargetId == null || !ids.Contains(k.TargetId.Value)))
                        Add(CheckLevel.Error, c, "No té cue destí (o s'ha esborrat).");
                    if (k.HasIgnoredTarget) Add(CheckLevel.Warning, c, "Té «Totes les cues» marcat: actuarà sobre totes, no només sobre la destí.");
                    break;
                case GroupCue g:
                    if (ShowTree.Children(cues, g).Count == 0) Add(CheckLevel.Warning, c, "El grup és buit: arrossega-hi cues (o selecciona'n unes quantes i prem «+ Grup»).");
                    if (g.IsPlaylist && g.LoopPlaylist && ShowTree.Children(cues, g).All(x => x.Duration <= 0 && !IsEndless(x)))
                        Add(CheckLevel.Error, c, "Llista en bucle sense cap cue amb durada: es repetiria sense parar.");
                    break;
            }
            if (ShowTree.Parent(cues, c) is GroupCue { Mode: GroupMode.Timeline or GroupMode.Playlist or GroupMode.StartRandom } parentGroup
                && c.NextAction != NextAction.None)
                Add(CheckLevel.Warning, c, $"És dins del grup «{parentGroup.Name}», que decideix quan sona: el seu «Continua» o «Segueix» no fa res.");

            // Encadenaments perillosos
            if (c.NextAction != NextAction.None && next == null)
                Add(CheckLevel.Warning, c, "És l'última cue però té «Continua» o «Segueix»: no hi ha res després.");
            if (c.NextAction == NextAction.AutoFollow && IsEndless(c))
                Add(CheckLevel.Warning, c, "Té «Segueix», però no s'acaba mai sola (bucle o sense temps): la següent no es dispararà.");
            if (c.NextAction == NextAction.AutoContinue && c.PostWait < 0.05 && next is ControlCue { Action: ControlAction.Stop, AllCues: true })
                Add(CheckLevel.Error, c, $"Dispara de seguida «{next.Name}», que ho atura tot: aquesta cue s'aturarà en començar.");
            if (c is PlayableCue { Loop: true } && c.NextAction == NextAction.None && !cues.Skip(i + 1).Any(x => Stops(x, c)))
                Add(CheckLevel.Info, c, "Està en bucle i cap cue posterior no l'atura: caldrà aturar-la a mà (o amb Esc).");

            // Tecles
            if (c.Hotkey.Length > 0 && ShowController.ReservedKeys.Contains(c.Hotkey))
                Add(CheckLevel.Error, c, $"La tecla {c.Hotkey} ja la fa servir Escena.");
        }

        foreach (var dup in cues.Where(c => c.Hotkey.Length > 0).GroupBy(c => c.Hotkey, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
            Add(CheckLevel.Warning, dup.First(), $"La tecla {dup.Key} dispara {dup.Count()} cues alhora: " + string.Join(", ", dup.Select(c => c.DisplayName)));
        if (show.FindDuplicateNumber() is { } number) Add(CheckLevel.Warning, null, $"Hi ha més d'una cue amb el número «{number}».");
        if (cues.Count == 0) Add(CheckLevel.Info, null, "L'espectacle no té cap cue.");
        if (show.IsDirty) Add(CheckLevel.Info, null, "Hi ha canvis sense desar.");
        return issues.OrderBy(x => x.Level).ToList();
    }

    private static bool IsEndless(Cue c) => c switch
    {
        PlayableCue { Loop: true } => true,
        ImageCue { DisplayTime: <= 0 } => true,
        TextCue { DisplayTime: <= 0 } => true,
        MicCue => true,
        _ => false,
    };

    private static bool Stops(Cue x, Cue target) => x switch
    {
        ControlCue { Action: ControlAction.Stop } k => k.AllCues || k.TargetId == target.Id,
        FadeCue { StopTarget: true } f => f.TargetId == target.Id,
        _ => false,
    };

}

/// <summary>Mostra el resultat de la comprovació. Doble clic en un problema selecciona la cue.</summary>
public sealed class CheckWindow : Window
{
    private readonly ShowController _show;
    private readonly ListView _list = new();
    private readonly TextBlock _summary = new() { FontSize = 15, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 8) };

    public CheckWindow(ShowController show)
    {
        _show = show;
        Ui.StyleWindow(this, "Comprovació abans de la funció", 820, 520);
        var root = new DockPanel { Margin = new Thickness(16) };
        var top = new StackPanel();
        top.Children.Add(Ui.Header("COMPROVACIÓ ABANS DE LA FUNCIÓ"));
        top.Children.Add(_summary);
        DockPanel.SetDock(top, Dock.Top);
        root.Children.Add(top);

        var buttons = Ui.Buttons(Ui.Button("Torna a comprovar", (_, _) => Refresh()), Ui.Button("Tanca", (_, _) => Close(), 100));
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(buttons);

        var view = new GridView();
        view.Columns.Add(new GridViewColumn { Header = "", Width = 34, DisplayMemberBinding = new System.Windows.Data.Binding(nameof(CheckIssue.Icon)) });
        view.Columns.Add(new GridViewColumn { Header = "Cue", Width = 200, DisplayMemberBinding = new System.Windows.Data.Binding(nameof(CheckIssue.CueText)) });
        view.Columns.Add(new GridViewColumn { Header = "Problema", Width = 540, DisplayMemberBinding = new System.Windows.Data.Binding(nameof(CheckIssue.Message)) });
        _list.View = view;
        _list.Background = Ui.Brush("PanelBrush");
        _list.Foreground = Ui.Brush("TextBrush");
        _list.FontSize = 13;
        _list.MouseDoubleClick += (_, _) =>
        {
            if (_list.SelectedItem is CheckIssue { Cue: { } cue }) _show.SelectedCue = cue;
        };
        root.Children.Add(_list);
        Content = root;
        Refresh();
    }

    private void Refresh()
    {
        var issues = ShowCheck.Run(_show);
        _list.ItemsSource = issues;
        int errors = issues.Count(i => i.Level == CheckLevel.Error), warnings = issues.Count(i => i.Level == CheckLevel.Warning);
        _summary.Text = errors + warnings == 0
            ? "✓ Tot correcte. " + (issues.Count > 0 ? "Hi ha alguna nota informativa." : "")
            : $"{errors} error(s) i {warnings} avís(os). Doble clic en una línia per anar a la cue.";
        _summary.Foreground = errors > 0 ? Ui.Brush("ErrorBrush") : warnings > 0 ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xF0, 0xB4, 0x29)) : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x4C, 0xC2, 0x6F));
    }
}
