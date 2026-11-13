using System.Text.Json.Serialization;

namespace Escena.Model;

public enum NextAction { None, AutoContinue, AutoFollow }

/// <summary>Fase d'execució. La pausa és a part (<see cref="Cue.IsPaused"/>) perquè es pot pausar en qualsevol fase.</summary>
public enum CueState { Idle, PreWait, Running, Stopping, Error }

public enum FadeCurve { Linear, Quadratic, InverseQuadratic, SCurve, Exponential }

public static class FadeCurves
{
    /// <summary>Converteix el progrés lineal t (0–1) segons la corba.</summary>
    public static double Apply(FadeCurve curve, double t)
    {
        t = Math.Clamp(t, 0, 1);
        return curve switch
        {
            FadeCurve.Quadratic => t * t,
            FadeCurve.InverseQuadratic => 1 - (1 - t) * (1 - t),
            FadeCurve.SCurve => t * t * (3 - 2 * t),
            _ => t,
        };
    }

    /// <summary>
    /// Valor entre <paramref name="from"/> i <paramref name="to"/> al progrés t. L'exponencial avança a passos
    /// iguals de dB (sona com una fosa «regular» a l'orella); la resta deformen el progrés lineal.
    /// </summary>
    public static double Interpolate(FadeCurve curve, double from, double to, double t)
    {
        if (t <= 0) return from;
        if (t >= 1) return to;
        if (curve != FadeCurve.Exponential) return from + (to - from) * Apply(curve, t);
        const double floorDb = -60;
        double a = from <= 0 ? floorDb : Math.Max(floorDb, 20 * Math.Log10(from));
        double b = to <= 0 ? floorDb : Math.Max(floorDb, 20 * Math.Log10(to));
        return Math.Pow(10, (a + (b - a) * t) / 20);
    }
}

/// <summary>
/// Base comuna de totes les cues. La llista, el GO i els encadenaments només treballen amb aquesta classe,
/// de manera que un tipus nou (p. ex. vídeo) no hi ha de canviar res.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(AudioCue), "audio")]
[JsonDerivedType(typeof(VideoCue), "video")]
[JsonDerivedType(typeof(ImageCue), "image")]
[JsonDerivedType(typeof(FadeCue), "fade")]
[JsonDerivedType(typeof(ControlCue), "control")]
[JsonDerivedType(typeof(WaitCue), "wait")]
[JsonDerivedType(typeof(GroupCue), "group")]
[JsonDerivedType(typeof(MicCue), "mic")]
[JsonDerivedType(typeof(TextCue), "text")]
[JsonDerivedType(typeof(MemoCue), "memo")]
public abstract class Cue : ObservableObject
{
    /// <summary>Propietats d'execució o derivades: no marquen l'espectacle com a modificat.</summary>
    public static readonly HashSet<string> RuntimeProperties = new()
    {
        nameof(Position), nameof(IsStandby), nameof(DisplayName), nameof(State), nameof(IsActive), nameof(IsPaused), nameof(Elapsed),
        nameof(Progress), nameof(StatusText), nameof(ErrorMessage), nameof(Duration), nameof(DurationText),
        nameof(NextActionText), nameof(TypeName), "FileDuration", "MemberCount", "HasIgnoredTarget", nameof(HasPendingLoop), "SourceChannels", "MatrixText", nameof(Depth), nameof(IsHiddenByGroup), nameof(HasParent), "IsCollapsed", "IsPlaylist", "ModeHint", "AudioOutputChoice",
    };

    private string _name = "";
    private double _preWait, _postWait;
    private NextAction _nextAction;
    private string _notes = "";

    public Guid Id { get; set; } = Guid.NewGuid();

    private string _number = "";

    /// <summary>Número lliure de la cue («1», «1.5», «2A»…). No ha de coincidir amb la posició a la llista.</summary>
    public string Number
    {
        get => _number;
        set { if (Set(ref _number, (value ?? "").Trim())) OnPropertyChanged(nameof(DisplayName)); }
    }

    public string Name
    {
        get => _name;
        set { if (Set(ref _name, value ?? "")) OnPropertyChanged(nameof(DisplayName)); }
    }

    public double PreWait { get => _preWait; set => Set(ref _preWait, Math.Max(0, value)); }

    public double PostWait { get => _postWait; set => Set(ref _postWait, Math.Max(0, value)); }

    public NextAction NextAction
    {
        get => _nextAction;
        set { if (Set(ref _nextAction, value)) OnPropertyChanged(nameof(NextActionText)); }
    }

