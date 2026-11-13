using System.Text.Json.Serialization;

namespace Escena.Model;

/// <summary>Porta una cue de fitxer a un volum en un temps i, si cal, l'atura.</summary>
public sealed class FadeCue : Cue
{
    private Guid? _targetId;
    private double _targetVolumeDb = AudioMath.SilenceDb;
    private double _fadeTime = 3;
    private FadeCurve _curve = FadeCurve.Linear;
    private bool _stopTarget = true;

    public Guid? TargetId { get => _targetId; set => Set(ref _targetId, value); }

    public double TargetVolumeDb
    {
        get => _targetVolumeDb;
        set => Set(ref _targetVolumeDb, Math.Clamp(value, AudioMath.SilenceDb, 12));
    }

    public double FadeTime
    {
        get => _fadeTime;
        set { if (Set(ref _fadeTime, Math.Max(0, value))) NotifyDurationChanged(); }
    }

    public FadeCurve Curve { get => _curve; set => Set(ref _curve, value); }

    public bool StopTarget { get => _stopTarget; set => Set(ref _stopTarget, value); }

    /// <summary>La fosa canvia el volum (si no, només el panorama).</summary>
    public bool FadeVolume { get => _fadeVolume; set => Set(ref _fadeVolume, value); }
    private bool _fadeVolume = true;

    /// <summary>La fosa també mou el panorama fins a <see cref="TargetPan"/>.</summary>
    public bool FadePan { get => _fadePan; set => Set(ref _fadePan, value); }
    private bool _fadePan;

    /// <summary>Panorama final: −1 esquerra, 0 centre, +1 dreta.</summary>
    public double TargetPan { get => _targetPan; set => Set(ref _targetPan, Math.Clamp(value, -1, 1)); }
    private double _targetPan;

    /// <summary>En cues visuals, la fosa també enfosqueix la imatge (a −60 dB queda en negre).</summary>
    public bool AffectsPicture { get => _affectsPicture; set => Set(ref _affectsPicture, value); }
    private bool _affectsPicture = true;

    [JsonIgnore] public override string TypeName => Escena.L.T("Fosa");

    [JsonIgnore] public override double Duration => FadeTime;
}

public enum ControlAction { Stop, Pause, Resume, Devamp }

/// <summary>Atura, pausa o reprèn una cue concreta o totes.</summary>
public sealed class ControlCue : Cue
{
    private ControlAction _action;
    private bool _allCues = true;
    private Guid? _targetId;
    private bool _useOwnFade = true;
    private double _fadeTime = 2;

    public ControlAction Action
    {
        get => _action;
        set
        {
            if (!Set(ref _action, value)) return;
            OnPropertyChanged(nameof(TypeName));
            NotifyDurationChanged();
        }
    }

    public bool AllCues
    {
        get => _allCues;
        set { if (Set(ref _allCues, value)) OnPropertyChanged(nameof(HasIgnoredTarget)); }
    }

    public Guid? TargetId
    {
        get => _targetId;
        set { if (Set(ref _targetId, value)) OnPropertyChanged(nameof(HasIgnoredTarget)); }
    }

    /// <summary>Té una cue destí però «Totes» està marcat: actuarà sobre totes (cal avisar-ho).</summary>
    [JsonIgnore] public bool HasIgnoredTarget => AllCues && TargetId != null;

    /// <summary>En aturar: fes servir la fosa de sortida de cada cue en lloc de <see cref="FadeTime"/>.</summary>
    public bool UseOwnFade
    {
        get => _useOwnFade;
        set { if (Set(ref _useOwnFade, value)) NotifyDurationChanged(); }
    }

    public double FadeTime
    {
        get => _fadeTime;
        set { if (Set(ref _fadeTime, Math.Max(0, value))) NotifyDurationChanged(); }
    }

    [JsonIgnore]
    public override string TypeName => Action switch
    {
        ControlAction.Pause => Escena.L.T("Pausa"),
        ControlAction.Resume => Escena.L.T("Reprèn"),
        ControlAction.Devamp => Escena.L.T("Surt del bucle"),
        _ => Escena.L.T("Atura"),
    };

    [JsonIgnore] public override double Duration => Action == ControlAction.Stop && !UseOwnFade ? FadeTime : 0;
}

/// <summary>
/// Nota a la llista (com les «Memo» del QLab): marca parts de l'espectacle («Escena 1», «Escena 2»…) i no fa res.
/// Ve amb «Continua» marcat: quan li arriba el GO, passa a la cue següent i l'espectacle no s'hi atura.
/// </summary>
public sealed class MemoCue : Cue
{
    public MemoCue() => NextAction = NextAction.AutoContinue;

    [JsonIgnore] public override string TypeName => Escena.L.T("Nota");
}

/// <summary>Una espera cronometrada (útil amb «Segueix» per fer pauses dins d'una seqüència).</summary>
public sealed class WaitCue : Cue
{
    private double _waitTime = 5;

    public double WaitTime
    {
        get => _waitTime;
        set { if (Set(ref _waitTime, Math.Max(0, value))) NotifyDurationChanged(); }
    }

    [JsonIgnore] public override string TypeName => Escena.L.T("Espera");

    [JsonIgnore] public override double Duration => WaitTime;
}

