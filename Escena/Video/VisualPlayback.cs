using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Escena.Audio;
using Escena.Model;
using LibVLCSharp.Shared;
using Media = LibVLCSharp.Shared.Media;
using MediaPlayer = LibVLCSharp.Shared.MediaPlayer;

namespace Escena.Video;

/// <summary>Rampa de valor en el temps amb corba (per a l'opacitat i el volum).</summary>
internal sealed class Ramp
{
    private double _from, _to, _duration, _t;
    private FadeCurve _curve;

    public Ramp(double initial) => Value = initial;

    public double Value { get; private set; }
    public bool Active { get; private set; }

    public void Start(double to, double seconds, FadeCurve curve)
    {
        if (seconds <= 0)
        {
            Value = to;
            Active = false;
            return;
        }
        _from = Value;
        _to = to;
        _duration = seconds;
        _t = 0;
        _curve = curve;
        Active = true;
    }

    public void Advance(double dt)
    {
        if (!Active) return;
        _t += dt;
        Value = FadeCurves.Interpolate(_curve, _from, _to, _t / _duration);
        if (_t >= _duration)
        {
            Value = _to;
            Active = false;
        }
    }
}

/// <summary>
/// Base de les reproduccions visuals: una capa a la finestra de vídeo amb opacitat (fosa a negre)
/// i volum amb rampes, pausa i aturada. S'actualitza a cada fotograma de la interfície.
/// </summary>
internal abstract class VisualPlayback : IMediaPlayback
{
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private double _last;
    private bool _stopWhenRampsDone, _disposed;
    protected readonly VideoOutput Output;
    protected readonly FrameworkElement Layer;
    protected readonly Ramp Opacity, Volume;
    protected readonly Ramp Duck = new(1);
    protected volatile bool Finished;

    protected VisualPlayback(VideoOutput output, MediaCue cue, FrameworkElement? layer = null)
    {
        Output = output;
        Layer = layer ?? new Image { Stretch = Stretch.Uniform };
        double start = cue.FadeIn > 0 ? 0 : 1;
        Opacity = new Ramp(start);
        Volume = new Ramp(start);
        if (cue.FadeIn > 0)
        {
            Opacity.Start(1, cue.FadeIn, cue.FadeInCurve);
            Volume.Start(1, cue.FadeIn, cue.FadeInCurve);
        }
        Layer.Opacity = Opacity.Value;
        RenderOptions.SetBitmapScalingMode(Layer, BitmapScalingMode.HighQuality);
        output.Window?.AddLayer(Layer);
        CompositionTarget.Rendering += OnRendering;
    }

    public abstract double Position { get; }

    public bool IsFinished => Finished;

    public virtual bool Paused { get; set; }

    public void RampTo(float gain, double seconds, FadeCurve curve, bool stopAfter, bool picture = true)
    {
        Volume.Start(gain, seconds, curve);
        if (picture) Opacity.Start(Math.Min(1, gain), seconds, curve);
        if (stopAfter)
        {
            _stopWhenRampsDone = true;
            if (seconds <= 0) Stop();
        }
    }

    public void RampPan(double pan, double seconds, FadeCurve curve, bool stopAfter)
    {
        // El vídeo no té panorama; si s'ha de parar en acabar, es para quan acabaria la fosa.
        if (stopAfter) RampTo((float)Volume.Value, seconds, curve, stopAfter: true, picture: false);
    }

    public void SetDuck(double gain, double seconds) => Duck.Start(gain, seconds, FadeCurve.Linear);

    /// <summary>La cue s'ha tret de la pantalla per una altra (p. ex. un text nou): fosa curta i fora.</summary>
    public void FadeAway(double seconds) => RampTo(0, seconds, FadeCurve.Linear, stopAfter: true);

    public virtual bool Devamp() => false;

    public virtual bool HasPendingLoop => false;

