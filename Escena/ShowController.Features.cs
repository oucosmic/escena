using Escena.Audio;
using Escena.Model;
using Escena.Video;

namespace Escena;

/// <summary>
/// Funcions del full de ruta que van més enllà del GO bàsic: enviaments a diverses sortides, ducking,
/// modes de grup, tecles per a cues concretes, «Comença des de…», empaquetar i informació per al regidor.
/// </summary>
public sealed partial class ShowController
{
    // ================= Reproducció amb matriu i enviaments =================

    /// <summary>Avisos de l'última obertura de sortides (per a la comprovació abans de la funció).</summary>
    public List<string> OutputWarnings { get; } = new();

    /// <summary>
    /// Reprodueix una cue de fitxer o de micròfon. Les d'àudio sonen per la sortida principal (amb la seva matriu)
    /// i per cada enviament (amb el seu nivell i matriu); es controlen totes juntes.
    /// </summary>
    private IMediaPlayback PlayMedia(MediaCue media, double offset)
    {
        IMediaPlayback playback;
        if (media is IAudioSource source)
        {
            var main = _outputManager.Resolve(media) as AudioOutput
                       ?? throw new InvalidOperationException("No hi ha cap sortida d'àudio oberta per a aquesta cue.");
            var parts = new List<IMediaPlayback> { main.PlayRouted(source, source.Routing.Crosspoints, 0, offset) };
            var problems = new List<string>();
            foreach (var send in source.Routing.Sends)
            {
                if (send.OutputId == null) continue;
                if (_outputManager.Find(send.OutputId) is not AudioOutput output)
                {
                    problems.Add($"l'enviament a «{Outputs.FirstOrDefault(o => o.Id == send.OutputId)?.Name ?? "?"}» no té la sortida oberta");
                    continue;
                }
                try { parts.Add(output.PlayRouted(source, send.Crosspoints, send.LevelDb, offset)); }
                catch (Exception ex) { problems.Add($"«{output.Name}»: {ex.Message}"); }
            }
            if (problems.Count > 0) Status = $"Atenció a {media.DisplayName}: " + string.Join("; ", problems);
            playback = parts.Count == 1 ? parts[0] : new MultiPlayback(parts);
        }
        else
        {
            var output = _outputManager.Resolve(media) ?? throw new InvalidOperationException("No hi ha cap sortida oberta per a aquesta cue.");
            playback = output.Play(media, offset);
        }

        // Si ja hi ha una cue fent ducking, la nova comença ja abaixada.
        if (!IsDucker(media))
        {
            double duck = DuckTarget(media, out _);
            if (duck < 1)
            {
                playback.SetDuck(duck, 0);
                _ducked[media] = duck;
            }
        }
        return playback;
    }

    /// <summary>Canals oberts d'una sortida d'àudio (per editar la matriu).</summary>
    public int GetOutputChannels(Guid? outputId)
    {
        var output = _outputManager.Find(outputId) as AudioOutput ?? _outputManager.Outputs.OfType<AudioOutput>().FirstOrDefault();
        return output?.Channels ?? 2;
    }

    /// <summary>To de prova en un canal d'una sortida (per saber quin altaveu és).</summary>
    public void PlayChannelTone(Guid? outputId, int channel)
    {
        var output = _outputManager.Find(outputId) as AudioOutput ?? _outputManager.Outputs.OfType<AudioOutput>().FirstOrDefault();
        output?.PlayChannelTone(channel);
    }

    /// <summary>La sortida de vídeo oberta (per ajustar la correcció del projector en directe).</summary>
    public VideoOutput? FindVideoOutput(Guid id) => _outputManager.Find(id) as VideoOutput;

    // ================= Ducking =================

    private readonly Dictionary<Cue, double> _ducked = new();
    private double _lastDuckFade = 1;

    private static bool IsDucker(Cue cue) => cue is IAudioSource { Routing.DuckOthers: true };

