using Escena.Model;
using NAudio.Wave;

namespace Escena.Audio;

/// <summary>Punt únic per obrir fitxers d'àudio: per afegir un format (p. ex. OGG) només cal tocar aquí.</summary>
public static class AudioReaders
{
    public static readonly string[] Extensions = { ".wav", ".mp3", ".flac", ".aif", ".aiff", ".m4a", ".aac", ".wma" };

    public static string DialogFilter =>
        "Fitxers d'àudio|" + string.Join(";", Extensions.Select(e => "*" + e)) + "|Tots els fitxers|*.*";

    public static bool IsSupported(string path) =>
        Extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    /// <summary>Retorna un lector que és alhora WaveStream (posició) i ISampleProvider (mostres float).</summary>
    public static WaveStream Open(string path) => new AudioFileReader(path);
}

/// <summary>Un tros que es repeteix dins del fitxer, en segons del fitxer. Repeats 0 = fins que s'alliberi.</summary>
public readonly record struct SliceSpec(double Start, double End, int Repeats);

/// <summary>
/// Llegeix el fitxer entre [inici, final] i aplica els bucles: els trossos (slices) es repeteixen
/// fins que s'acaben les repeticions o fins que <see cref="Devamp"/> els allibera; el bucle de tota la cue
/// torna a l'inici en arribar al final.
/// </summary>
public sealed class TrimLoopProvider : ISampleProvider
{
    private sealed class SliceState
    {
        public required SliceSpec Spec;
        public int Remaining;   // -1 = infinit
        public bool Released;   // «Surt del bucle»: en arribar al final, continua
        public bool Passed;     // ja l'hem deixat enrere en aquesta passada
    }

    private readonly WaveStream _stream;
    private readonly ISampleProvider _samples;
    private readonly TimeSpan _start;
    private readonly TimeSpan? _end;
    private readonly SliceState[] _slices;
    private readonly object _lock = new();
    private volatile bool _loop;

    /// <param name="offset">Segons després de l'inici on comença a sonar (per «Comença des de…»).</param>
    public TrimLoopProvider(WaveStream stream, double startAt, double endAt, bool loop, IEnumerable<SliceSpec>? slices, double offset)
        : this(stream, startAt, endAt, loop, slices)
    {
        if (offset <= 0) return;
        double end = endAt > startAt ? Math.Min(endAt, stream.TotalTime.TotalSeconds) : stream.TotalTime.TotalSeconds;
        double length = end - startAt;
        if (length <= 0) return;
        if (loop) offset %= length;
        double pos = Math.Min(startAt + offset, end);
        lock (_lock) _stream.CurrentTime = TimeSpan.FromSeconds(pos);
    }

    public TrimLoopProvider(WaveStream stream, double startAt, double endAt, bool loop, IEnumerable<SliceSpec>? slices = null)
    {
        _stream = stream;
        _samples = (ISampleProvider)stream;
        _start = TimeSpan.FromSeconds(startAt);
        _end = endAt > startAt ? TimeSpan.FromSeconds(endAt) : null;
        _loop = loop;
        _slices = (slices ?? Enumerable.Empty<SliceSpec>())
            .Where(s => s.End > s.Start + 0.05 && s.End > startAt && (_end == null || s.Start < endAt))
            .OrderBy(s => s.Start)
            .Select(s => new SliceState { Spec = s })
            .ToArray();
        ResetSlices();
        if (startAt > 0) _stream.CurrentTime = _start;
    }

    public WaveFormat WaveFormat => _samples.WaveFormat;

    public bool Loop { get => _loop; set => _loop = value; }

    /// <summary>Hi ha algun tros en bucle que encara no s'ha alliberat.</summary>
    public bool HasPendingLoop
    {
        get { lock (_lock) return _slices.Any(s => !s.Passed && !s.Released && s.Remaining != 0); }
    }

    /// <summary>Allibera el tros on som (o el proper): en arribar al seu final continuarà endavant.</summary>
    public bool Devamp()
    {
        lock (_lock)
        {
            double pos = _stream.CurrentTime.TotalSeconds;
            var slice = _slices.FirstOrDefault(s => !s.Passed && !s.Released && s.Remaining != 0 && s.Spec.End >= pos - 0.0005);
            if (slice == null) return false;
            slice.Released = true;
            return true;
        }
    }

    private void ResetSlices()
    {
        foreach (var s in _slices)
        {
            s.Remaining = s.Spec.Repeats == 0 ? -1 : s.Spec.Repeats;
            s.Released = false;
            s.Passed = false;
        }
    }

