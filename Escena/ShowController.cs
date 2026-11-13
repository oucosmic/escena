using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows.Threading;
using Escena.Audio;
using Escena.Model;

namespace Escena;

public enum LayoutMode { List, Cart }

/// <summary>On es deixa una cue arrossegada respecte d'una altra.</summary>
public enum DropPosition { Before, After, Into }

/// <summary>
/// Cervell de l'espectacle: llista de cues, GO, pre/post-espera, encadenaments, pausa i aturades.
/// Hi ha dos cursors independents: la cue <b>en espera</b> (la que dispara el GO) i la cue
/// <b>seleccionada</b> (la que s'edita), de manera que es pot editar mentre corre l'espectacle.
/// Només coneix <see cref="Cue"/>, <see cref="MediaCue"/> i les cues d'acció; no sap si un fitxer és àudio o vídeo.
/// Tot s'executa al fil de la interfície amb un rellotge de 15 ms; l'àudio va pel seu fil.
/// </summary>
public sealed partial class ShowController : ObservableObject, IDisposable
{
    private sealed class PendingGo
    {
        public required Cue Source;
        public double Remaining;
    }

    private sealed class Retired
    {
        public required IMediaPlayback Playback;
        public double Age;
    }

    private const int MaxGroupDepth = 8;

    private readonly OutputManager _outputManager = new();
    private readonly Dictionary<MediaCue, IMediaPlayback> _playbacks = new();
    private readonly List<Retired> _retired = new();
    private readonly List<PendingGo> _pending = new();
    private readonly DispatcherTimer _timer;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private double _lastTick, _lastGo = double.MinValue / 2;
    private DateTime _lastStopAll = DateTime.MinValue, _lastPanic = DateTime.MinValue;
    private int _groupDepth;

    private Cue? _selectedCue, _standbyCue, _lastFired;
    private bool _allPaused, _isDirty;
    private double _masterVolumeDb;
    private string? _filePath;
    private string _status = "Llest.";
    private string _outputSummary = "";
    private LayoutMode _layout = LayoutMode.List;

    // Recuperació
    private bool _sessionChanged = true, _hasRecoveryCopy;
    private double _autosaveElapsed, _sessionElapsed;

    // Escolta pels auriculars
    private AudioOutput? _previewOutput;
    private IMediaPlayback? _preview;
    private Cue? _previewCue;
    private string _previewStatus = "";

