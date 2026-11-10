using Escena.Audio;
using Escena.Model;

namespace Escena.Video;

/// <summary>Sortida de vídeo: manté oberta la finestra negra a la pantalla escollida.</summary>
public sealed class VideoOutput : IMediaOutput
{
    private readonly VideoOutputConfig _config;
    private readonly List<TextPlayback> _texts = new();
    private double _masterDb;

    public VideoOutput(VideoOutputConfig config) => _config = config;

    internal VideoWindow? Window { get; private set; }

    /// <summary>Troba una sortida d'àudio d'Escena (per fer-hi sortir el so dels vídeos).</summary>
    public Func<Guid, AudioOutput?>? AudioResolver { get; set; }

    /// <summary>Sortida d'àudio oberta per al so d'aquest vídeo; null = el dispositiu predeterminat de Windows.</summary>
    internal AudioOutput? ResolveAudio(VideoCue cue) =>
        cue.AudioOutputId is Guid id && AudioResolver?.Invoke(id) is { MixFormat: not null } audio ? audio : null;

    public Guid Id => _config.Id;
    public string Description { get; private set; } = "";
    public string? Warning { get; private set; }

    /// <summary>Factor de volum del so dels vídeos (volum general + ajust de la sortida).</summary>
    internal double VolumeFactor => AudioMath.DbToGain(_masterDb + _config.TrimDb);

    public bool CanPlay(MediaCue cue) => cue.IsVisual;

    public void Open()
    {
        Close();
        var monitor = Monitors.Resolve(_config.ScreenId, out bool found);
        bool fullscreen = _config.Fullscreen;
        if (!found)
        {
            // Sense el projector no es tapa mai la pantalla de l'operador.
            if (monitor.IsPrimary) fullscreen = false;
            Warning = $"«{_config.Name}»: la pantalla escollida no hi és; s'usa la {monitor.Label.ToLowerInvariant()}"
                      + (monitor.IsPrimary ? " en finestra." : ".");
        }
        Window = new VideoWindow(_config.Name, monitor, fullscreen);
        Window.SetCorrection(_config.Corners);
        Window.Show();
        Description = $"{_config.Name}: {monitor.Label}{(fullscreen ? ", pantalla completa" : ", finestra")}";
    }

    public IMediaPlayback Play(MediaCue cue, double offset = 0)
    {
        if (Window == null) throw new InvalidOperationException("La sortida de vídeo no està oberta.");
        if (cue is TextCue text)
        {
            _texts.RemoveAll(t => t.IsFinished);
            if (text.ReplaceTexts)
                foreach (var old in _texts) old.FadeAway(Math.Max(0.05, Math.Min(text.FadeIn, old.Cue.FadeOut)));
            var playback = new TextPlayback(this, text, offset);
            _texts.Add(playback);
            return playback;
        }
        if (string.IsNullOrWhiteSpace(cue.FilePath)) throw new InvalidOperationException("La cue no té cap fitxer.");
        return cue switch
        {
            VideoCue video => new VideoPlayback(this, video, offset),
            ImageCue image => new ImagePlayback(this, image, offset),
            _ => throw new NotSupportedException("Aquesta sortida només mostra vídeo, imatges i text."),
        };
    }

    public void SetMasterVolume(double masterDb) => _masterDb = masterDb;

    public void Test() => Window?.Identify(_config.Name);

    /// <summary>Aplica en directe una correcció de cantonades (mentre s'ajusta el projector).</summary>
    public void PreviewCorrection(double[] corners, bool showGrid)
    {
        Window?.SetCorrection(corners);
        Window?.ShowTestGrid(showGrid);
    }

    public void Close()
    {
        _texts.Clear();
        Window?.ForceClose();
        Window = null;
    }

    public void Dispose() => Close();
}