    public int Read(float[] buffer, int offset, int count)
    {
        lock (_lock)
        {
            int channels = WaveFormat.Channels;
            int total = 0;
            int emptyReads = 0;

            while (total < count)
            {
                double pos = _stream.CurrentTime.TotalSeconds;
                // Tolerància de mig mil·lisegon: just al final del tros encara hi som.
                var slice = _slices.FirstOrDefault(s => !s.Passed && s.Spec.End >= pos - 0.0005);
                double boundary = Math.Min(slice?.Spec.End ?? double.MaxValue, _end?.TotalSeconds ?? double.MaxValue);

                int want = count - total;
                if (boundary < double.MaxValue)
                {
                    long remainingSamples = (long)((boundary - pos) * WaveFormat.SampleRate) * channels;
                    if (remainingSamples <= 0)
                    {
                        if (slice != null && slice.Spec.End <= boundary + 1e-9 && OnSliceEnd(slice)) continue;
                        if (!Rewind(ref emptyReads)) break;
                        continue;
                    }
                    want = (int)Math.Min(want, remainingSamples);
                }

                int n = _samples.Read(buffer, offset + total, want);
                if (n == 0)
                {
                    if (!Rewind(ref emptyReads)) break;
                    continue;
                }
                emptyReads = 0;
                total += n;
            }
            return total;
        }
    }

    /// <summary>Arribada al final d'un tros: torna a l'inici si encara s'ha de repetir. Retorna si cal continuar llegint.</summary>
    private bool OnSliceEnd(SliceState slice)
    {
        if (!slice.Released && slice.Remaining != 0)
        {
            if (slice.Remaining > 0) slice.Remaining--;
            _stream.CurrentTime = TimeSpan.FromSeconds(slice.Spec.Start);
            return true;
        }
        slice.Passed = true;
        // Si el final del tros és també el final de la cue, que ho resolgui el bucle general.
        return _end == null || slice.Spec.End < _end.Value.TotalSeconds - 1e-9;
    }

    private bool Rewind(ref int emptyReads)
    {
        if (!_loop || ++emptyReads > 2) return false;
        _stream.CurrentTime = _start;
        ResetSlices();
        return true;
    }
}

/// <summary>
/// Volum, panorama i encaminament d'una cue cap als canals de la sortida, amb rampes (foses amb corba),
/// pausa i aturada. Cada canal de la cue arriba a cada canal de sortida amb un nivell propi (matriu de
/// punts de creu); avui la matriu és estèreo i el panorama la modifica, però està feta perquè més endavant
/// una cue pugui enviar a diverses sortides i targetes multicanal amb nivells independents.
/// Quan s'atura retorna 0 mostres i el mesclador el treu.
/// </summary>
public sealed class GainRampProvider : ISampleProvider
{
    private readonly ISampleProvider _source;
    private readonly int _inChannels, _outChannels;
    private readonly object _lock = new();
    private float[] _sourceBuffer = Array.Empty<float>();
    private float[,] _matrix;

    // Volum (fader) amb rampa
    private float _gain, _gainFrom, _gainTo;
    private long _gainTotal, _gainDone;
    private FadeCurve _gainCurve;
    private bool _gainRamping;

    // Panorama amb rampa
    private float _pan, _panFrom, _panTo;
    private long _panTotal, _panDone;
    private FadeCurve _panCurve;
    private bool _panRamping;

    // Ducking (lineal, independent de les foses)
    private float _duck = 1f, _duckFrom = 1f, _duckTo = 1f;
    private long _duckTotal, _duckDone;
    private bool _duckRamping;

    private bool _stopWhenDone;
    private volatile bool _paused, _stopped, _finished;

