using System.Text.Json.Serialization;

namespace Escena.Model;

/// <summary>
/// Sortida definida a l'espectacle, independent de les cues: cada cue de fitxer n'apunta una.
/// Avui només n'hi ha d'àudio; una sortida de vídeo (pantalla/projector) serà un altre tipus.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(AudioOutputConfig), "audio")]
[JsonDerivedType(typeof(VideoOutputConfig), "video")]
public abstract class OutputConfig : ObservableObject
{
    private string _name = "";

    public Guid Id { get; set; } = Guid.NewGuid();

    public string Name
    {
        get => _name;
        set { if (Set(ref _name, value ?? "")) OnPropertyChanged(nameof(ListLabel)); }
    }

    [JsonIgnore] public abstract string KindName { get; }

    [JsonIgnore] public string ListLabel => $"{Name}  ({KindName})";

    public abstract OutputConfig Clone();

    public abstract void CopyFrom(OutputConfig other);
}

public enum AudioDriver { WasapiShared, WasapiExclusive, Asio }

public sealed class AudioOutputConfig : OutputConfig
{
    private AudioDriver _driver = AudioDriver.WasapiShared;
    private string _deviceId = "";
    private int _latencyMs = 50;
    private int _asioChannelOffset;
    private double _trimDb;
    private int _channels;

    /// <summary>
    /// Canals de la sortida. WASAPI compartit fa servir sempre els del dispositiu; en exclusiu i ASIO,
    /// 0 = 2 canals (o els del dispositiu en exclusiu) i un altre número obre aquests canals.
    /// </summary>
    public int Channels { get => _channels; set => Set(ref _channels, Math.Clamp(value, 0, 64)); }

    public AudioDriver Driver { get => _driver; set => Set(ref _driver, value); }

    /// <summary>Id del dispositiu WASAPI o nom del controlador ASIO; buit = predeterminat.</summary>
    public string DeviceId { get => _deviceId; set => Set(ref _deviceId, value ?? ""); }

    public int LatencyMs { get => _latencyMs; set => Set(ref _latencyMs, Math.Clamp(value, 5, 500)); }

    /// <summary>Primer canal de sortida ASIO (0 = canals 1-2, 2 = canals 3-4…).</summary>
    public int AsioChannelOffset { get => _asioChannelOffset; set => Set(ref _asioChannelOffset, Math.Max(0, value)); }

    /// <summary>Ajust fix de volum d'aquesta sortida (dB).</summary>
    public double TrimDb { get => _trimDb; set => Set(ref _trimDb, Math.Clamp(value, AudioMath.SilenceDb, 12)); }

    [JsonIgnore] public override string KindName => "Àudio";

    public static AudioOutputConfig CreateDefault() => new() { Name = "Sortida principal" };

    public override OutputConfig Clone()
    {
        var c = new AudioOutputConfig { Id = Id };
        c.CopyFrom(this);
        return c;
    }

    public override void CopyFrom(OutputConfig other)
    {
        if (other is not AudioOutputConfig o) return;
        Name = o.Name;
        Driver = o.Driver;
        DeviceId = o.DeviceId;
        LatencyMs = o.LatencyMs;
        AsioChannelOffset = o.AsioChannelOffset;
        TrimDb = o.TrimDb;
        Channels = o.Channels;
    }
}

/// <summary>
/// Sortida de vídeo: una finestra negra en una pantalla. En una pantalla ampliada (projector)
/// es posa a pantalla completa i en negre des que s'obre l'espectacle i entre vídeos.
/// </summary>
public sealed class VideoOutputConfig : OutputConfig
{
    private string _screenId = "";
    private bool _fullscreen = true;
    private double _trimDb;

    /// <summary>Nom de dispositiu de la pantalla (\\.\DISPLAY2…); buit = la primera pantalla ampliada, o la principal.</summary>
    public string ScreenId { get => _screenId; set => Set(ref _screenId, value ?? ""); }

    /// <summary>Pantalla completa (sense vores ni cursor). Si no, una finestra que es pot moure.</summary>
    public bool Fullscreen { get => _fullscreen; set => Set(ref _fullscreen, value); }

    /// <summary>Ajust fix del volum del so dels vídeos (dB).</summary>
    public double TrimDb { get => _trimDb; set => Set(ref _trimDb, Math.Clamp(value, AudioMath.SilenceDb, 12)); }

    /// <summary>
    /// Correcció del projector: desplaçament de cada cantonada en fracció de l'amplada/alçada
    /// (ordre: dalt-esquerra X,Y · dalt-dreta X,Y · baix-dreta X,Y · baix-esquerra X,Y). Tot 0 = sense correcció.
    /// </summary>
    public double[] Corners { get => _corners; set => Set(ref _corners, value is { Length: 8 } ? value : new double[8]); }
    private double[] _corners = new double[8];

    [JsonIgnore] public bool HasCorrection => Corners.Any(c => Math.Abs(c) > 1e-6);

    [JsonIgnore] public override string KindName => "Vídeo";

    /// <summary>A la primera pantalla ampliada a pantalla completa; si no n'hi ha, en finestra a la principal.</summary>
    public static VideoOutputConfig CreateDefault()
    {
        var monitors = Escena.Video.Monitors.List();
        var extended = monitors.FirstOrDefault(m => !m.IsPrimary);
        return new VideoOutputConfig
        {
            Name = "Pantalla",
            ScreenId = (extended ?? monitors.FirstOrDefault())?.DeviceName ?? "",
            Fullscreen = extended != null,
        };
    }

    public override OutputConfig Clone()
    {
        var c = new VideoOutputConfig { Id = Id };
        c.CopyFrom(this);
        return c;
    }

    public override void CopyFrom(OutputConfig other)
    {
        if (other is not VideoOutputConfig o) return;
        Name = o.Name;
        ScreenId = o.ScreenId;
        Fullscreen = o.Fullscreen;
        TrimDb = o.TrimDb;
        Corners = (double[])o.Corners.Clone();
    }
}