    /// <summary>Factor de ducking que li toca a una cue: el més baix de les cues que fan ducking i sonen.</summary>
    private double DuckTarget(Cue exclude, out double fade)
    {
        double gain = 1;
        fade = _lastDuckFade;
        foreach (var c in ActiveCues)
        {
            if (c == exclude || c.State != CueState.Running || c is not IAudioSource { Routing.DuckOthers: true } s) continue;
            double g = AudioMath.DbToGain(s.Routing.DuckDb);
            if (g <= gain)
            {
                gain = g;
                fade = s.Routing.DuckFade;
            }
        }
        if (gain < 1) _lastDuckFade = fade;
        return gain;
    }

    private void UpdateDucking()
    {
        foreach (var (media, playback) in _playbacks)
        {
            if (IsDucker(media)) continue; // les cues que fan ducking no s'abaixen entre elles
            double target = DuckTarget(media, out double fade);
            double current = _ducked.TryGetValue(media, out var d) ? d : 1;
            if (Math.Abs(current - target) < 1e-4) continue;
            playback.SetDuck(target, fade);
            _ducked[media] = target;
        }
    }

    // ================= Modes de grup (com al QLab) =================

    private sealed class GroupRun
    {
        public required List<Cue> Order;
        public int Next;
        public Cue? Current;
    }

    private readonly Dictionary<GroupCue, GroupRun> _groupRuns = new();
    private readonly Dictionary<GroupCue, HashSet<Guid>> _randomPlayed = new();
    private readonly Random _random = new();

    /// <summary>Fills directes d'un grup, en l'ordre de la llista.</summary>
    public List<Cue> GroupMembersOf(GroupCue group) => ShowTree.Children(Cues, group);

    private List<Cue> Shuffled(List<Cue> list) => list.OrderBy(_ => _random.Next()).ToList();

    private void BeginGroupRun(GroupCue group)
    {
        var children = GroupMembersOf(group);
        var run = new GroupRun { Order = group.IsPlaylist && group.Shuffle ? Shuffled(children) : children };
        _groupRuns[group] = run;
        if (children.Count == 0) return;
        switch (group.Mode)
        {
            case GroupMode.Playlist:
                AdvancePlaylist(group, run);
                break;
            case GroupMode.StartFirst:
            case GroupMode.StartFirstAndEnter:
                Trigger(children[0], "grup");
                break;
            case GroupMode.StartRandom:
                var played = _randomPlayed.TryGetValue(group, out var p) ? p : _randomPlayed[group] = new HashSet<Guid>();
                var candidates = children.Where(c => !played.Contains(c.Id) && !c.IsActive).ToList();
                if (candidates.Count == 0)
                {
                    played.Clear();
                    candidates = children.Where(c => !c.IsActive).ToList();
                }
                if (candidates.Count == 0) break;
                var pick = candidates[_random.Next(candidates.Count)];
                played.Add(pick.Id);
                Trigger(pick, "grup");
                break;
            default: // línia de temps: totes alhora, cadascuna amb la seva pre-espera
                foreach (var c in children) Trigger(c, "grup");
                break;
        }
    }

    /// <summary>Llista: dispara la fill següent. Retorna fals si ja no en queden.</summary>
    private bool AdvancePlaylist(GroupCue group, GroupRun run)
    {
        for (int guard = 0; guard <= run.Order.Count; guard++)
        {
            if (run.Next >= run.Order.Count)
            {
                if (!group.LoopPlaylist || run.Order.Count == 0 || guard > 0) return false;
                run.Next = 0;
                if (group.Shuffle) run.Order = Shuffled(run.Order);
            }
            run.Current = run.Order[run.Next++];
            Trigger(run.Current, "grup");
            if (run.Current.IsActive) return true; // si ha fallat de seguida, passa a la següent
        }
        return false;
    }