    public GainRampProvider(ISampleProvider source, float initialGain, int outputChannels = 2)
    {
        _source = source;
        _inChannels = source.WaveFormat.Channels;
        _outChannels = Math.Max(1, outputChannels);
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, _outChannels);
        _gain = initialGain;
        _matrix = DefaultMatrix(_inChannels, _outChannels);
    }

    public WaveFormat WaveFormat { get; }

    /// <summary>Volum base de la cue (lineal).</summary>
    public float MasterGain { get; set; } = 1f;

    public bool Paused { get => _paused; set => _paused = value; }

    public bool IsFinished => _finished;

    public float CurrentGain { get { lock (_lock) return _gain; } }

    public float CurrentPan { get { lock (_lock) return _pan; } }

    /// <summary>Encaminament per defecte: mono a tots dos costats, estèreo L→1 i R→2, la resta canal a canal.</summary>
    public static float[,] DefaultMatrix(int inChannels, int outChannels)
    {
        var m = new float[inChannels, outChannels];
        if (inChannels == 1)
        {
            for (int o = 0; o < Math.Min(2, outChannels); o++) m[0, o] = 1f;
        }
        else
        {
            for (int i = 0; i < Math.Min(inChannels, outChannels); i++) m[i, i] = 1f;
        }
        return m;
    }

    /// <summary>Canvia els nivells dels punts de creu [canal de la cue, canal de sortida].</summary>
    public void SetRouting(float[,] matrix)
    {
        if (matrix.GetLength(0) != _inChannels || matrix.GetLength(1) != _outChannels)
            throw new ArgumentException("La matriu no coincideix amb els canals.");
        lock (_lock) _matrix = (float[,])matrix.Clone();
    }

    /// <summary>Balanç immediat: −1 esquerra … +1 dreta.</summary>
    public void SetPan(double pan)
    {
        lock (_lock)
        {
            _pan = (float)Math.Clamp(pan, -1, 1);
            _panRamping = false;
        }
    }

    public void RampTo(float target, double seconds, FadeCurve curve = FadeCurve.Linear, bool stopAfter = false)
    {
        lock (_lock)
        {
            long frames = (long)(seconds * WaveFormat.SampleRate);
            if (stopAfter) _stopWhenDone = true;
            if (frames <= 0)
            {
                _gain = target;
                _gainRamping = false;
                CheckDone();
                return;
            }
            _gainFrom = _gain;
            _gainTo = target;
            _gainTotal = frames;
            _gainDone = 0;
            _gainCurve = curve;
            _gainRamping = true;
        }
    }

    public void RampPan(double target, double seconds, FadeCurve curve = FadeCurve.Linear, bool stopAfter = false)
    {
        lock (_lock)
        {
            long frames = (long)(seconds * WaveFormat.SampleRate);
            if (stopAfter) _stopWhenDone = true;
            float to = (float)Math.Clamp(target, -1, 1);
            if (frames <= 0)
            {
                _pan = to;
                _panRamping = false;
                CheckDone();
                return;
            }
            _panFrom = _pan;
            _panTo = to;
            _panTotal = frames;
            _panDone = 0;
            _panCurve = curve == FadeCurve.Exponential ? FadeCurve.Linear : curve; // el panorama no va en dB
            _panRamping = true;
        }
    }

    /// <summary>Ducking: multiplica el volum per <paramref name="target"/> amb una rampa lineal.</summary>
    public void RampDuck(float target, double seconds)
    {
        lock (_lock)
        {
            long frames = (long)(seconds * WaveFormat.SampleRate);
            if (frames <= 0)
            {
                _duck = target;
                _duckRamping = false;
                return;
            }
            _duckFrom = _duck;
            _duckTo = target;
            _duckTotal = frames;
            _duckDone = 0;
            _duckRamping = true;
        }
    }

    public float CurrentDuck { get { lock (_lock) return _duck; } }

    private void CheckDone()
    {
        if (_stopWhenDone && !_gainRamping && !_panRamping) _stopped = true;
    }

    public void Stop() => _stopped = true;

    public int Read(float[] buffer, int offset, int count)
    {
        if (_finished) return 0;
        if (_stopped)
        {
            _finished = true;
            return 0;
        }
        if (_paused)
        {
            Array.Clear(buffer, offset, count);
            return count;
        }

        int frames = count / _outChannels;
        int need = frames * _inChannels;
        if (_sourceBuffer.Length < need) _sourceBuffer = new float[need];
        int read = _source.Read(_sourceBuffer, 0, need);
        int framesRead = read / _inChannels;

        lock (_lock)
        {
            float master = MasterGain;
            var m = _matrix;
            for (int f = 0; f < framesRead; f++)
            {
                if (_gainRamping)
                {
                    _gainDone++;
                    _gain = (float)FadeCurves.Interpolate(_gainCurve, _gainFrom, _gainTo, (double)_gainDone / _gainTotal);
                    if (_gainDone >= _gainTotal)
                    {
                        _gain = _gainTo;
                        _gainRamping = false;
                        CheckDone();
                    }
                }
                if (_panRamping)
                {
                    _panDone++;
                    _pan = (float)FadeCurves.Interpolate(_panCurve, _panFrom, _panTo, (double)_panDone / _panTotal);
                    if (_panDone >= _panTotal)
                    {
                        _pan = _panTo;
                        _panRamping = false;
                        CheckDone();
                    }
                }

                if (_duckRamping)
                {
                    _duckDone++;
                    _duck = _duckFrom + (_duckTo - _duckFrom) * (float)_duckDone / _duckTotal;
                    if (_duckDone >= _duckTotal)
                    {
                        _duck = _duckTo;
                        _duckRamping = false;
                    }
                }

                float g = _gain * master * _duck;
                float left = _pan > 0 ? 1 - _pan : 1f;   // balanç sobre els dos primers canals
                float right = _pan < 0 ? 1 + _pan : 1f;
                int si = f * _inChannels;
                int oi = offset + f * _outChannels;
                for (int o = 0; o < _outChannels; o++)
                {
                    float sum = 0;
                    for (int i = 0; i < _inChannels; i++) sum += _sourceBuffer[si + i] * m[i, o];
                    float balance = o == 0 ? left : o == 1 ? right : 1f;
                    buffer[oi + o] = sum * g * balance;
                }
            }
        }

        int written = framesRead * _outChannels;
        // El mesclador retira l'entrada si rep menys mostres de les demanades.
        if (written < count) _finished = true;
        return written;
    }
}

/// <summary>Volum general d'una sortida (després del mesclador).</summary>
public sealed class MasterGainProvider : ISampleProvider
{
    private readonly ISampleProvider _source;
    private volatile float _gain = 1f;

    public MasterGainProvider(ISampleProvider source) => _source = source;

    public WaveFormat WaveFormat => _source.WaveFormat;

    public float Gain { get => _gain; set => _gain = value; }

    public int Read(float[] buffer, int offset, int count)
    {
        int n = _source.Read(buffer, offset, count);
        float g = _gain;
        if (g != 1f)
            for (int i = 0; i < n; i++) buffer[offset + i] *= g;
        return n;
    }
}
