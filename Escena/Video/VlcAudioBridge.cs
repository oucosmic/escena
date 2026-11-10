using System.Runtime.InteropServices;
using NAudio.Wave;
using MediaPlayer = LibVLCSharp.Shared.MediaPlayer;

namespace Escena.Video;

/// <summary>
/// Porta el so d'un vídeo del VLC al mesclador d'una sortida d'àudio d'Escena (en lloc del dispositiu
/// predeterminat de Windows).
/// El VLC entrega el so per avançat (fins a uns segons) i cada tros porta el moment en què s'ha de sentir
/// (en el rellotge del VLC). Aquí es guarden els trossos i, en llegir, se sincronitzen amb aquest rellotge
/// tenint en compte la latència de la sortida: si va endarrerit se salta, si va avançat espera amb silenci.
/// Així la imatge i el so van junts.
/// </summary>
public sealed class VlcAudioBridge : ISampleProvider
{
    private const int Channels = 2;
    private const long LateToleranceUs = 40_000;  // endarreriment que s'accepta abans de saltar
    private const long EarlyToleranceUs = 15_000; // avançament que s'accepta abans d'esperar
    private const long MaxQueuedUs = 4_000_000;

    private sealed class Chunk
    {
        public required float[] Data;
        public required long Pts;
        public int Pos;
    }

    private readonly Queue<Chunk> _chunks = new();
    private readonly object _lock = new();
    private readonly int _rate;
    private short[] _scratch = Array.Empty<short>();
    private volatile bool _ended;
    private long _queuedSamples;

    // Cal guardar els delegats: el VLC els crida des del seu fil i no poden ser recollits.
    private readonly MediaPlayer.LibVLCAudioPlayCb _play;
    private readonly MediaPlayer.LibVLCAudioPauseCb _pause;
    private readonly MediaPlayer.LibVLCAudioResumeCb _resume;
    private readonly MediaPlayer.LibVLCAudioFlushCb _flush;
    private readonly MediaPlayer.LibVLCAudioDrainCb _drain;

    /// <param name="latencyMs">Temps entre que el mesclador llegeix i el so surt pels altaveus.</param>
    public VlcAudioBridge(MediaPlayer player, int sampleRate, int latencyMs)
    {
        _rate = sampleRate;
        LatencyUs = Math.Max(0, latencyMs) * 1000L;
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, Channels);
        _play = OnPlay;
        _pause = (_, _) => { };
        _resume = (_, _) => { };
        _flush = (_, _) => Clear();
        _drain = _ => { };
        // Enters de 16 bits en l'ordre de la màquina: el format que el VLC entrega amb seguretat.
        player.SetAudioFormat("S16N", (uint)sampleRate, Channels);
        player.SetAudioCallbacks(_play, _pause, _resume, _flush, _drain);
    }

    public WaveFormat WaveFormat { get; }

    public long LatencyUs { get; }

    /// <summary>Rellotge per sincronitzar (el del VLC); es pot canviar a les proves.</summary>
    public Func<long> Clock { get; set; } = () => VlcCore.LibVLC.Clock;

    /// <summary>Segons d'àudio rebuts i encara no llegits.</summary>
    public double Buffered
    {
        get { lock (_lock) return _queuedSamples / (double)(_rate * Channels); }
    }

    /// <summary>Mostres saltades (endarreriment) i silencis afegits (avançament), per al diagnòstic.</summary>
    public long DroppedFrames { get; private set; }
    public long WaitedFrames { get; private set; }

    /// <summary>El vídeo s'ha acabat: quan la cua es buidi, el mesclador deixa de llegir.</summary>
    public void MarkEnded() => _ended = true;

    public void Clear()
    {
        lock (_lock)
        {
            _chunks.Clear();
            _queuedSamples = 0;
        }
    }

    private void OnPlay(IntPtr data, IntPtr samples, uint count, long pts)
    {
        int n = (int)count * Channels;
        if (n <= 0) return;
        if (_scratch.Length < n) _scratch = new short[n];
        Marshal.Copy(samples, _scratch, 0, n);
        var f = new float[n];
        for (int i = 0; i < n; i++) f[i] = _scratch[i] / 32768f;
        Push(f, pts);
    }

    /// <summary>Afegeix un tros que s'ha de sentir al moment <paramref name="pts"/> (µs del rellotge).</summary>
    public void Push(float[] interleaved, long pts)
    {
        lock (_lock)
        {
            _chunks.Enqueue(new Chunk { Data = interleaved, Pts = pts });
            _queuedSamples += interleaved.Length;
            // Seguretat: no acumular més de 4 s (no hauria de passar).
            while (_queuedSamples > MaxQueuedUs * _rate * Channels / 1_000_000 && _chunks.Count > 1)
            {
                var old = _chunks.Dequeue();
                _queuedSamples -= old.Data.Length - old.Pos;
            }
        }
    }

    public int Read(float[] buffer, int offset, int count)
    {
        count -= count % Channels;
        int written = 0;
        lock (_lock)
        {
            if (_chunks.Count == 0 && _ended) return 0;
            // Moment en què se sentirà la primera mostra d'aquesta lectura.
            long now = Clock() + LatencyUs;
            while (written < count && _chunks.Count > 0)
            {
                var c = _chunks.Peek();
                long due = c.Pts + (c.Pos / Channels) * 1_000_000L / _rate;
                long at = now + (written / Channels) * 1_000_000L / _rate;
                long diff = due - at; // > 0: el so arriba abans d'hora; < 0: tard

                if (diff > EarlyToleranceUs)
                {
                    // Massa d'hora: silenci fins que toqui (o fins al final d'aquesta lectura).
                    int frames = (int)Math.Min((count - written) / Channels, diff * _rate / 1_000_000L);
                    if (frames <= 0) break;
                    Array.Clear(buffer, offset + written, frames * Channels);
                    written += frames * Channels;
                    WaitedFrames += frames;
                    continue;
                }
                if (diff < -LateToleranceUs)
                {
                    // Tard: salta el que ja hauria d'haver sonat.
                    int skip = (int)Math.Min((c.Data.Length - c.Pos) / Channels, -diff * _rate / 1_000_000L) * Channels;
                    if (skip <= 0) skip = Channels;
                    c.Pos += skip;
                    _queuedSamples -= skip;
                    DroppedFrames += skip / Channels;
                    if (c.Pos >= c.Data.Length) _chunks.Dequeue();
                    continue;
                }
                int n = Math.Min(count - written, c.Data.Length - c.Pos);
                Array.Copy(c.Data, c.Pos, buffer, offset + written, n);
                c.Pos += n;
                written += n;
                _queuedSamples -= n;
                if (c.Pos >= c.Data.Length) _chunks.Dequeue();
            }
        }
        // Encara no ha arribat res (o el vídeo està en pausa): silenci.
        if (written < count) Array.Clear(buffer, offset + written, count - written);
        return count;
    }
}