    /// <summary>El grup està en marxa mentre alguna de les seves cues (a qualsevol nivell) ho està.</summary>
    private void UpdateGroupRun(GroupCue group, double dt)
    {
        string paused = group.IsPaused ? "Pausa  " : "";
        var descendants = ShowTree.Subtree(Cues, group).Skip(1).ToList();
        int running = descendants.Count(c => c.IsActive && c is not GroupCue);
        if (group.IsPlaylist && _groupRuns.TryGetValue(group, out var run))
        {
            if (run.Current == null || !run.Current.IsActive)
            {
                if (group.IsPaused) return;
                if (!AdvancePlaylist(group, run))
                {
                    FinishCue(group, natural: true);
                    return;
                }
            }
            group.StatusText = $"{paused}{run.Next}/{run.Order.Count}: {run.Current?.Name}";
            return;
        }
        group.StatusText = $"{paused}{running} en marxa";
        if (!descendants.Any(c => c.IsActive)) FinishCue(group, natural: true);
    }

    /// <summary>Aturar un grup atura tot el que conté.</summary>
    private void StopGroupMembers(GroupCue group, double? fade)
    {
        _groupRuns.Remove(group);
        foreach (var c in ShowTree.Subtree(Cues, group).Skip(1).Where(c => c.IsActive).ToList())
        {
            if (c is GroupCue g) _groupRuns.Remove(g);
            if (c.IsActive) StopCue(c, fade);
        }
    }

    private void PauseGroupMembers(GroupCue group, bool pause)
    {
        foreach (var c in ShowTree.Children(Cues, group).Where(c => c.IsActive).ToList())
        {
            if (pause) PauseCue(c); else ResumeCue(c);
        }
    }

    // ================= Tecles per a cues concretes =================

