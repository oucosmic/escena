using System.Collections.ObjectModel;
using System.Text.Json.Serialization;

namespace Escena.Model;

/// <summary>Un punt de creu de la matriu: canal de la cue → canal de la sortida, amb un nivell en dB.</summary>
public sealed class CrossPoint
{
    public int In { get; set; }
    public int Out { get; set; }
    public double Db { get; set; }
}

/// <summary>Enviament addicional d'una cue a una altra sortida (monitors, efectes d'escenari…).</summary>
public sealed class OutputSend : ObservableObject
{
    private Guid? _outputId;
    private double _levelDb;
    private List<CrossPoint> _crosspoints = new();

    public Guid? OutputId { get => _outputId; set => Set(ref _outputId, value); }

    /// <summary>Nivell de l'enviament (dB), a més del volum de la cue.</summary>
    public double LevelDb { get => _levelDb; set => Set(ref _levelDb, Math.Clamp(value, AudioMath.SilenceDb, 12)); }

    /// <summary>Matriu pròpia d'aquest enviament; buida = encaminament per defecte.</summary>
    public List<CrossPoint> Crosspoints
    {
        get => _crosspoints;
        set { _crosspoints = value ?? new(); OnPropertyChanged(); OnPropertyChanged(nameof(MatrixText)); }
    }

    [JsonIgnore] public string MatrixText => Crosspoints.Count == 0 ? Escena.L.T("Matriu…") : Escena.L.T("Matriu ✱");
}

/// <summary>
/// Encaminament d'una cue d'àudio (fitxer o micròfon): matriu cap a la sortida principal,
/// enviaments a altres sortides i ducking (abaixar les altres cues mentre sona).
/// </summary>
public sealed class AudioRouting : ObservableObject
{
    private List<CrossPoint> _crosspoints = new();
    private bool _duckOthers;
    private double _duckDb = -12, _duckFade = 1;

    public AudioRouting()
    {
        Sends.CollectionChanged += (_, e) =>
        {
            if (e.NewItems != null) foreach (OutputSend s in e.NewItems) s.PropertyChanged += OnChildChanged;
            if (e.OldItems != null) foreach (OutputSend s in e.OldItems) s.PropertyChanged -= OnChildChanged;
            OnPropertyChanged(nameof(Sends));
        };
    }

    /// <summary>Matriu cap a la sortida principal de la cue; buida = per defecte (estèreo L→1, R→2; mono a 1 i 2).</summary>
    public List<CrossPoint> Crosspoints
    {
        get => _crosspoints;
        set { _crosspoints = value ?? new(); OnPropertyChanged(); OnPropertyChanged(nameof(MatrixText)); }
    }

    [JsonIgnore] public string MatrixText => Crosspoints.Count == 0 ? Escena.L.T("Matriu…") : Escena.L.T("Matriu ✱");

    [JsonObjectCreationHandling(JsonObjectCreationHandling.Populate)]
    public ObservableCollection<OutputSend> Sends { get; } = new();

    /// <summary>Mentre aquesta cue sona, les altres baixen <see cref="DuckDb"/>.</summary>
    public bool DuckOthers { get => _duckOthers; set => Set(ref _duckOthers, value); }

    public double DuckDb { get => _duckDb; set => Set(ref _duckDb, Math.Clamp(value, AudioMath.SilenceDb, 0)); }

    /// <summary>Temps de baixada i de pujada del ducking (s).</summary>
    public double DuckFade { get => _duckFade; set => Set(ref _duckFade, Math.Clamp(value, 0, 30)); }

    private void OnChildChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) =>
        OnPropertyChanged(nameof(Sends));

    /// <summary>Matriu de guanys [canal de la cue, canal de sortida] a partir dels punts de creu (buits = per defecte).</summary>
    public static float[,] BuildMatrix(IReadOnlyList<CrossPoint> points, int inChannels, int outChannels)
    {
        if (points.Count == 0) return Audio.GainRampProvider.DefaultMatrix(inChannels, outChannels);
        var m = new float[inChannels, outChannels];
        foreach (var p in points)
            if (p.In >= 0 && p.In < inChannels && p.Out >= 0 && p.Out < outChannels && p.Db > AudioMath.SilenceDb)
                m[p.In, p.Out] = (float)AudioMath.DbToGain(p.Db);
        return m;
    }
}

/// <summary>Cue que sona per les sortides d'àudio amb volum, panorama i encaminament (fitxer d'àudio o micròfon).</summary>
public interface IAudioSource
{
    Guid Id { get; }
    Guid? OutputId { get; }
    double VolumeDb { get; }
    double Pan { get; }
    AudioRouting Routing { get; }
    /// <summary>Canals de la font (per editar la matriu); 0 = desconegut.</summary>
    int SourceChannels { get; }
}
