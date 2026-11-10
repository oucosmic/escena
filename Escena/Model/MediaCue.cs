using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Media.Imaging;
using Escena.Audio;
using Escena.Video;

namespace Escena.Model;

/// <summary>
/// Cue que mostra o reprodueix un fitxer per una sortida, amb fosa d'entrada i de sortida.
/// El controlador només coneix aquesta classe: no sap si és àudio, vídeo o imatge.
/// </summary>
public abstract class MediaCue : Cue
{
    private string _filePath = "";
    private Guid? _outputId;
    private double _fadeIn, _fadeOut;
    private FadeCurve _fadeInCurve = FadeCurve.Linear, _fadeOutCurve = FadeCurve.Linear;

    public string FilePath
    {
        get => _filePath;
        set { if (Set(ref _filePath, value ?? "")) ReloadFileInfo(); }
    }

    /// <summary>Sortida per on surt; null = la primera adequada de l'espectacle.</summary>
    public Guid? OutputId { get => _outputId; set => Set(ref _outputId, value); }

    public double FadeIn { get => _fadeIn; set => Set(ref _fadeIn, Math.Max(0, value)); }

    public FadeCurve FadeInCurve { get => _fadeInCurve; set => Set(ref _fadeInCurve, value); }

    public double FadeOut { get => _fadeOut; set => Set(ref _fadeOut, Math.Max(0, value)); }

    public FadeCurve FadeOutCurve { get => _fadeOutCurve; set => Set(ref _fadeOutCurve, value); }

    /// <summary>Volum base (dB) respecte del qual es calculen les cues de fosa.</summary>
    [JsonIgnore] public virtual double BaseGainDb => 0;

    /// <summary>Les cues visuals surten per una sortida de vídeo.</summary>
    [JsonIgnore] public virtual bool IsVisual => false;

    /// <summary>Torna a llegir la informació del fitxer (i marca l'error si no s'obre).</summary>
    public abstract void ReloadFileInfo();
}

/// <summary>Fitxer amb temps (àudio o vídeo): volum, panorama, punts d'inici/final i bucle.</summary>
public abstract class PlayableCue : MediaCue
{
    private double _volumeDb, _pan, _startAt, _endAt, _fileDuration;
    private bool _loop;

    public double VolumeDb { get => _volumeDb; set => Set(ref _volumeDb, Math.Clamp(value, AudioMath.SilenceDb, 12)); }

    /// <summary>−1 esquerra, 0 centre, +1 dreta.</summary>
    public double Pan { get => _pan; set => Set(ref _pan, Math.Clamp(value, -1, 1)); }

    public double StartAt
    {
        get => _startAt;
        set { if (Set(ref _startAt, Math.Max(0, value))) NotifyDurationChanged(); }
    }

    /// <summary>Punt final en segons; 0 vol dir fins al final del fitxer.</summary>
    public double EndAt
    {
        get => _endAt;
        set { if (Set(ref _endAt, Math.Max(0, value))) NotifyDurationChanged(); }
    }

    public bool Loop { get => _loop; set => Set(ref _loop, value); }

    [JsonIgnore] public override double BaseGainDb => VolumeDb;

    [JsonIgnore]
    public double FileDuration
    {
        get => _fileDuration;
        protected set { if (Set(ref _fileDuration, value)) NotifyDurationChanged(); }
    }

    [JsonIgnore]
    public override double Duration
    {
        get
        {
            double end = EndAt > StartAt && (FileDuration <= 0 || EndAt < FileDuration) ? EndAt : FileDuration;
            return Math.Max(0, end - StartAt);
        }
    }
}

/// <summary>
/// Tros d'un fitxer que es repeteix (com els «slices»/«devamp» de QLab): quan la reproducció arriba al final
/// del tros torna a l'inici, fins que s'han fet les repeticions o fins que una cue «Surt del bucle» l'allibera.
/// </summary>
public sealed class LoopSlice : ObservableObject
{
    private double _start, _end;
    private int _repeats;

    public double Start { get => _start; set => Set(ref _start, Math.Max(0, value)); }

    public double End { get => _end; set => Set(ref _end, Math.Max(0, value)); }

    /// <summary>Repeticions extra; 0 = es repeteix fins que se'n surt amb «Surt del bucle».</summary>
    public int Repeats { get => _repeats; set => Set(ref _repeats, Math.Max(0, value)); }

    [JsonIgnore] public bool IsValid => End > Start + 0.05;
}

public sealed class AudioCue : PlayableCue, IAudioSource
{
    private int _sourceChannels;