    /// <summary>Tecles que ja fa servir Escena i no es poden assignar a cues.</summary>
    public static readonly HashSet<string> ReservedKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "Space", "Escape", "Shift+Escape", "P", "E", "Enter", "Up", "Down", "Shift+Up", "Shift+Down", "Delete",
        "F5", "F6", "F11", "Tab", "Ctrl+N", "Ctrl+O", "Ctrl+S", "Ctrl+Up", "Ctrl+Down", "Ctrl+Shift+S", "Ctrl+G",
        "Ctrl+C", "Ctrl+X", "Ctrl+V", "Ctrl+D",
    };

    /// <summary>Dispara les cues que tenen aquesta tecla. Retorna si n'hi havia cap.</summary>
    public bool FireHotkey(string key)
    {
        var cues = Cues.Where(c => c.Hotkey.Length > 0 && c.Hotkey.Equals(key, StringComparison.OrdinalIgnoreCase)).ToList();
        if (cues.Count == 0) return false;
        foreach (var c in cues) Trigger(c, "tecla");
        Status = $"Tecla {key}: " + string.Join(", ", cues.Select(c => c.DisplayName));
        return true;
    }

    // ================= Registre d'assaig =================

    public RehearsalLog Log { get; } = new();

    /// <summary>
    /// Del registre: la cue anterior passa a disparar aquesta sola, amb la post-espera que hi va haver
    /// entre els dos GO (per ajustar els temps amb dades reals).
    /// </summary>
    public string ApplyLoggedTiming(LogEntry entry)
    {
        int i = Log.Entries.IndexOf(entry);
        if (i <= 0) return "Cal una entrada anterior al registre.";
        var previous = Log.Entries[i - 1];
        var a = Cues.FirstOrDefault(c => c.Id == previous.CueId);
        var b = Cues.FirstOrDefault(c => c.Id == entry.CueId);
        if (a == null || b == null) return "Alguna de les dues cues ja no és a l'espectacle.";
        if (Cues.IndexOf(a) + 1 != Cues.IndexOf(b))
            return $"{b.DisplayName} no va just després de {a.DisplayName} a la llista: no es pot encadenar.";
        double delta = Math.Round((entry.Time - previous.Time).TotalSeconds, 1);
        a.NextAction = NextAction.AutoContinue;
        a.PostWait = delta;
        return $"{a.DisplayName} ara dispara {b.DisplayName} sola al cap de {delta:0.0} s (Continua + post-espera).";
    }

    // ================= Vista de regidor =================

    private string _stageCurrent = "—", _stageRemaining = "", _stageClock = "";
    private double _stageElapsed;

    /// <summary>Cue en curs per a la vista de regidor (l'última disparada que encara sona).</summary>
    public string StageCurrent { get => _stageCurrent; private set => Set(ref _stageCurrent, value); }

    /// <summary>Temps que li queda a la cue en curs (o el que porta, si no té durada).</summary>
    public string StageRemaining { get => _stageRemaining; private set => Set(ref _stageRemaining, value); }

    public string StageClock { get => _stageClock; private set => Set(ref _stageClock, value); }

    private void UpdateStageInfo(double dt)
    {
        _stageElapsed += dt;
        if (_stageElapsed < 0.1) return;
        _stageElapsed = 0;
        var current = _lastFired is { IsActive: true } ? _lastFired : ActiveCues.LastOrDefault();
        StageCurrent = current?.DisplayName ?? "—";
        StageRemaining = current switch
        {
            null => "",
            { State: CueState.PreWait } c => $"espera {Math.Max(0, c.PreWait - c.WaitElapsed):0.0} s",
            { Duration: > 0 } c => "−" + TimeFormat.Format(Math.Max(0, c.Duration - c.Elapsed)),
            var c => TimeFormat.Format(c.Elapsed),
        };
        StageClock = DateTime.Now.ToString("HH:mm:ss");
    }

    // ================= Empaquetar =================

    /// <summary>
    /// Copia l'espectacle i tots els seus fitxers a <paramref name="destDir"/> (fitxers a «Fitxers\»),
    /// fa que les cues apuntin a les còpies i desa l'espectacle allà.
    /// </summary>
    public (int Copied, int Missing, string ShowPath) PackShow(string destDir)
    {
        string mediaDir = Path.Combine(destDir, "Fitxers");
        Directory.CreateDirectory(mediaDir);
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int copied = 0, missing = 0;
        foreach (var cue in Cues.OfType<MediaCue>().ToList())
        {
            if (string.IsNullOrWhiteSpace(cue.FilePath)) continue;
            string source = Path.GetFullPath(cue.FilePath);
            if (!File.Exists(source))
            {
                missing++;
                continue;
            }
            if (!map.TryGetValue(source, out var dest))
            {
                string name = Path.GetFileName(source), candidate = name;
                for (int n = 2; used.Contains(candidate); n++)
                    candidate = $"{Path.GetFileNameWithoutExtension(name)} ({n}){Path.GetExtension(name)}";
                used.Add(candidate);
                dest = Path.Combine(mediaDir, candidate);
                if (!string.Equals(Path.GetFullPath(dest), source, StringComparison.OrdinalIgnoreCase))
                {
                    File.Copy(source, dest, overwrite: true);
                    copied++;
                }
                map[source] = dest;
            }
            if (!string.Equals(cue.FilePath, dest, StringComparison.OrdinalIgnoreCase)) cue.FilePath = dest;
        }
        string showName = FilePath != null ? Path.GetFileName(FilePath) : "Espectacle" + ShowFile.Extension;
        string showPath = Path.Combine(destDir, showName);
        Save(showPath);
        Status = $"Empaquetat a {destDir}: {copied} fitxers copiats" + (missing > 0 ? $", {missing} no es troben." : ".");
        return (copied, missing, showPath);
    }

    // ================= Començar des d'un temps =================

    private sealed class SimEntry
    {
        public required Cue Cue;
        public double Fire, Start, End;
        public SimEntry? Parent;
        public bool ViaFollow;
    }

    private sealed class Delayed
    {
        public required Cue Cue;
        public double Remaining;
    }

    private readonly List<Delayed> _delayed = new();

    private void UpdateDelayed(double dt)
    {
        if (IsAllPaused || _delayed.Count == 0) return;
        foreach (var d in _delayed.ToList())
        {
            d.Remaining -= dt;
            if (d.Remaining > 0) continue;
            _delayed.Remove(d);
            Trigger(d.Cue, "auto");
        }
    }

    private static double SimDuration(Cue c) => c switch
    {
        PlayableCue { Loop: true } => double.PositiveInfinity,
        PlayableCue p => p.Duration > 0 ? p.Duration : double.PositiveInfinity,
        ImageCue i => i.DisplayTime > 0 ? i.DisplayTime : double.PositiveInfinity,
        TextCue t => t.DisplayTime > 0 ? t.DisplayTime : double.PositiveInfinity,
        MicCue => double.PositiveInfinity,
        GroupCue => 0,
        _ => c.Duration,
    };

    /// <summary>Calcula quan es dispararia, començaria i acabaria cada cue de la cadena automàtica que comença a <paramref name="first"/>.</summary>
    private List<SimEntry> Simulate(Cue first)
    {
        var list = new List<SimEntry>();

        SimEntry Fire(Cue c, double t, SimEntry? parent, bool viaFollow, int depth)
        {
            var e = new SimEntry { Cue = c, Fire = t, Start = t + c.PreWait, Parent = parent, ViaFollow = viaFollow };
            e.End = e.Start + SimDuration(c);
            list.Add(e);
            if (list.Count > 500 || depth > 60) return e;

            if (c is GroupCue g)
            {
                var members = GroupMembersOf(g);
                double end = e.Start;
                switch (g.Mode)
                {
                    case GroupMode.Playlist:
                        double t2 = e.Start;
                        foreach (var m in members)
                        {
                            t2 = Fire(m, t2, e, false, depth + 1).End;
                            if (double.IsInfinity(t2)) break;
                        }
                        end = g.LoopPlaylist ? double.PositiveInfinity : t2;
                        break;
                    case GroupMode.Timeline:
                        foreach (var m in members) end = Math.Max(end, Fire(m, e.Start, e, false, depth + 1).End);
                        break;
                    default:
                        // Primera (o una a l'atzar): la simulació agafa la primera; la resta segueix els seus encadenaments.
                        if (members.Count > 0) end = Fire(members[0], e.Start, e, false, depth + 1).End;
                        break;
                }
                e.End = end;
            }

            var siblings = ShowTree.Siblings(Cues, c);
            int i = siblings.IndexOf(c);
            if (i >= 0 && i + 1 < siblings.Count && ChainsAllowed(c))
            {
                if (c.NextAction == NextAction.AutoContinue)
                    Fire(siblings[i + 1], e.Start + c.PostWait, e, false, depth + 1);
                else if (c.NextAction == NextAction.AutoFollow && !double.IsInfinity(e.End))
                    Fire(siblings[i + 1], e.End + c.PostWait, e, true, depth + 1);
            }
            return e;
        }

        Fire(first, 0, null, false, 0);

        // Aturades: escurcen les cues afectades.
        foreach (var s in list.OrderBy(x => x.Start).ToList())
        {
            switch (s.Cue)
            {
                case ControlCue { Action: ControlAction.Stop } stop:
                    foreach (var e in list.Where(e => e != s && e.Start <= s.Start && e.End > s.Start
                                                      && (stop.AllCues || e.Cue.Id == stop.TargetId)))
                        e.End = s.Start;
                    break;
                case FadeCue { StopTarget: true } fade:
                    foreach (var e in list.Where(e => e.Cue.Id == fade.TargetId && e.Start <= s.Start && e.End > s.Start))
                        e.End = Math.Min(e.End, s.Start + fade.FadeTime);
                    break;
            }
        }
        return list;
    }

    /// <summary>
    /// «Comença des de…»: posa l'espectacle tal com estaria <paramref name="seconds"/> segons després del GO
    /// de <paramref name="first"/>, seguint els encadenaments automàtics (cues en marxa a mitges, foses ja fetes,
    /// esperes) i deixa en espera la cue que tocaria després. Amb <paramref name="includeEarlier"/>, també
    /// engega les cues anteriors en bucle (o sense final) que encara sonarien.
    /// </summary>
    public int StartFrom(Cue first, double seconds, bool includeEarlier)
    {
        StopAll(0);
        StopPreview();
        double T = Math.Max(0, seconds);
        var sim = Simulate(first);
        var fired = sim.Where(e => e.Fire <= T).GroupBy(e => e.Cue).Select(g => g.OrderBy(e => e.Fire).Last()).ToList();
        var mid = fired.Where(e => e.Start <= T && e.End > T).ToList();
        var midSet = mid.ToHashSet();
        int started = 0;

        // 1. Cues de fitxer a mitges (amb el nivell que els hauran deixat les foses).
        foreach (var e in mid.Where(e => e.Cue is MediaCue))
        {
            var (fader, pan) = LevelsAt(sim, e.Cue, T);
            if (StartCueMid(e.Cue, T - e.Start, fader, pan)) started++;
        }
        // 2. Cues anteriors que encara sonarien.
        if (includeEarlier)
            foreach (var c in EarlierStillPlaying(first))
                if (sim.All(e => e.Cue != c))
                {
                    var (fader, pan) = EarlierLevels(c, first);
                    if (StartCueMid(c, 0, fader, pan)) started++;
                }
        // 3. Foses i esperes a mitges.
        foreach (var e in mid.Where(e => e.Cue is FadeCue or WaitCue))
            if (StartCueMid(e.Cue, T - e.Start, 1, null)) started++;
        // 4. Cues en pre-espera.
        foreach (var e in fired.Where(e => e.Start > T))
        {
            var cue = e.Cue;
            Log.Add(cue, "des de");
            cue.IsPaused = false;
            cue.Elapsed = 0;
            cue.WaitElapsed = T - e.Fire;
            cue.State = CueState.PreWait;
            Activate(cue);
            started++;
        }
        // 5. Cues que encara s'han de disparar: les fills de cues engegades a mitges o ja acabades.
        foreach (var e in sim.Where(e => e.Fire > T && e.Parent != null))
        {
            var parent = e.Parent!;
            bool parentNatural = parent.Fire <= T && parent.Start > T || parent.Fire > T;
            if (parentNatural) continue; // el motor ja la dispararà
            bool engineFollows = e.ViaFollow && midSet.Contains(parent) && parent.Cue is not GroupCue && !double.IsInfinity(parent.End);
            if (engineFollows) continue;
            _delayed.Add(new Delayed { Cue = e.Cue, Remaining = e.Fire - T });
        }

        // En espera: la cue de després de l'última de la cadena.
        var lastInChain = sim.Where(e => e.Parent == null || e.Parent.Cue is not GroupCue).MaxBy(e => Cues.IndexOf(e.Cue))!.Cue;
        StandbyCue = NextStandbyAfter(lastInChain);
        _lastFired = first;
        Status = $"Començat des de {first.DisplayName} a {TimeFormat.Format(T)}: {started} cues en marxa"
                 + (_delayed.Count > 0 ? $", {_delayed.Count} per disparar" : "")
                 + $". En espera: {StandbyCue?.DisplayName ?? "final de la llista"}.";
        return started;
    }

    /// <summary>Nivell (fader lineal) i panorama d'una cue al temps T segons les foses de la simulació.</summary>
    private (double Fader, double? Pan) LevelsAt(List<SimEntry> sim, Cue target, double T)
    {
        double fader = 1;
        double? pan = null;
        if (target is not MediaCue media) return (fader, pan);
        double basePan = target is IAudioSource a ? a.Pan : 0;
        foreach (var e in sim.Where(e => e.Cue is FadeCue f && f.TargetId == target.Id && e.Start <= T).OrderBy(e => e.Start))
        {
            var f = (FadeCue)e.Cue;
            double t = f.FadeTime > 0 ? Math.Clamp((T - e.Start) / f.FadeTime, 0, 1) : 1;
            if (t >= 1 || f.FadeTime <= 0)
            {
                if (f.FadeVolume || !f.FadePan) fader = FaderFor(media, f.TargetVolumeDb);
                if (f.FadePan) pan = f.TargetPan;
            }
            else
            {
                // Fosa a mitges: el valor d'aquest moment; la cue de fosa farà la resta.
                if (f.FadeVolume || !f.FadePan) fader = FadeCurves.Interpolate(f.Curve, fader, FaderFor(media, f.TargetVolumeDb), t);
                if (f.FadePan) pan = (pan ?? basePan) + (f.TargetPan - (pan ?? basePan)) * FadeCurves.Apply(f.Curve, t);
            }
        }
        return (fader, pan);
    }

    private static double FaderFor(MediaCue target, double wantedDb)
    {
        double master = AudioMath.DbToGain(target.BaseGainDb);
        return master > 0 ? Math.Min(AudioMath.DbToGain(wantedDb) / master, 16) : 0;
    }

    /// <summary>Cues de fitxer anteriors a <paramref name="first"/> sense final (bucle, imatge fixa, micròfon) que cap cue no ha aturat.</summary>
    private List<Cue> EarlierStillPlaying(Cue first)
    {
        int index = Cues.IndexOf(first);
        var result = new List<Cue>();
        for (int i = 0; i < index; i++)
        {
            if (Cues[i] is not MediaCue m || !double.IsInfinity(SimDuration(m))) continue;
            bool stopped = Cues.Skip(i + 1).Take(index - i - 1).Any(c => c switch
            {
                ControlCue { Action: ControlAction.Stop } s => s.AllCues || s.TargetId == m.Id,
                FadeCue { StopTarget: true } f => f.TargetId == m.Id,
                _ => false,
            });
            if (!stopped) result.Add(m);
        }
        return result;
    }

    private (double Fader, double? Pan) EarlierLevels(Cue target, Cue first)
    {
        double fader = 1;
        double? pan = null;
        int from = Cues.IndexOf(target), to = Cues.IndexOf(first);
        foreach (var f in Cues.Skip(from + 1).Take(to - from - 1).OfType<FadeCue>().Where(f => f.TargetId == target.Id))
        {
            if (f.FadeVolume || !f.FadePan) fader = FaderFor((MediaCue)target, f.TargetVolumeDb);
            if (f.FadePan) pan = f.TargetPan;
        }
        return (fader, pan);
    }

    /// <summary>Engega una cue com si ja portés <paramref name="offset"/> segons en marxa (sense disparar encadenaments).</summary>
    private bool StartCueMid(Cue cue, double offset, double fader, double? pan)
    {
        Log.Add(cue, "des de");
        cue.IsPaused = false;
        cue.WaitElapsed = offset;
        cue.Elapsed = offset;
        if (cue.State == CueState.Error) cue.State = CueState.Idle;
        try
        {
            switch (cue)
            {
                case MediaCue media:
                    var pb = PlayMedia(media, offset);
                    _playbacks[media] = pb;
                    if (Math.Abs(fader - 1) > 1e-6) pb.RampTo((float)fader, 0, FadeCurve.Linear, false);
                    if (pan != null) pb.RampPan(pan.Value, 0, FadeCurve.Linear, false);
                    media.ErrorMessage = null;
                    break;
                case FadeCue fade:
                    ApplyFade(fade, Math.Max(0, fade.FadeTime - offset));
                    break;
                case GroupCue:
                    return false;
            }
        }
        catch (Exception ex)
        {
            cue.State = CueState.Error;
            cue.ErrorMessage = ex.Message;
            return false;
        }
        cue.State = CueState.Running;
        Activate(cue);
        return true;
    }
}