    public void Stop()
    {
        Finished = true;
        Layer.Visibility = Visibility.Collapsed;
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        double now = _clock.Elapsed.TotalSeconds;
        double dt = Paused ? 0 : now - _last;
        _last = now;
        if (Finished) return;

        Opacity.Advance(dt);
        Volume.Advance(dt);
        Duck.Advance(dt);
        Layer.Opacity = Opacity.Value;
        Tick(dt);

        if (_stopWhenRampsDone && !Opacity.Active && !Volume.Active) Stop();
    }

    /// <summary>Feina de cada fotograma (copiar el vídeo, aplicar el volum…).</summary>
    protected abstract void Tick(double dt);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Finished = true;
        CompositionTarget.Rendering -= OnRendering;
        Output.Window?.RemoveLayer(Layer);
        Release();
    }

    protected abstract void Release();
}

internal sealed class ImagePlayback : VisualPlayback
{
    private readonly ImageCue _cue;
    private double _elapsed;
    private bool _endFadeStarted;

    public ImagePlayback(VideoOutput output, ImageCue cue, double offset = 0) : base(output, cue)
    {
        _cue = cue;
        _elapsed = offset;
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.UriSource = new Uri(cue.FilePath);
        bitmap.EndInit();
        bitmap.Freeze();
        ImageLayer.Source = bitmap;
    }

    private Image ImageLayer => (Image)Layer;

    public override double Position => _elapsed;

    protected override void Tick(double dt)
    {
        _elapsed += dt;
        double total = _cue.DisplayTime;
        if (total <= 0) return;
        // Amb temps fix, la fosa de sortida acaba just quan s'acaba el temps.
        if (!_endFadeStarted && _cue.FadeOut > 0 && _elapsed >= total - _cue.FadeOut)
        {
            _endFadeStarted = true;
            RampTo(0, Math.Max(0, total - _elapsed), _cue.FadeOutCurve, stopAfter: true);
        }
        if (_elapsed >= total) Stop();
    }

    protected override void Release() => ImageLayer.Source = null;
}

/// <summary>
/// Vídeo amb el VLC. Els fotogrames es copien a una imatge WPF (en lloc d'una finestra nativa)
/// perquè així es poden fondre a negre i superposar com qualsevol altra capa.
/// </summary>
internal sealed class VideoPlayback : VisualPlayback
{
    private const int MaxWidth = 1920;

    private readonly VideoCue _cue;
    private readonly Media _media;
    private readonly MediaPlayer _player;
    private readonly SemaphoreSlim _frameLock = new(1, 1);
    private readonly MediaPlayer.LibVLCVideoFormatCb _formatCb;
    private readonly MediaPlayer.LibVLCVideoCleanupCb _cleanupCb;
    private readonly MediaPlayer.LibVLCVideoLockCb _lockCb;
    private readonly MediaPlayer.LibVLCVideoUnlockCb _unlockCb;
    private readonly MediaPlayer.LibVLCVideoDisplayCb _displayCb;
    private IntPtr _buffer;
    private int _width, _height, _pitch;
    private volatile bool _frameReady, _sizeChanged;
    private WriteableBitmap? _bitmap;
    private int _lastVolume = -1;
    private bool _paused;
    private readonly VlcAudioBridge? _bridge;
    private readonly GainRampProvider? _bridgeGain;

    /// <summary>El so surt per una sortida d'àudio d'Escena (i no pel dispositiu predeterminat de Windows).</summary>
    internal bool UsesEscenaAudio => _bridge != null;

    internal double BufferedAudio => _bridge?.Buffered ?? 0;