    public AudioCue()
    {
        Routing.PropertyChanged += (_, _) => OnPropertyChanged(nameof(Routing));
        Slices.CollectionChanged += (_, e) =>
        {
            if (e.NewItems != null) foreach (LoopSlice s in e.NewItems) s.PropertyChanged += OnSliceChanged;
            if (e.OldItems != null) foreach (LoopSlice s in e.OldItems) s.PropertyChanged -= OnSliceChanged;
            OnPropertyChanged(nameof(Slices));
        };
    }

    /// <summary>Bucles dins del fitxer (en segons del fitxer).</summary>
    [JsonObjectCreationHandling(JsonObjectCreationHandling.Populate)] // omple aquesta mateixa col·lecció en obrir
    public System.Collections.ObjectModel.ObservableCollection<LoopSlice> Slices { get; } = new();

    private void OnSliceChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) =>
        OnPropertyChanged(nameof(Slices));

    /// <summary>Matriu, enviaments a altres sortides i ducking.</summary>
    [JsonObjectCreationHandling(JsonObjectCreationHandling.Populate)]
    public AudioRouting Routing { get; } = new();

    [JsonIgnore] public int SourceChannels { get => _sourceChannels; private set => Set(ref _sourceChannels, value); }

    [JsonIgnore] public override string TypeName => Escena.L.T("Àudio");

    public override void ReloadFileInfo()
    {
        if (string.IsNullOrWhiteSpace(FilePath))
        {
            FileDuration = 0;
            ErrorMessage = null;
            return;
        }
        try
        {
            using var reader = AudioReaders.Open(FilePath);
            FileDuration = reader.TotalTime.TotalSeconds;
            SourceChannels = reader.WaveFormat.Channels;
            ErrorMessage = null;
        }
        catch (Exception ex)
        {
            FileDuration = 0;
            SourceChannels = 0;
            ErrorMessage = "No es pot obrir el fitxer: " + ex.Message;
        }
    }
}

public sealed class VideoCue : PlayableCue
{
    private Guid? _audioOutputId;

    /// <summary>Sortida d'àudio d'Escena per on surt el so del vídeo; null = el dispositiu predeterminat de Windows.</summary>
    public Guid? AudioOutputId
    {
        get => _audioOutputId;
        set { if (Set(ref _audioOutputId, value)) OnPropertyChanged(nameof(AudioOutputChoice)); }
    }

    /// <summary>Per al desplegable: <see cref="Guid.Empty"/> vol dir «predeterminat de Windows».</summary>
    [JsonIgnore]
    public Guid AudioOutputChoice
    {
        get => _audioOutputId ?? Guid.Empty;
        set => AudioOutputId = value == Guid.Empty ? null : value;
    }

    [JsonIgnore] public override string TypeName => Escena.L.T("Vídeo");

    [JsonIgnore] public override bool IsVisual => true;

    public override void ReloadFileInfo()
    {
        FileDuration = 0;
        if (string.IsNullOrWhiteSpace(FilePath))
        {
            ErrorMessage = null;
            return;
        }
        if (!File.Exists(FilePath))
        {
            ErrorMessage = "No es troba el fitxer: " + FilePath;
            return;
        }
        ErrorMessage = null;

        // La durada la llegeix el VLC en segon pla.
        string path = FilePath;
        var dispatcher = Application.Current?.Dispatcher;
        _ = Task.Run(async () =>
        {
            double seconds;
            string? error = null;
            try { seconds = await VlcCore.GetDurationAsync(path); }
            catch (Exception ex) { seconds = 0; error = ex.Message; }
            void apply()
            {
                if (path != FilePath) return;
                FileDuration = seconds;
                if (error != null) ErrorMessage = "No es pot llegir el vídeo: " + error;
            }
            if (dispatcher != null) _ = dispatcher.BeginInvoke(apply); else apply();
        });
    }
}

/// <summary>Imatge fixa a la sortida de vídeo, amb fosa des de negre i cap a negre.</summary>
public sealed class ImageCue : MediaCue
{
    private double _displayTime;

    /// <summary>Segons que es mostra (inclosa la fosa de sortida); 0 = fins que s'aturi.</summary>
    public double DisplayTime
    {
        get => _displayTime;
        set { if (Set(ref _displayTime, Math.Max(0, value))) NotifyDurationChanged(); }
    }

    [JsonIgnore] public override string TypeName => Escena.L.T("Imatge");

    [JsonIgnore] public override bool IsVisual => true;

    [JsonIgnore] public override double Duration => DisplayTime;

    public override void ReloadFileInfo()
    {
        if (string.IsNullOrWhiteSpace(FilePath))
        {
            ErrorMessage = null;
            return;
        }
        try
        {
            using var stream = File.OpenRead(FilePath);
            BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
            ErrorMessage = null;
        }
        catch (Exception ex)
        {
            ErrorMessage = "No es pot obrir la imatge: " + ex.Message;
        }
    }
}
