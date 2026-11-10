using System.Text.Json.Serialization;

namespace Escena.Model;

/// <summary>
/// Entrada en directe (micròfon o línia) cap a les sortides d'àudio, amb volum, panorama, foses,
/// matriu i enviaments com qualsevol cue d'àudio. Sona fins que s'atura.
/// </summary>
public sealed class MicCue : MediaCue, IAudioSource
{
    private string _inputDeviceId = "";
    private double _volumeDb, _pan;
    private int _sourceChannels;

    public MicCue()
    {
        Routing.PropertyChanged += (_, _) => OnPropertyChanged(nameof(Routing));
    }

    /// <summary>Dispositiu d'entrada WASAPI; buit = l'entrada predeterminada de Windows.</summary>
    public string InputDeviceId { get => _inputDeviceId; set => Set(ref _inputDeviceId, value ?? ""); }

    public double VolumeDb { get => _volumeDb; set => Set(ref _volumeDb, Math.Clamp(value, AudioMath.SilenceDb, 24)); }

    public double Pan { get => _pan; set => Set(ref _pan, Math.Clamp(value, -1, 1)); }

    [JsonObjectCreationHandling(JsonObjectCreationHandling.Populate)]
    public AudioRouting Routing { get; } = new();

    [JsonIgnore] public int SourceChannels { get => _sourceChannels; internal set => Set(ref _sourceChannels, value); }

    [JsonIgnore] public override double BaseGainDb => VolumeDb;

    [JsonIgnore] public override string TypeName => Escena.L.T("Micròfon");

    public override void ReloadFileInfo() => ErrorMessage = null;
}

public enum TextPlacement { Top, Center, Bottom }

/// <summary>
/// Text a la pantalla (títols, rètols, sobretítols), amb fosa des de negre i a negre.
/// Si <see cref="ReplaceTexts"/> està marcat, en començar treu els altres textos de la mateixa pantalla.
/// </summary>
public sealed class TextCue : MediaCue
{
    private string _text = "";
    private double _fontSize = 64, _displayTime, _backgroundOpacity;
    private string _color = "#FFFFFF";
    private TextPlacement _placement = TextPlacement.Center;
    private bool _replaceTexts = true;

    public string Text { get => _text; set => Set(ref _text, value ?? ""); }

    /// <summary>Mida de la lletra en punts d'una pantalla de 1080 línies (s'escala amb la pantalla).</summary>
    public double FontSize { get => _fontSize; set => Set(ref _fontSize, Math.Clamp(value, 8, 400)); }

    /// <summary>Color en format #RRGGBB.</summary>
    public string Color { get => _color; set => Set(ref _color, string.IsNullOrWhiteSpace(value) ? "#FFFFFF" : value.Trim()); }

    public TextPlacement Placement { get => _placement; set => Set(ref _placement, value); }

    /// <summary>Opacitat de la franja fosca darrere el text (0 = cap, 1 = negre).</summary>
    public double BackgroundOpacity { get => _backgroundOpacity; set => Set(ref _backgroundOpacity, Math.Clamp(value, 0, 1)); }

    /// <summary>Segons que es mostra (inclosa la fosa de sortida); 0 = fins que s'aturi.</summary>
    public double DisplayTime
    {
        get => _displayTime;
        set { if (Set(ref _displayTime, Math.Max(0, value))) NotifyDurationChanged(); }
    }

    /// <summary>En començar, treu (amb la seva fosa) els altres textos de la mateixa pantalla: ideal per a sobretítols.</summary>
    public bool ReplaceTexts { get => _replaceTexts; set => Set(ref _replaceTexts, value); }

    [JsonIgnore] public override string TypeName => Escena.L.T("Text");

    [JsonIgnore] public override bool IsVisual => true;

    [JsonIgnore] public override double Duration => DisplayTime;

    public override void ReloadFileInfo() => ErrorMessage = null;
}