    public VideoPlayback(VideoOutput output, VideoCue cue, double offset = 0) : base(output, cue)
    {
        _cue = cue;
        _media = new Media(VlcCore.LibVLC, cue.FilePath, FromType.FromPath);
        var inv = CultureInfo.InvariantCulture;
        double start = cue.StartAt + offset;
        if (offset > 0 && cue.Loop && cue.Duration > 0) start = cue.StartAt + offset % cue.Duration;
        if (start > 0) _media.AddOption(":start-time=" + start.ToString(inv));
        if (cue.EndAt > cue.StartAt) _media.AddOption(":stop-time=" + cue.EndAt.ToString(inv));
        if (cue.Loop) _media.AddOption(":input-repeat=65535");

        _player = new MediaPlayer(_media) { EnableHardwareDecoding = true };
        _formatCb = OnFormat;
        _cleanupCb = OnCleanup;
        _lockCb = OnLock;
        _unlockCb = OnUnlock;
        _displayCb = OnDisplay;
        _player.SetVideoFormatCallbacks(_formatCb, _cleanupCb);
        _player.SetVideoCallbacks(_lockCb, _unlockCb, _displayCb);

        // So per una sortida d'Escena: el VLC ens dona les mostres i les afegim al seu mesclador.
        var audio = output.ResolveAudio(cue);
        if (audio != null)
        {
            _bridge = new VlcAudioBridge(_player, audio.MixFormat!.SampleRate, audio.LatencyMs);
            _bridgeGain = audio.AddExternal(_bridge);
        }

        _player.EndReached += (_, _) => { Finished = true; _bridge?.MarkEnded(); };
        _player.EncounteredError += (_, _) => { Finished = true; _bridge?.MarkEnded(); };
        _player.Playing += (_, _) => _lastVolume = -1; // torna a aplicar el volum quan arrenca l'àudio

        ApplyVolume();
        if (!_player.Play())
        {
            _bridgeGain?.Stop();
            throw new InvalidOperationException("El VLC no ha pogut obrir el vídeo.");
        }
    }

    public override double Position
    {
        get
        {
            long t = _player.Time;
            return t < 0 ? 0 : Math.Max(0, t / 1000.0 - _cue.StartAt);
        }
    }

    public override bool Paused
    {
        get => _paused;
        set
        {
            if (_paused == value) return;
            _paused = value;
            _player.SetPause(value);
            if (_bridgeGain != null) _bridgeGain.Paused = value;
        }
    }

    protected override void Tick(double dt)
    {
        ApplyVolume();

        if (_sizeChanged)
        {
            _sizeChanged = false;
            _bitmap = new WriteableBitmap(_width, _height, 96, 96, PixelFormats.Bgr32, null);
            ((Image)Layer).Source = _bitmap;
        }
        if (_frameReady && _bitmap != null && _frameLock.Wait(0))
        {
            try
            {
                if (_buffer != IntPtr.Zero && _bitmap.PixelWidth == _width && _bitmap.PixelHeight == _height)
                    _bitmap.WritePixels(new Int32Rect(0, 0, _width, _height), _buffer, _pitch * _height, _pitch);
                _frameReady = false;
            }
            finally { _frameLock.Release(); }
        }
    }

    private void ApplyVolume()
    {
        if (_bridgeGain != null)
        {
            // La sortida d'àudio ja hi posa el volum general i el seu ajust.
            _bridgeGain.MasterGain = (float)(Volume.Value * Duck.Value * AudioMath.DbToGain(_cue.VolumeDb));
            return;
        }
        double gain = Volume.Value * Duck.Value * AudioMath.DbToGain(_cue.VolumeDb) * Output.VolumeFactor;
        int vol = (int)Math.Round(Math.Clamp(gain, 0, 2) * 100);
        if (vol == _lastVolume) return;
        _lastVolume = vol;
        _player.Volume = vol;
    }

    // ---- Callbacks del VLC (fil del descodificador) ----