    public ShowController()
    {
        Cues.CollectionChanged += OnCuesChanged;
        Settings.PropertyChanged += (_, _) => IsDirty = true;
        AudioOutputs = new System.Windows.Data.ListCollectionView(Outputs) { Filter = o => o is AudioOutputConfig };
        VideoOutputs = new System.Windows.Data.ListCollectionView(Outputs) { Filter = o => o is VideoOutputConfig };
        VideoAudioChoices.Add(new OutputChoice(Guid.Empty, L.T("Predeterminat de Windows")));
        Outputs.CollectionChanged += (_, _) => SyncVideoAudioChoices();
        VisibleCues = new System.Windows.Data.ListCollectionView(Cues) { Filter = c => !((Cue)c).IsHiddenByGroup };
        VisibleCues.LiveFilteringProperties.Add(nameof(Cue.IsHiddenByGroup));
        VisibleCues.IsLiveFiltering = true; // plegar un grup amaga les fills sense refer la llista
        _timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(15) };
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
        WatchDisplays();
    }

    public ObservableCollection<Cue> Cues { get; } = new();

    /// <summary>Cues en pre-espera, sonant o aturant-se (panell «En curs»).</summary>
    public ObservableCollection<Cue> ActiveCues { get; } = new();

    public ObservableCollection<OutputConfig> Outputs { get; } = new();

    /// <summary>Vistes filtrades per als desplegables de sortida de les cues.</summary>
    public ICollectionView AudioOutputs { get; }
    public ICollectionView VideoOutputs { get; }

    /// <summary>
    /// Per on pot sortir el so d'un vídeo: el predeterminat de Windows (primer) i les sortides d'àudio de l'espectacle.
    /// Es manté sense refer-la, perquè els desplegables no perdin la selecció.
    /// </summary>
    public ObservableCollection<object> VideoAudioChoices { get; } = new();

    private void SyncVideoAudioChoices()
    {
        var wanted = Outputs.OfType<AudioOutputConfig>().ToList();
        for (int i = VideoAudioChoices.Count - 1; i >= 1; i--)
            if (!wanted.Contains(VideoAudioChoices[i])) VideoAudioChoices.RemoveAt(i);
        foreach (var o in wanted)
            if (!VideoAudioChoices.Contains(o)) VideoAudioChoices.Add(o);
    }

    /// <summary>La llista tal com es veu: sense les fills dels grups plegats.</summary>
    public System.Windows.Data.ListCollectionView VisibleCues { get; }

    /// <summary>Fills de la cue seleccionada, si és un grup (per al panell de propietats).</summary>
    public ObservableCollection<Cue> GroupMembers { get; } = new();

    public ShowSettings Settings { get; } = new();

    /// <summary>Entrades d'àudio (per a les cues de micròfon).</summary>
    public List<AudioDevice> InputDevices => AudioDevices.Inputs();

    /// <summary>Ajustos d'aquest ordinador (sortida d'auriculars).</summary>
    public AppSettings MachineSettings { get; } = AppSettings.Load();

    /// <summary>La cue que s'edita al panell de propietats.</summary>
    public Cue? SelectedCue
    {
        get => _selectedCue;
        set
        {
            if (!Set(ref _selectedCue, value)) return;
            RebuildGroupMembers();
            // Al final de la llista no hi ha res en espera: triar una cue amb el ratolí la deixa en espera,
            // igual que fan les fletxes (si no, el GO no faria res).
            if (StandbyCue == null && value != null && !value.IsHiddenByGroup && Cues.Contains(value)) StandbyCue = value;
        }
    }

    /// <summary>La cue en espera: la que dispararà el proper GO.</summary>
    public Cue? StandbyCue
    {
        get => _standbyCue;
        set
        {
            if (_standbyCue == value) return;
            if (_standbyCue != null) _standbyCue.IsStandby = false;
            _standbyCue = value;
            if (value != null) value.IsStandby = true;
            OnPropertyChanged();
            _sessionChanged = true;
        }
    }

    public LayoutMode Layout
    {
        get => _layout;
        set { if (Set(ref _layout, value)) { OnPropertyChanged(nameof(IsListLayout)); OnPropertyChanged(nameof(IsCartLayout)); } }
    }

    public bool IsListLayout => Layout == LayoutMode.List;
    public bool IsCartLayout => Layout == LayoutMode.Cart;

    public bool IsAllPaused
    {
        get => _allPaused;
        private set { if (Set(ref _allPaused, value)) OnPropertyChanged(nameof(PauseLabel)); }
    }

    public string PauseLabel => IsAllPaused ? "▶ Reprèn" : "⏸ Pausa";

    public double MasterVolumeDb
    {
        get => _masterVolumeDb;
        set
        {
            if (!Set(ref _masterVolumeDb, Math.Clamp(value, AudioMath.SilenceDb, 6))) return;
            _outputManager.SetMasterVolume(_masterVolumeDb);
            IsDirty = true;
        }
    }

    public bool IsDirty
    {
        get => _isDirty;
        set { if (Set(ref _isDirty, value)) OnPropertyChanged(nameof(Title)); }
    }

    public string? FilePath
    {
        get => _filePath;
        private set { if (Set(ref _filePath, value)) { OnPropertyChanged(nameof(Title)); _sessionChanged = true; } }
    }

    public string Title =>
        $"{(FilePath is null ? "Sense títol" : Path.GetFileNameWithoutExtension(FilePath))}{(IsDirty ? " *" : "")} — Escena {AppVersion}{(IsShowMode ? " — MODE FUNCIÓ" : "")}";

    /// <summary>Versió del programa («0.6.0»), la del projecte.</summary>
    public static string AppVersion { get; } =
        typeof(ShowController).Assembly.GetName().Version is { } v ? $"{v.Major}.{v.Minor}.{v.Build}" : "";

    public string Status { get => _status; set => Set(ref _status, value); }

    public string OutputSummary { get => _outputSummary; private set => Set(ref _outputSummary, value); }

    // ---------------- Sortides ----------------

    /// <summary>Aplica una llista nova de sortides (des del diàleg) i les torna a obrir.</summary>
    public void ApplyOutputs(IReadOnlyList<OutputConfig> edited)
    {
        if (!OutputsDiffer(edited)) return; // res a canviar: no es toca res del que sona
        // S'actualitza la col·lecció al seu lloc perquè els desplegables no perdin la selecció.
        foreach (var old in Outputs.ToList())
            if (edited.All(e => e.Id != old.Id)) Outputs.Remove(old);
        foreach (var e in edited)
        {
            var existing = Outputs.FirstOrDefault(o => o.Id == e.Id);
            if (existing != null) existing.CopyFrom(e);
            else Outputs.Add(e.Clone());
        }
        IsDirty = true;
        ReopenOutputs();
    }

    /// <summary>La llista editada canvia alguna cosa respecte de les sortides actuals? (Aplicar-la ho atura tot.)</summary>
    public bool OutputsDiffer(IReadOnlyList<OutputConfig> edited)
    {
        static string Json(IEnumerable<OutputConfig> list) =>
            System.Text.Json.JsonSerializer.Serialize(list.ToList(), ShowFile.Options);
        return Json(Outputs) != Json(edited);
    }

    public void ReopenOutputs()
    {
        StopAll(0);
        _outputManager.CloseAll();
        FlushRetired(0, force: true);
        if (Outputs.Count == 0) Outputs.Add(AudioOutputConfig.CreateDefault());

        var warnings = _outputManager.Open(Outputs);
        OutputWarnings.Clear();
        OutputWarnings.AddRange(warnings);
        _outputManager.SetMasterVolume(MasterVolumeDb);
        UpdateOutputSummary();
        Status = warnings.Count > 0 ? "Atenció: " + string.Join(" ", warnings) : $"Sortida: {OutputSummary}";
    }

    public void TestOutput(Guid outputId) =>
        _outputManager.Outputs.FirstOrDefault(o => o.Id == outputId)?.Test();

    /// <summary>La primera sortida del tipus que necessita la cue; si és visual i no n'hi ha cap de vídeo, la crea.</summary>
    private OutputConfig? EnsureOutputFor(MediaCue cue)
    {
        var existing = cue.IsVisual
            ? Outputs.OfType<VideoOutputConfig>().FirstOrDefault() as OutputConfig
            : Outputs.OfType<AudioOutputConfig>().FirstOrDefault();
        if (existing != null || !cue.IsVisual) return existing;

        var video = VideoOutputConfig.CreateDefault();
        Outputs.Add(video);
        var warnings = new List<string>();
        _outputManager.OpenOne(video, warnings);
        OutputWarnings.AddRange(warnings);
        UpdateOutputSummary();
        Status = warnings.Count > 0
            ? "Atenció: " + string.Join(" ", warnings)
            : "S'ha creat la sortida de vídeo «Pantalla». Pots canviar la pantalla al botó de sortides.";
        return video;
    }

    private void UpdateOutputSummary()
    {
        var opened = _outputManager.Outputs;
        OutputSummary = opened.Count switch
        {
            0 => "Cap sortida oberta",
            1 => opened[0].Description,
            _ => string.Join(" · ", Outputs.Select(o => o.Name)),
        };
    }

    // ---------------- Fitxers ----------------

    private ShowDocument BuildDocument() => new()
    {
        MasterVolumeDb = MasterVolumeDb,
        Settings = Settings,
        Outputs = Outputs.ToList(),
        Cues = Cues.ToList(),
    };

    public void NewShow()
    {
        StopAll(0);
        ReplaceCues(Array.Empty<Cue>());
        Outputs.Clear();
        Outputs.Add(AudioOutputConfig.CreateDefault());
        Settings.CopyFrom(new ShowSettings());
        _masterVolumeDb = 0;
        OnPropertyChanged(nameof(MasterVolumeDb));
        ReopenOutputs();
        FilePath = null;
        StandbyCue = null;
        SelectedCue = null;
        IsDirty = false;
        // La còpia antiga no s'esborra aquí: en arrencar pot caldre per recuperar.
        _hasRecoveryCopy = false;
        _sessionChanged = true;
        ResetUndo();
        Status = "Espectacle nou.";
    }

    /// <summary>L'últim espectacle obert és d'una versió d'Escena més nova que aquesta.</summary>
    public bool OpenedNewerVersion { get; private set; }

    public void Load(string path)
    {
        var doc = ShowFile.Load(path);
        bool legacy = Path.GetExtension(path).Equals(ShowFile.LegacyExtension, StringComparison.OrdinalIgnoreCase);
        // Un .escena antic es desarà com a .show nou.
        ApplyDocument(doc, legacy ? Path.ChangeExtension(path, ShowFile.Extension) : path, dirty: legacy);

        int missing = Cues.Count(c => c.ErrorMessage != null);
        string opened = $"Obert: {Path.GetFileName(path)} ({Cues.Count} cues)";
        if (missing > 0) opened += $" — atenció: {missing} cue(s) amb fitxers que no es troben";
        if (legacy) opened += " — format antic, es desarà com a .show";
        OpenedNewerVersion = doc.Version > ShowDocument.CurrentVersion;
        if (OpenedNewerVersion) opened += " — atenció: fet amb un Escena més nou; en desar-lo es pot perdre el que aquesta versió no coneix";
        if (Status.StartsWith("Atenció")) opened += ". " + Status;
        Status = opened;
    }

    private void ApplyDocument(ShowDocument doc, string? filePath, bool dirty)
    {
        StopAll(0);
        ReplaceCues(doc.Cues);
        Outputs.Clear();
        foreach (var o in doc.Outputs) Outputs.Add(o);
        Settings.CopyFrom(doc.Settings);
        _masterVolumeDb = doc.MasterVolumeDb;
        OnPropertyChanged(nameof(MasterVolumeDb));
        ReopenOutputs();
        FilePath = filePath;
        StandbyCue = Cues.FirstOrDefault();
        SelectedCue = Cues.FirstOrDefault();
        IsDirty = dirty;
        _hasRecoveryCopy = false; // la propera còpia automàtica la tornarà a crear si cal
        _sessionChanged = true;
        ResetUndo();
    }

    public void Save(string path)
    {
        ShowFile.Save(path, BuildDocument());
        FilePath = path;
        IsDirty = false;
        Recovery.DeleteCopy();
        _hasRecoveryCopy = false;
        _sessionChanged = true;
        Status = $"Desat: {Path.GetFileName(path)}";
    }

    /// <summary>Torna a obrir l'espectacle d'una sessió que va acabar malament.</summary>
    public void Recover(SessionState state, bool continueFromStandby)
    {
        string copyPath = state.CopyPath ?? Recovery.CopyFile;
        bool fromCopy = state.HasUnsavedCopy && File.Exists(copyPath);
        if (!fromCopy && (state.ShowPath == null || !File.Exists(state.ShowPath)))
            throw new FileNotFoundException("No s'ha trobat ni la còpia ni el fitxer de l'espectacle.");
        var doc = ShowFile.Load(fromCopy ? copyPath : state.ShowPath!);
        ApplyDocument(doc, state.ShowPath, dirty: fromCopy);
        // Els canvis sense desar passen de seguida a la còpia d'aquesta finestra (la de la sessió morta s'esborrarà).
        if (fromCopy && Recovery.SaveCopy(BuildDocument())) _hasRecoveryCopy = true;
        if (continueFromStandby)
        {
            var standby = Cues.FirstOrDefault(c => c.Id == state.StandbyCueId);
            if (standby != null || state.StandbyCueId == null) StandbyCue = standby;
            SelectedCue = StandbyCue ?? SelectedCue;
        }
        Status = (fromCopy ? "Recuperat amb els canvis sense desar. " : "Recuperat. ")
                 + (continueFromStandby && StandbyCue != null ? $"En espera: {StandbyCue.DisplayName}." : "");
    }

    private void ReplaceCues(IEnumerable<Cue> cues)
    {
        foreach (var c in Cues) c.PropertyChanged -= OnCuePropertyChanged;
        Cues.Clear();
        foreach (var c in cues) Cues.Add(c);
    }

    // ---------------- Edició ----------------

    /// <summary>
    /// Insereix una cue després de la seleccionada (i de tot el que contingui, si és un grup), al mateix nivell:
    /// si la seleccionada és dins d'un grup, la nova també hi va. Rep el número següent.
    /// </summary>
    public void InsertCue(Cue cue)
    {
        if (cue is MediaCue media)
        {
            var output = EnsureOutputFor(media);
            bool fits = Outputs.FirstOrDefault(o => o.Id == media.OutputId) is { } current
                        && (current is VideoOutputConfig) == media.IsVisual;
            if (!fits) media.OutputId = output?.Id;
        }
        if (string.IsNullOrEmpty(cue.Number)) cue.Number = Cue.NextNumber(Cues);
        int index = Cues.Count;
        if (SelectedCue != null && Cues.IndexOf(SelectedCue) is var s and >= 0)
        {
            index = ShowTree.SubtreeEnd(Cues, s);
            cue.ParentId = SelectedCue.ParentId;
        }
        Cues.Insert(index, cue);
        SelectedCue = cue;
        if (Cues.Count == 1) StandbyCue = cue;
    }

    /// <summary>Esborra una cue; si és un grup, també totes les que conté.</summary>
    public void RemoveCue(Cue cue)
    {
        var block = ShowTree.Subtree(Cues, cue);
        if (block.Count == 0) return;
        int index = Cues.IndexOf(cue);
        bool standbyGone = StandbyCue != null && block.Contains(StandbyCue);
        foreach (var c in block)
        {
            if (c.IsActive) StopCue(c, 0);
            if (_previewCue == c) StopPreview();
            _pending.RemoveAll(p => p.Source == c);
        }
        foreach (var c in Enumerable.Reverse(block)) Cues.Remove(c);
        var replacement = Cues.Count == 0 ? null : Cues[Math.Min(index, Cues.Count - 1)];
        SelectedCue = replacement;
        if (standbyGone) StandbyCue = index < Cues.Count ? Cues[index] : null;
    }

    /// <summary>Ctrl+↑↓: mou la cue (amb tot el que conté) per davant o per darrere de la germana del costat.</summary>
    public void MoveCue(Cue cue, int delta)
    {
        var siblings = ShowTree.Siblings(Cues, cue);
        int i = siblings.IndexOf(cue) + delta;
        if (i < 0 || i >= siblings.Count) return;
        MoveCueTo(cue, siblings[i], delta < 0 ? DropPosition.Before : DropPosition.After);
    }

    /// <summary>
    /// Arrossegar i deixar: mou la cue (i, si és un grup, tot el que conté) davant o darrere de
    /// <paramref name="anchor"/> (al mateix nivell), o dins del grup <paramref name="anchor"/> com a última fill.
    /// Sense <paramref name="anchor"/>, la porta al final de la llista principal.
    /// </summary>
    public void MoveCueTo(Cue cue, Cue? anchor, DropPosition position)
    {
        if (anchor == cue) return;
        if (anchor != null && ShowTree.IsDescendant(Cues, anchor, cue)) return; // un grup no pot anar dins seu
        if (position == DropPosition.Into && anchor is not GroupCue) position = DropPosition.After;

        var block = ShowTree.Subtree(Cues, cue);
        if (block.Count == 0) return;
        bool wasSelected = SelectedCue == cue;
        _moving = true;
        try
        {
            foreach (var c in Enumerable.Reverse(block)) Cues.Remove(c);
            int index;
            Guid? parent;
            if (anchor == null)
            {
                index = Cues.Count;
                parent = null;
            }
            else
            {
                int a = Cues.IndexOf(anchor);
                switch (position)
                {
                    case DropPosition.Before:
                        index = a;
                        parent = anchor.ParentId;
                        break;
                    case DropPosition.Into:
                        index = ShowTree.SubtreeEnd(Cues, a);
                        parent = anchor.Id;
                        if (anchor is GroupCue g) g.IsCollapsed = false;
                        break;
                    default:
                        index = ShowTree.SubtreeEnd(Cues, a);
                        parent = anchor.ParentId;
                        break;
                }
            }
            cue.ParentId = parent;
            for (int k = 0; k < block.Count; k++) Cues.Insert(index + k, block[k]);
        }
        finally { _moving = false; }
        AfterTreeChange();
        if (wasSelected || SelectedCue == null) SelectedCue = cue;
    }

    /// <summary>Fa un grup nou amb les cues triades (com al QLab): el grup va on era la primera i les conté totes.</summary>
    public GroupCue GroupCues(IReadOnlyList<Cue> selection, string name = "Grup")
    {
        // Només les cues de més amunt: si un grup ja hi és, les seves fills hi van amb ell.
        var roots = selection.Where(c => Cues.Contains(c) && !selection.Any(o => o != c && ShowTree.IsDescendant(Cues, c, o)))
            .OrderBy(c => Cues.IndexOf(c)).ToList();
        var group = new GroupCue { Name = name, Number = Cue.NextNumber(Cues) };
        if (roots.Count == 0)
        {
            InsertCue(group);
            return group;
        }
        var first = roots[0];
        group.ParentId = first.ParentId;
        Cues.Insert(Cues.IndexOf(first), group);
        foreach (var c in roots) MoveCueTo(c, group, DropPosition.Into);
        if (roots.Contains(StandbyCue!)) StandbyCue = group;
        SelectedCue = group;
        Status = $"Grup nou amb {roots.Count} cues.";
        return group;
    }

    /// <summary>Desfà un grup: les fills queden al seu lloc, al nivell del grup, i el grup desapareix.</summary>
    public void Ungroup(GroupCue group)
    {
        if (group.IsActive) StopCue(group, 0);
        int index = Cues.IndexOf(group);
        if (index < 0) return;
        foreach (var child in ShowTree.Children(Cues, group)) child.ParentId = group.ParentId;
        bool standby = StandbyCue == group;
        Cues.Remove(group);
        AfterTreeChange();
        if (standby) StandbyCue = index < Cues.Count ? Cues[index] : null;
        SelectedCue = index < Cues.Count ? Cues[index] : Cues.LastOrDefault();
    }

    /// <summary>Treu una cue del seu grup: queda just després del grup, al mateix nivell que el grup.</summary>
    public void RemoveFromGroup(Cue cue)
    {
        if (ShowTree.Parent(Cues, cue) is { } parent) MoveCueTo(cue, parent, DropPosition.After);
    }

    private bool _moving;

    /// <summary>Recalcula nivells, cues amagades (grups plegats), la vista i el panell del grup.</summary>
    private void AfterTreeChange()
    {
        ShowTree.UpdateDepths(Cues);
        RebuildGroupMembers();
    }

    private void OnCuesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems != null)
            foreach (Cue c in e.OldItems) c.PropertyChanged -= OnCuePropertyChanged;
        if (e.NewItems != null)
            foreach (Cue c in e.NewItems) c.PropertyChanged += OnCuePropertyChanged;
        for (int i = 0; i < Cues.Count; i++) Cues[i].Position = i + 1;
        IsDirty = true;
        MarkUndoChange();
        if (_moving) return;
        // Una cue que surt de la llista deixa òrfenes les seves fills: pugen de nivell.
        if (e.OldItems != null)
            foreach (Cue gone in e.OldItems)
                foreach (var orphan in Cues.Where(c => c.ParentId == gone.Id)) orphan.ParentId = gone.ParentId;
        ShowTree.UpdateDepths(Cues);
        RebuildGroupMembers();
        UpdateDuplicateWarning();
    }

    private void OnCuePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != null && !Cue.RuntimeProperties.Contains(e.PropertyName))
        {
            IsDirty = true;
            MarkUndoChange();
        }
        if (e.PropertyName == nameof(Cue.Number)) UpdateDuplicateWarning();
        if (e.PropertyName == nameof(GroupCue.IsCollapsed) && !_moving)
        {
            AfterTreeChange();
            VisibleCues.Refresh();
            // Si la cue en espera o la seleccionada queden amagades, passen al grup.
            if (sender is GroupCue g && g.IsCollapsed)
            {
                if (StandbyCue != null && ShowTree.IsDescendant(Cues, StandbyCue, g)) StandbyCue = g;
                if (SelectedCue != null && ShowTree.IsDescendant(Cues, SelectedCue, g)) SelectedCue = g;
            }
        }
    }

    private string _duplicateWarning = "";

    /// <summary>Avís si hi ha números de cue repetits (buit si no n'hi ha).</summary>
    public string DuplicateWarning { get => _duplicateWarning; private set => Set(ref _duplicateWarning, value); }

    private void UpdateDuplicateWarning()
    {
        var dup = FindDuplicateNumber();
        DuplicateWarning = dup == null ? "" : $"⚠ Hi ha més d'una cue amb el número «{dup}».";
    }

    private void RebuildGroupMembers()
    {
        GroupMembers.Clear();
        if (SelectedCue is not GroupCue group) return;
        foreach (var c in ShowTree.Children(Cues, group)) GroupMembers.Add(c);
    }

    /// <summary>Mou la cue en espera amunt o avall per la llista visible (les fills dels grups plegats no hi són).</summary>
    public void MoveStandby(int delta)
    {
        var visible = Cues.Where(c => !c.IsHiddenByGroup).ToList();
        if (visible.Count == 0) return;
        int index = StandbyCue != null && visible.IndexOf(StandbyCue) is var i and >= 0 ? i + delta
            : delta < 0 ? visible.Count - 1 : 0; // al final de la llista, ↑ torna a l'última
        StandbyCue = visible[Math.Clamp(index, 0, visible.Count - 1)];
    }

    /// <summary>
    /// La cue que queda en espera després de fer GO a <paramref name="cue"/>: la de després (saltant el contingut
    /// d'un grup); amb un grup «entra», la seva segona fill.
    /// </summary>
    private Cue? NextStandbyAfter(Cue cue)
    {
        int i = Cues.IndexOf(cue);
        if (i < 0) return null;
        if (cue is GroupCue { Mode: GroupMode.StartFirstAndEnter } g)
        {
            var kids = ShowTree.Children(Cues, g);
            if (kids.Count > 1) return kids[1];
        }
        int next = cue is GroupCue ? ShowTree.SubtreeEnd(Cues, i) : i + 1;
        while (next < Cues.Count && Cues[next].IsHiddenByGroup && cue is not GroupCue) next++;
        return next < Cues.Count ? Cues[next] : null;
    }

    /// <summary>Hi ha números de cue repetits?</summary>
    public string? FindDuplicateNumber() =>
        Cues.Where(c => c.Number.Length > 0).GroupBy(c => c.Number, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(g => g.Count() > 1)?.Key;

    // ---------------- Controls en directe ----------------

    /// <summary>
    /// Dispara la cue en espera i passa a la següent. Ignora els GO massa seguits (protecció de doble GO).
    /// Si tot està en pausa, primer reprèn.
    /// </summary>
    public void Go()
    {
        double now = _clock.Elapsed.TotalSeconds;
        if ((now - _lastGo) * 1000 < Settings.GoLockoutMs)
        {
            Status = $"GO ignorat: massa a prop de l'anterior (protecció de {Settings.GoLockoutMs} ms).";
            return;
        }
        _lastGo = now;

        if (IsAllPaused) TogglePauseAll();
        var cue = StandbyCue;
        if (cue == null) return;
        StandbyCue = NextStandbyAfter(cue);
        Trigger(cue, "GO");
    }

    /// <summary>Vista de botonera: un clic dispara la cue; si ja és en marxa, l'atura.</summary>
    public void ToggleCue(Cue cue)
    {
        if (cue.IsActive) StopCue(cue);
        else Trigger(cue, "botonera");
    }

    public void TogglePauseAll()
    {
        // Si no «tot» està en pausa però tot el que hi ha en marxa ho està (cues pausades una a una), reprèn.
        bool pause = !IsAllPaused && !(ActiveCues.Count > 0 && ActiveCues.All(c => c.IsPaused));
        IsAllPaused = pause;
        foreach (var c in ActiveCues.ToList())
        {
            if (pause) PauseCue(c); else ResumeCue(c);
        }
        Status = pause ? "Tot en pausa." : "Represa.";
    }

    /// <summary>Esc: atura-ho tot amb la fosa de sortida de cada cue; si es repeteix aviat, talla de cop.</summary>
    public void StopAllPressed()
    {
        bool immediate = (DateTime.Now - _lastStopAll).TotalSeconds < 3;
        _lastStopAll = DateTime.Now;
        StopAll(immediate ? 0 : null);
        Status = immediate ? "Tot aturat (tall immediat)." : "Aturant-ho tot… (torna a prémer Esc per tallar de cop)";
    }

    /// <summary>Pànic de dos nivells: el primer toc fa una fosa curta de tot; el segon (abans de 3 s) ho talla.</summary>
    public void Panic()
    {
        bool immediate = (DateTime.Now - _lastPanic).TotalSeconds < 3;
        _lastPanic = DateTime.Now;
        StopAll(immediate ? 0 : Settings.PanicFadeSeconds);
        Status = immediate ? "PÀNIC: tot tallat." : $"PÀNIC: fosa de {Settings.PanicFadeSeconds:0.0} s de tot (torna a prémer per tallar).";
    }

    /// <param name="fade">null = la fosa de sortida de cada cue; 0 = immediat.</param>
    public void StopAll(double? fade, Cue? except = null)
    {
        _pending.Clear();
        _delayed.Clear();
        if (_previewViaShow) StopPreview(); // l'escolta sona per la sala: també s'atura
        foreach (var cue in ActiveCues.ToList())
        {
            if (cue == except) continue;
            if (fade != 0 && cue.State == CueState.Stopping) continue; // ja s'està aturant amb la seva fosa
            StopCue(cue, fade);
        }
        if (except == null) IsAllPaused = false;
    }

    /// <summary>Atura una cue. Si ja s'estava aturant o està en pausa, talla de cop.</summary>
    /// <param name="fade">null = la seva fosa de sortida; 0 = immediat.</param>
    public void StopCue(Cue cue, double? fade = null)
    {
        _pending.RemoveAll(p => p.Source == cue);
        if (cue is GroupCue group) StopGroupMembers(group, fade);
        if (cue is MediaCue media && cue.State == CueState.Running && !cue.IsPaused
            && _playbacks.TryGetValue(media, out var pb))
        {
            double f = fade ?? media.FadeOut;
            if (f > 0)
            {
                pb.RampTo(0, f, media.FadeOutCurve, stopAfter: true);
                cue.State = CueState.Stopping;
                return;
            }
        }
        FinishCue(cue, natural: false);
    }

    public void PauseCue(Cue cue)
    {
        if (!cue.IsActive || cue.IsPaused) return;
        cue.IsPaused = true;
        if (cue is GroupCue group) PauseGroupMembers(group, true);
        if (cue is MediaCue m && _playbacks.TryGetValue(m, out var pb)) pb.Paused = true;
    }

    public void ResumeCue(Cue cue)
    {
        if (!cue.IsPaused) return;
        cue.IsPaused = false;
        if (cue is GroupCue group) PauseGroupMembers(group, false);
        if (cue is MediaCue m && _playbacks.TryGetValue(m, out var pb)) pb.Paused = false;
    }

    /// <summary>«Surt del bucle»: la cue deixa de repetir el tros on és i continua fins al final.</summary>
    public bool DevampCue(Cue cue)
    {
        if (cue is not MediaCue m || !_playbacks.TryGetValue(m, out var pb)) return false;
        bool done = pb.Devamp();
        if (done) Status = $"{cue.DisplayName}: surt del bucle en acabar aquesta volta.";
        return done;
    }

    // ---------------- Escolta pels auriculars ----------------

    /// <summary>Hi ha una sortida d'escolta separada. Si no, s'escolta per la sortida de la cue.</summary>
    public bool IsPreviewConfigured => !string.IsNullOrEmpty(MachineSettings.PreviewDeviceId);
    private bool _previewViaShow;

    public Cue? PreviewCue
    {
        get => _previewCue;
        private set => Set(ref _previewCue, value);
    }

    public string PreviewStatus { get => _previewStatus; private set => Set(ref _previewStatus, value); }

    public void SetPreviewDevice(string deviceId)
    {
        StopPreview();
        _previewOutput?.Dispose();
        _previewOutput = null;
        MachineSettings.PreviewDeviceId = deviceId ?? "";
        MachineSettings.Save();
        OnPropertyChanged(nameof(IsPreviewConfigured));
    }

    /// <summary>
    /// Escolta una cue d'àudio pels auriculars, sense que el públic la senti ni canviï res de l'espectacle.
    /// Si ja s'escolta, l'atura.
    /// </summary>
    public void TogglePreview(Cue cue)
    {
        if (_previewCue == cue)
        {
            StopPreview();
            return;
        }
        StopPreview();
        if (cue is not AudioCue audio)
        {
            Status = "Només es poden escoltar cues d'àudio.";
            return;
        }
        try
        {
            if (!IsPreviewConfigured)
            {
                // Sense sortida d'escolta pròpia: sona per la sortida de la cue
                // (el cas habitual quan l'àudio de l'espectacle ja va als auriculars).
                var showOutput = _outputManager.Resolve(audio) ?? throw new InvalidOperationException("no hi ha cap sortida oberta per a aquesta cue");
                _preview = showOutput.Play(audio);
                _previewViaShow = true;
                PreviewCue = audio;
                return;
            }
            if (_previewOutput == null)
            {
                var output = new AudioOutput(new AudioOutputConfig { Name = "Auriculars", DeviceId = MachineSettings.PreviewDeviceId });
                output.Open();
                if (output.Warning != null)
                {
                    // Mai no s'ha d'escoltar per la sortida del públic.
                    output.Dispose();
                    throw new InvalidOperationException("la sortida d'auriculars no està connectada");
                }
                _previewOutput = output;
            }
            _preview = _previewOutput.Play(audio);
            _previewViaShow = false;
            PreviewCue = audio;
        }
        catch (Exception ex)
        {
            Status = "No es pot escoltar: " + ex.Message;
        }
    }

    public void StopPreview()
    {
        if (_preview != null)
        {
            _preview.Stop();
            _retired.Add(new Retired { Playback = _preview });
        }
        _preview = null;
        PreviewCue = null;
        PreviewStatus = "";
    }

    // ---------------- Execució ----------------

    private void Trigger(Cue cue, string source = "GO")
    {
        // Tornar a disparar una cue en marxa la reinicia. Un grup aleatori no: cada tret hi afegeix una
        // altra fill, sense tallar les que ja sonen.
        if (cue.IsActive && cue is not GroupCue { Mode: GroupMode.StartRandom }) StopCue(cue, 0);
        // Amb tot en pausa, una cue disparada (tecla directa, botonera) sona: ja no «tot» està en pausa, i el
        // botó torna a dir «Pausa». Les que estaven en pausa s'hi queden fins que es reprenguin.
        if (IsAllPaused) IsAllPaused = false;
        Log.Add(cue, source);
        cue.IsPaused = false;
        cue.WaitElapsed = 0;
        cue.Elapsed = 0;
        if (cue.State == CueState.Error) cue.State = CueState.Idle;
        _lastFired = cue;
        _sessionChanged = true;
        if (cue.PreWait > 0)
        {
            cue.State = CueState.PreWait;
            Activate(cue);
        }
        else
        {
            StartCue(cue);
        }
    }

    private void StartCue(Cue cue)
    {
        cue.WaitElapsed = 0;
        try
        {
            switch (cue)
            {
                case MediaCue media:
                    _playbacks[media] = PlayMedia(media, 0);
                    media.ErrorMessage = null;
                    break;
                case FadeCue fade:
                    ApplyFade(fade);
                    break;
                case ControlCue control:
                    ApplyControl(control);
                    break;
                case GroupCue group:
                    StartGroup(group);
                    break;
                // WaitCue: només compta el temps.
            }
        }
        catch (Exception ex)
        {
            cue.State = CueState.Error;
            cue.StatusText = "Error";
            cue.ErrorMessage = ex.Message;
            ActiveCues.Remove(cue);
            Status = $"Error a la cue {cue.DisplayName}: {ex.Message}";
            // L'espectacle continua encara que falli una cue.
            if (cue.NextAction != NextAction.None && ChainsAllowed(cue))
                _pending.Add(new PendingGo { Source = cue, Remaining = cue.PostWait });
            return;
        }

        cue.State = CueState.Running;
        Activate(cue);
        if (cue.NextAction == NextAction.AutoContinue && ChainsAllowed(cue))
            _pending.Add(new PendingGo { Source = cue, Remaining = cue.PostWait });
    }

    /// <summary>
    /// Dins dels grups «alhora», «llista» i «aleatòria» el grup decideix quan sona cada fill:
    /// els «Continua» i «Segueix» de les fills s'ignoren (com al QLab).
    /// </summary>
    private bool ChainsAllowed(Cue cue) =>
        ShowTree.Parent(Cues, cue) is not GroupCue g || g.Mode is GroupMode.StartFirst or GroupMode.StartFirstAndEnter;

    private Cue? FindCue(Guid? id) => id == null ? null : Cues.FirstOrDefault(c => c.Id == id);

    private void ApplyFade(FadeCue fade, double? time = null)
    {
        double fadeTime = time ?? fade.FadeTime;
        if (FindCue(fade.TargetId) is not MediaCue target)
            throw new InvalidOperationException("La fosa no té cap cue de fitxer destí.");
        if (target.State != CueState.Running || !_playbacks.TryGetValue(target, out var pb)) return;

        bool volume = fade.FadeVolume || !fade.FadePan; // si no es tria res, es fa el volum
        if (volume)
        {
            double master = AudioMath.DbToGain(target.BaseGainDb);
            double wanted = AudioMath.DbToGain(fade.TargetVolumeDb);
            float fader = master > 0 ? (float)Math.Min(wanted / master, 16) : 0f;
            pb.RampTo(fader, fadeTime, fade.Curve, fade.StopTarget, fade.AffectsPicture);
        }
        if (fade.FadePan)
            pb.RampPan(fade.TargetPan, fadeTime, fade.Curve, fade.StopTarget && !volume);
        if (fade.StopTarget) target.State = CueState.Stopping;
    }

    private void ApplyControl(ControlCue control)
    {
        if (!control.AllCues && FindCue(control.TargetId) == null)
            throw new InvalidOperationException("La cue de control no té cap cue destí.");
        var target = control.AllCues ? null : FindCue(control.TargetId);
        double? fade = control.UseOwnFade ? null : control.FadeTime;
        var targets = target == null ? ActiveCues.Where(c => c != control).ToList() : new List<Cue> { target };

        switch (control.Action)
        {
            case ControlAction.Stop:
                if (target == null) StopAll(fade, except: control);
                else StopCue(target, fade);
                break;
            case ControlAction.Pause:
                foreach (var c in targets) PauseCue(c);
                break;
            case ControlAction.Resume:
                foreach (var c in targets) ResumeCue(c);
                break;
            case ControlAction.Devamp:
                if (!targets.Select(DevampCue).ToList().Any(x => x))
                    Status = $"{control.DisplayName}: cap cue en marxa té un bucle pendent.";
                break;
        }
    }

    private void StartGroup(GroupCue group)
    {
        if (_groupDepth >= MaxGroupDepth) return; // evita bucles de grups que es contenen
        _groupDepth++;
        try { BeginGroupRun(group); }
        finally { _groupDepth--; }
    }

    private void TriggerNext(Cue source)
    {
        // L'encadenament va a la germana següent: dins d'un grup no en surt, i a fora salta el contingut dels grups.
        var siblings = ShowTree.Siblings(Cues, source);
        int i = siblings.IndexOf(source);
        if (i < 0 || i + 1 >= siblings.Count) return;
        var next = siblings[i + 1];
        if (StandbyCue == next) StandbyCue = NextStandbyAfter(next);
        Trigger(next, "auto");
    }

    private void Activate(Cue cue)
    {
        if (!ActiveCues.Contains(cue)) ActiveCues.Add(cue);
    }

    private void FinishCue(Cue cue, bool natural)
    {
        if (cue is GroupCue g) _groupRuns.Remove(g);
        _ducked.Remove(cue);
        if (cue is MediaCue media && _playbacks.Remove(media, out var pb))
        {
            pb.Stop();
            _retired.Add(new Retired { Playback = pb });
        }
        if (cue.State != CueState.Error) cue.State = CueState.Idle;
        cue.IsPaused = false;
        cue.HasPendingLoop = false;
        cue.Elapsed = 0;
        cue.WaitElapsed = 0;
        cue.StatusText = "";
        ActiveCues.Remove(cue);
        if (natural && cue.NextAction == NextAction.AutoFollow && ChainsAllowed(cue))
            _pending.Add(new PendingGo { Source = cue, Remaining = cue.PostWait });
    }

    // ---------------- Rellotge ----------------

    private void Tick()
    {
        double now = _clock.Elapsed.TotalSeconds;
        double dt = now - _lastTick;
        _lastTick = now;

        foreach (var cue in ActiveCues.ToList())
            UpdateCue(cue, cue.IsPaused ? 0 : dt);

        foreach (var p in _pending.ToList())
        {
            if (!_pending.Contains(p) || IsAllPaused || p.Source.IsPaused) continue;
            p.Remaining -= dt;
            if (p.Remaining <= 0)
            {
                _pending.Remove(p);
                TriggerNext(p.Source);
            }
        }

        UpdateDelayed(dt);
        UpdateUndo(dt);
        UpdateHealth(dt);
        UpdateDucking();
        UpdateStageInfo(dt);
        UpdatePreview();
        UpdateRecovery(dt);
        FlushRetired(dt, force: false);
    }

    private void UpdateCue(Cue cue, double dt)
    {
        string paused = cue.IsPaused ? "Pausa  " : "";
        switch (cue.State)
        {
            case CueState.PreWait:
                cue.WaitElapsed += dt;
                cue.StatusText = $"{paused}Espera {Math.Max(0, cue.PreWait - cue.WaitElapsed):0.0} s";
                if (cue.WaitElapsed >= cue.PreWait) StartCue(cue);
                break;

            case CueState.Running:
            case CueState.Stopping:
                switch (cue)
                {
                    case MediaCue media:
                        if (!_playbacks.TryGetValue(media, out var pb))
                        {
                            FinishCue(cue, natural: false);
                            return;
                        }
                        if (pb.IsFinished)
                        {
                            bool natural = cue.State != CueState.Stopping;
                            string? failed = (pb as AudioPlayback)?.Error;
                            _playbacks.Remove(media);
                            pb.Dispose();
                            FinishCue(cue, natural);
                            if (failed != null)
                            {
                                // El fitxer ha deixat de llegir-se a mitges: la cue s'acaba, l'espectacle continua.
                                cue.State = CueState.Error;
                                cue.ErrorMessage = "El fitxer ha fallat mentre sonava: " + failed;
                                Status = $"Error a la cue {cue.DisplayName}: {cue.ErrorMessage}";
                            }
                            return;
                        }
                        cue.Elapsed = pb.Position;
                        cue.HasPendingLoop = pb.HasPendingLoop;
                        string loop = cue.HasPendingLoop ? "🔁 " : "";
                        cue.StatusText = cue.State == CueState.Stopping
                            ? "Fosa de sortida…"
                            : cue.Duration > 0
                                ? $"{paused}{loop}{TimeFormat.Format(cue.Elapsed)}  /  -{TimeFormat.Format(cue.Duration - cue.Elapsed)}"
                                : $"{paused}{TimeFormat.Format(cue.Elapsed)}  (fins que s'aturi)";
                        break;

                    case GroupCue group:
                        cue.WaitElapsed += dt;
                        cue.Elapsed = cue.WaitElapsed;
                        UpdateGroupRun(group, dt);
                        break;

                    default:
                        cue.WaitElapsed += dt;
                        cue.Elapsed = cue.WaitElapsed;
                        cue.StatusText = $"{paused}{TimeFormat.Format(cue.Elapsed)}  /  {TimeFormat.Format(cue.Duration)}";
                        if (cue.WaitElapsed >= cue.Duration) FinishCue(cue, natural: true);
                        break;
                }
                break;
        }
    }

    private void UpdatePreview()
    {
        if (_preview == null || _previewCue == null) return;
        if (_preview.IsFinished)
        {
            StopPreview();
            return;
        }
        double pos = _preview.Position;
        string via = _previewViaShow ? "  (per la sortida de l'espectacle)" : "";
        PreviewStatus = $"🎧 {_previewCue.DisplayName}   {TimeFormat.Format(pos)} / {TimeFormat.Format(_previewCue.Duration)}{via}";
    }

    /// <summary>Desa l'estat de la sessió quan canvia i fa la còpia dels canvis sense desar cada X segons.</summary>
    private void UpdateRecovery(double dt)
    {
        _autosaveElapsed += dt;
        if (IsDirty && _autosaveElapsed >= Settings.AutosaveSeconds)
        {
            _autosaveElapsed = 0;
            if (Recovery.SaveCopy(BuildDocument()))
            {
                _hasRecoveryCopy = true;
                _sessionChanged = true;
            }
        }
        if (!IsDirty) _autosaveElapsed = 0;

        _sessionElapsed += dt;
        if (!_sessionChanged || _sessionElapsed < 0.5) return;
        _sessionElapsed = 0;
        _sessionChanged = false;
        Recovery.WriteSession(new SessionState
        {
            ShowPath = FilePath,
            HasUnsavedCopy = _hasRecoveryCopy,
            StandbyCueId = StandbyCue?.Id,
            StandbyLabel = StandbyCue?.DisplayName,
            LastFiredCueId = _lastFired?.Id,
            LastFiredLabel = _lastFired?.DisplayName,
        });
    }

    private void FlushRetired(double dt, bool force)
    {
        for (int i = _retired.Count - 1; i >= 0; i--)
        {
            var r = _retired[i];
            r.Age += dt;
            if (force || r.Playback.IsFinished || r.Age > 2)
            {
                r.Playback.Dispose();
                _retired.RemoveAt(i);
            }
        }
    }

    /// <summary>Tancament: atura-ho tot i, si <paramref name="clean"/>, esborra les dades de recuperació.</summary>
    public void Dispose() => Shutdown(clean: true);

    public void Shutdown(bool clean)
    {
        _timer.Stop();
        UnwatchDisplays();
        StopAll(0);
        StopPreview();
        _previewOutput?.Dispose();
        _outputManager.Dispose();
        FlushRetired(0, force: true);
        if (clean) Recovery.Clear();
    }
}