    public string Notes { get => _notes; set => Set(ref _notes, value ?? ""); }

    private Guid? _parentId;

    /// <summary>Grup que conté aquesta cue (null = és a la llista principal).</summary>
    public Guid? ParentId
    {
        get => _parentId;
        set { if (Set(ref _parentId, value)) OnPropertyChanged(nameof(HasParent)); }
    }

    [JsonIgnore] public bool HasParent => ParentId != null;

    private int _depth;
    private bool _isHiddenByGroup;

    /// <summary>Nivell dins dels grups (0 = llista principal), per sagnar-la a la llista.</summary>
    [JsonIgnore] public int Depth { get => _depth; set => Set(ref _depth, value); }

    /// <summary>És dins d'un grup plegat: no es mostra a la llista.</summary>
    [JsonIgnore] public bool IsHiddenByGroup { get => _isHiddenByGroup; set => Set(ref _isHiddenByGroup, value); }

    private string _hotkey = "";

    /// <summary>Tecla que dispara aquesta cue directament («F1», «Ctrl+1»…), a més del GO. Buit = cap.</summary>
    public string Hotkey { get => _hotkey; set => Set(ref _hotkey, value ?? ""); }

    [JsonIgnore] public abstract string TypeName { get; }

    /// <summary>Durada coneguda en segons (0 = instantània o desconeguda).</summary>
    [JsonIgnore] public virtual double Duration => 0;

    [JsonIgnore] public string DurationText => Duration > 0 ? TimeFormat.Format(Duration) : "";

    [JsonIgnore]
    public string NextActionText => NextAction switch
    {
        NextAction.AutoContinue => "↓ cont.",
        NextAction.AutoFollow => "↓ seg.",
        _ => "",
    };

    protected void NotifyDurationChanged()
    {
        OnPropertyChanged(nameof(Duration));
        OnPropertyChanged(nameof(DurationText));
        OnPropertyChanged(nameof(Progress));
    }

    // ---- Estat d'execució ----

    private int _position;
    private bool _isStandby;
    private CueState _state;
    private bool _isPaused;
    private double _elapsed;
    private string _statusText = "";
    private string? _errorMessage;

    /// <summary>Posició a la llista (1, 2, 3…), independent del número.</summary>
    [JsonIgnore] public int Position { get => _position; set => Set(ref _position, value); }

    /// <summary>És la cue en espera: la que dispararà el proper GO.</summary>
    [JsonIgnore] public bool IsStandby { get => _isStandby; set => Set(ref _isStandby, value); }

    /// <summary>Està sonant dins d'un tros en bucle que espera «Surt del bucle».</summary>
    [JsonIgnore] public bool HasPendingLoop { get => _hasPendingLoop; set => Set(ref _hasPendingLoop, value); }
    private bool _hasPendingLoop;

    [JsonIgnore] public string DisplayName => string.IsNullOrEmpty(Number) ? Name : $"{Number} · {Name}";

    /// <summary>Número següent per a una cue nova: l'enter més alt de la llista + 1.</summary>
    public static string NextNumber(IEnumerable<Cue> cues)
    {
        int max = 0;
        foreach (var c in cues)
        {
            var digits = new string(c.Number.TakeWhile(char.IsDigit).ToArray());
            if (int.TryParse(digits, out int n)) max = Math.Max(max, n);
        }
        return (max + 1).ToString();
    }

    [JsonIgnore]
    public CueState State
    {
        get => _state;
        set { if (Set(ref _state, value)) OnPropertyChanged(nameof(IsActive)); }
    }

    [JsonIgnore] public bool IsActive => State is CueState.PreWait or CueState.Running or CueState.Stopping;

    [JsonIgnore] public bool IsPaused { get => _isPaused; set => Set(ref _isPaused, value); }

    [JsonIgnore]
    public double Elapsed
    {
        get => _elapsed;
        set { if (Set(ref _elapsed, value)) OnPropertyChanged(nameof(Progress)); }
    }

    [JsonIgnore] public double Progress => Duration > 0 ? Math.Clamp(Elapsed / Duration * 100.0, 0, 100) : 0;

    [JsonIgnore] public string StatusText { get => _statusText; set => Set(ref _statusText, value); }

    [JsonIgnore] public string? ErrorMessage { get => _errorMessage; set => Set(ref _errorMessage, value); }

    /// <summary>Temps acumulat de la pre-espera o, per a cues sense fitxer, de l'execució.</summary>
    internal double WaitElapsed;
}