    private uint OnFormat(ref IntPtr opaque, IntPtr chroma, ref uint width, ref uint height, ref uint pitches, ref uint lines)
    {
        Marshal.Copy(new[] { (byte)'R', (byte)'V', (byte)'3', (byte)'2' }, 0, chroma, 4);
        uint w = width, h = height;
        if (w == 0 || h == 0) return 0;
        if (w > MaxWidth)
        {
            h = (uint)Math.Max(2, Math.Round(h * (double)MaxWidth / w));
            w = MaxWidth;
        }
        width = w;
        height = h;
        pitches = w * 4;
        lines = h;

        _frameLock.Wait();
        try
        {
            if (_buffer != IntPtr.Zero) Marshal.FreeHGlobal(_buffer);
            _width = (int)w;
            _height = (int)h;
            _pitch = (int)w * 4;
            _buffer = Marshal.AllocHGlobal(_pitch * _height);
            _sizeChanged = true;
        }
        finally { _frameLock.Release(); }
        return 1;
    }

    private void OnCleanup(ref IntPtr opaque) { }

    private IntPtr OnLock(IntPtr opaque, IntPtr planes)
    {
        _frameLock.Wait();
        Marshal.WriteIntPtr(planes, _buffer);
        return IntPtr.Zero;
    }

    private void OnUnlock(IntPtr opaque, IntPtr picture, IntPtr planes) => _frameLock.Release();

    private void OnDisplay(IntPtr opaque, IntPtr picture) => _frameReady = true;

    protected override void Release()
    {
        ((Image)Layer).Source = null;
        _bitmap = null;
        _bridgeGain?.Stop();
        // Aturar el VLC pot trigar una mica: es fa fora del fil de la interfície.
        var player = _player;
        var media = _media;
        Task.Run(() =>
        {
            try { player.Stop(); } catch { /* ignorat */ }
            player.Dispose();
            media.Dispose();
            _frameLock.Wait();
            if (_buffer != IntPtr.Zero) Marshal.FreeHGlobal(_buffer);
            _buffer = IntPtr.Zero;
            _frameLock.Release();
        });
    }
}

/// <summary>Text (títol, rètol o sobretítol) com a capa, escalat des d'una pantalla de referència de 1920×1080.</summary>
internal sealed class TextPlayback : VisualPlayback
{
    private readonly TextCue _cue;
    private double _elapsed;
    private bool _endFadeStarted;

    public TextPlayback(VideoOutput output, TextCue cue, double offset = 0) : base(output, cue, BuildLayer(cue))
    {
        _cue = cue;
        _elapsed = offset;
    }

    public TextCue Cue => _cue;

    private static FrameworkElement BuildLayer(TextCue cue)
    {
        Brush foreground;
        try { foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(cue.Color)); }
        catch { foreground = Brushes.White; }
        var text = new TextBlock
        {
            Text = cue.Text, FontSize = cue.FontSize, Foreground = foreground, FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, Margin = new Thickness(60, 24, 60, 24),
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 10, ShadowDepth = 2, Opacity = 0.85 },
        };
        var band = new Border
        {
            Child = text,
            Background = new SolidColorBrush(Color.FromArgb((byte)Math.Round(cue.BackgroundOpacity * 255), 0, 0, 0)),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = cue.Placement switch
            {
                TextPlacement.Top => VerticalAlignment.Top,
                TextPlacement.Bottom => VerticalAlignment.Bottom,
                _ => VerticalAlignment.Center,
            },
            Margin = new Thickness(0, 50, 0, 50),
        };
        var page = new Grid { Width = 1920, Height = 1080 };
        page.Children.Add(band);
        return new Viewbox { Stretch = Stretch.Uniform, Child = page };
    }

    public override double Position => _elapsed;

    protected override void Tick(double dt)
    {
        _elapsed += dt;
        double total = _cue.DisplayTime;
        if (total <= 0) return;
        if (!_endFadeStarted && _cue.FadeOut > 0 && _elapsed >= total - _cue.FadeOut)
        {
            _endFadeStarted = true;
            RampTo(0, Math.Max(0, total - _elapsed), _cue.FadeOutCurve, stopAfter: true);
        }
        if (_elapsed >= total) Stop();
    }

    protected override void Release() { }
}
