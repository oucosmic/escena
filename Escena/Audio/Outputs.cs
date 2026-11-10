using Escena.Model;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace Escena.Audio;

/// <summary>Una reproducció en curs d'una cue de fitxer, de micròfon o visual.</summary>
public interface IMediaPlayback : IDisposable
{
    /// <summary>Segons des del punt d'inici de la cue.</summary>
    double Position { get; }
    bool IsFinished { get; }
    bool Paused { get; set; }
    /// <param name="picture">En cues visuals, si la fosa també afecta la imatge.</param>
    void RampTo(float gain, double seconds, FadeCurve curve, bool stopAfter, bool picture = true);
    /// <summary>Fosa de panorama (−1 esquerra … +1 dreta). Les cues sense panorama l'ignoren.</summary>
    void RampPan(double pan, double seconds, FadeCurve curve, bool stopAfter);
    /// <summary>Ducking: factor de volum (1 = normal) que s'aplica amb una rampa, a part de les foses.</summary>
    void SetDuck(double gain, double seconds);
    /// <summary>«Surt del bucle»: el tros en bucle on som (o el proper) deixa de repetir-se.</summary>
    bool Devamp();
    /// <summary>Hi ha un tros en bucle esperant «Surt del bucle».</summary>
    bool HasPendingLoop { get; }
    void Stop();
}

/// <summary>Una sortida oberta (d'àudio o de vídeo) capaç de reproduir cues.</summary>
public interface IMediaOutput : IDisposable
{
    Guid Id { get; }
    string Description { get; }
    string? Warning { get; }
    void Open();
    bool CanPlay(MediaCue cue);
    /// <param name="offset">Segons després de l'inici de la cue on comença (0 = des del principi).</param>
    IMediaPlayback Play(MediaCue cue, double offset = 0);
    void SetMasterVolume(double masterDb);
    /// <summary>Prova: to d'1 s a l'àudio, nom de la sortida a la pantalla.</summary>
    void Test();
}

public sealed record AudioDevice(string Id, string Name)
{
    public override string ToString() => Name;
}

public static class AudioDevices
{
    public static List<AudioDevice> List(AudioDriver driver)
    {
        var list = new List<AudioDevice>();
        try
        {
            if (driver == AudioDriver.Asio)
            {
                foreach (var name in AsioOut.GetDriverNames()) list.Add(new AudioDevice(name, name));
            }
            else
            {
                list.Add(new AudioDevice("", "Predeterminat del sistema"));
                list.AddRange(Endpoints(DataFlow.Render));
            }
        }
        catch { /* sense llista */ }
        return list;
    }

    /// <summary>Entrades (micròfons, línies) per a les cues de micròfon.</summary>
    public static List<AudioDevice> Inputs()
    {
        var list = new List<AudioDevice> { new("", "Entrada predeterminada del sistema") };
        try { list.AddRange(Endpoints(DataFlow.Capture)); } catch { /* sense llista */ }
        return list;
    }

    private static IEnumerable<AudioDevice> Endpoints(DataFlow flow)
    {
        var result = new List<AudioDevice>();
        using var enumerator = new MMDeviceEnumerator();
        foreach (var d in enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active))
        {
            result.Add(new AudioDevice(d.ID, d.FriendlyName));
            d.Dispose();
        }
        return result;
    }
}

/// <summary>
/// Una sortida d'àudio: un mesclador multicanal per a totes les cues (foses precises, latència baixa)
/// → volum general → WASAPI compartit/exclusiu o ASIO. Cada cue hi arriba a través de la seva matriu.
/// </summary>
public sealed class AudioOutput : IMediaOutput
{
    private readonly AudioOutputConfig _config;
    private IWavePlayer? _player;
    private MixingSampleProvider? _mixer;
    private MasterGainProvider? _master;
    private double _masterDb;

    public AudioOutput(AudioOutputConfig config) => _config = config;

    public Guid Id => _config.Id;
    public string Name => _config.Name;
    public string Description { get; private set; } = "";
    public string? Warning { get; private set; }
    public WaveFormat? MixFormat => _mixer?.WaveFormat;

    /// <summary>Latència aproximada de la sortida (per sincronitzar el so dels vídeos amb la imatge).</summary>
    public int LatencyMs => _config.Driver == AudioDriver.Asio ? 20 : _config.LatencyMs;

    /// <summary>Canals oberts (per a la matriu).</summary>
    public int Channels => _mixer?.WaveFormat.Channels ?? 2;

    public bool CanPlay(MediaCue cue) => cue is IAudioSource;

    public static IMediaOutput Create(OutputConfig config) => config switch
    {
        AudioOutputConfig a => new AudioOutput(a),
        VideoOutputConfig v => new Escena.Video.VideoOutput(v),
        _ => throw new NotSupportedException("Tipus de sortida desconegut."),
    };

    /// <summary>Obre el dispositiu. Si falla, prova el predeterminat (WASAPI compartit) i ho avisa a <see cref="Warning"/>.</summary>
    public void Open()
    {
        Close();
        try
        {
            switch (_config.Driver)
            {
                case AudioDriver.Asio: OpenAsio(); break;
                case AudioDriver.WasapiExclusive: OpenWasapi(exclusive: true); break;
                default: OpenWasapi(exclusive: false); break;
            }
        }
        catch (Exception ex)
        {
            Close();
            Warning = $"«{_config.Name}»: no s'ha pogut obrir ({ex.Message}). S'usa la sortida predeterminada.";
            OpenWasapi(exclusive: false, forceDefault: true);
        }
        SetMasterVolume(_masterDb);
    }

    private MMDevice GetDevice(bool forceDefault)
    {
        using var enumerator = new MMDeviceEnumerator();
        if (!forceDefault && !string.IsNullOrEmpty(_config.DeviceId))
        {
            try { return enumerator.GetDevice(_config.DeviceId); }
            catch { Warning = $"«{_config.Name}»: el dispositiu no hi és; s'usa el predeterminat."; }
        }
        return enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
    }

    private void BuildMixer(int sampleRate, int channels)
    {
        _mixer = new MixingSampleProvider(WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, channels)) { ReadFully = true };
        _master = new MasterGainProvider(_mixer);
    }

    private void OpenWasapi(bool exclusive, bool forceDefault = false)
    {
        var device = GetDevice(forceDefault);
        var mix = device.AudioClient.MixFormat;
        int mixRate = mix.SampleRate;
        int latency = _config.LatencyMs;

        if (!exclusive)
        {
            // En compartit, el mesclador té els canals del dispositiu (2, 6, 8…).
            BuildMixer(mixRate, mix.Channels);
            var wasapi = new WasapiOut(device, AudioClientShareMode.Shared, true, latency);
            wasapi.Init(_master);
            wasapi.Play();
            _player = wasapi;
            Description = $"{device.FriendlyName} · WASAPI · {mixRate / 1000.0:0.#} kHz · {mix.Channels} canals";
            return;
        }

        // Exclusiu: cal un format que el dispositiu accepti tal qual.
        var channelOptions = (_config.Channels > 0 ? new[] { _config.Channels } : new[] { mix.Channels, 2 }).Distinct();
        foreach (int ch in channelOptions)
        foreach (int rate in new[] { mixRate, 48000, 44100 }.Distinct())
        foreach (int bits in new[] { 32, 24, 16 })
        {
            var format = bits == 32 ? WaveFormat.CreateIeeeFloatWaveFormat(rate, ch) : new WaveFormat(rate, bits, ch);
            if (!device.AudioClient.IsFormatSupported(AudioClientShareMode.Exclusive, format)) continue;

            BuildMixer(rate, ch);
            IWaveProvider wave = bits switch
            {
                32 => _master!.ToWaveProvider(),
                24 => new SampleToWaveProvider24(_master!),
                _ => _master!.ToWaveProvider16(),
            };
            var wasapi = new WasapiOut(device, AudioClientShareMode.Exclusive, true, latency);
            wasapi.Init(wave);
            wasapi.Play();
            _player = wasapi;
            Description = $"{device.FriendlyName} · WASAPI exclusiu · {rate / 1000.0:0.#} kHz / {bits} bits · {ch} canals";
            return;
        }
        throw new NotSupportedException("el dispositiu no accepta cap format en mode exclusiu");
    }

    private void OpenAsio()
    {
        string driver = !string.IsNullOrEmpty(_config.DeviceId)
            ? _config.DeviceId
            : AsioOut.GetDriverNames().FirstOrDefault() ?? throw new InvalidOperationException("no hi ha cap controlador ASIO instal·lat");
        var asio = new AsioOut(driver) { ChannelOffset = _config.AsioChannelOffset };
        int channels;
        try
        {
            int available = Math.Max(1, asio.DriverOutputChannelCount - _config.AsioChannelOffset);
            channels = Math.Min(_config.Channels > 0 ? _config.Channels : 2, available);
            int rate = asio.IsSampleRateSupported(48000) ? 48000 : 44100;
            BuildMixer(rate, channels);
            asio.Init(_master!.ToWaveProvider());
            asio.Play();
        }
        catch
        {
            asio.Dispose();
            throw;
        }
        _player = asio;
        int first = _config.AsioChannelOffset + 1;
        Description = $"{driver} · ASIO · canals {first}-{first + channels - 1}";
    }

    public void SetMasterVolume(double masterDb)
    {
        _masterDb = masterDb;
        if (_master != null) _master.Gain = (float)AudioMath.DbToGain(masterDb + _config.TrimDb);
    }

    /// <summary>Reproducció per defecte: la matriu principal de la cue, sense nivell d'enviament.</summary>
    public IMediaPlayback Play(MediaCue media, double offset = 0)
    {
        if (media is not IAudioSource source) throw new NotSupportedException("Aquesta sortida només reprodueix àudio.");
        return PlayRouted(source, source.Routing.Crosspoints, 0, offset);
    }

    /// <summary>Reprodueix una cue d'àudio amb una matriu i un nivell d'enviament concrets.</summary>
    public IMediaPlayback PlayRouted(IAudioSource source, IReadOnlyList<CrossPoint> crosspoints, double sendDb, double offset = 0)
    {
        if (_mixer == null) throw new InvalidOperationException("La sortida d'àudio no està oberta.");
        var cue = (MediaCue)source;

        WaveStream? reader = null;
        LiveInput? live = null;
        TrimLoopProvider? trim = null;
        ISampleProvider chain;
        Func<double> position;
        double startAt = 0;

        if (source is AudioCue audio)
        {
            if (string.IsNullOrWhiteSpace(audio.FilePath)) throw new InvalidOperationException("La cue no té cap fitxer.");
            reader = AudioReaders.Open(audio.FilePath);
            var slices = audio.Slices.Where(s => s.IsValid).Select(s => new SliceSpec(s.Start, s.End, s.Repeats));
            trim = new TrimLoopProvider(reader, audio.StartAt, audio.EndAt, audio.Loop, slices, offset);
            chain = trim;
            startAt = audio.StartAt;
            var r = reader;
            position = () => Math.Max(0, r.CurrentTime.TotalSeconds - audio.StartAt);
        }
        else if (source is MicCue mic)
        {
            live = new LiveInput(mic.InputDeviceId);
            mic.SourceChannels = live.WaveFormat.Channels;
            chain = live;
            var clock = System.Diagnostics.Stopwatch.StartNew();
            position = () => clock.Elapsed.TotalSeconds + offset;
        }
        else throw new NotSupportedException("Tipus de cue d'àudio desconegut.");

        try
        {
            if (chain.WaveFormat.SampleRate != _mixer.WaveFormat.SampleRate)
                chain = new WdlResamplingSampleProvider(chain, _mixer.WaveFormat.SampleRate);

            int outChannels = _mixer.WaveFormat.Channels;
            bool fadeIn = cue.FadeIn > 0 && offset <= 0;
            var gain = new GainRampProvider(chain, fadeIn ? 0f : 1f, outChannels)
            {
                MasterGain = (float)AudioMath.DbToGain(source.VolumeDb + sendDb),
            };
            gain.SetRouting(AudioRouting.BuildMatrix(crosspoints, chain.WaveFormat.Channels, outChannels));
            gain.SetPan(source.Pan);
            if (fadeIn) gain.RampTo(1f, cue.FadeIn, cue.FadeInCurve);

            var playback = new AudioPlayback(gain, trim, position, (IDisposable?)reader ?? live!);
            _mixer.AddMixerInput(gain);
            live?.Start();
            return playback;
        }
        catch
        {
            reader?.Dispose();
            live?.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Afegeix una font externa (el so d'un vídeo) al mesclador, amb l'encaminament per defecte.
    /// Retorna el control de volum: el qui crida en porta el volum (<see cref="GainRampProvider.MasterGain"/>) i l'atura.
    /// </summary>
    internal GainRampProvider AddExternal(ISampleProvider source)
    {
        if (_mixer == null) throw new InvalidOperationException("La sortida d'àudio no està oberta.");
        if (source.WaveFormat.SampleRate != _mixer.WaveFormat.SampleRate)
            source = new WdlResamplingSampleProvider(source, _mixer.WaveFormat.SampleRate);
        var gain = new GainRampProvider(source, 1f, _mixer.WaveFormat.Channels) { MasterGain = 0f };
        _mixer.AddMixerInput(gain);
        return gain;
    }

    public void Test() => PlayTestTone();

    /// <summary>To de prova d'1 s a 440 Hz pels dos primers canals.</summary>
    public void PlayTestTone()
    {
        if (_mixer == null) return;
        ISampleProvider tone = new SignalGenerator(_mixer.WaveFormat.SampleRate, 1) { Frequency = 440, Gain = 0.2 }
            .Take(TimeSpan.FromSeconds(1));
        var routed = new GainRampProvider(tone, 1f, _mixer.WaveFormat.Channels);
        _mixer.AddMixerInput(routed);
    }

    /// <summary>To de prova en un sol canal (per identificar altaveus de la matriu).</summary>
    public void PlayChannelTone(int channel)
    {
        if (_mixer == null || channel < 0 || channel >= Channels) return;
        ISampleProvider tone = new SignalGenerator(_mixer.WaveFormat.SampleRate, 1) { Frequency = 440, Gain = 0.2 }
            .Take(TimeSpan.FromSeconds(1));
        var m = new float[1, Channels];
        m[0, channel] = 1f;
        var routed = new GainRampProvider(tone, 1f, Channels);
        routed.SetRouting(m);
        _mixer.AddMixerInput(routed);
    }

    public void Close()
    {
        if (_player != null)
        {
            try { _player.Stop(); } catch { /* ignorat */ }
            _player.Dispose();
        }
        _player = null;
        _mixer = null;
        _master = null;
    }

    public void Dispose() => Close();
}

public sealed class AudioPlayback : IMediaPlayback
{
    private readonly GainRampProvider _gain;
    private readonly TrimLoopProvider? _trim;
    private readonly Func<double> _position;
    private readonly IDisposable _resource;

    internal AudioPlayback(GainRampProvider gain, TrimLoopProvider? trim, Func<double> position, IDisposable resource)
    {
        _gain = gain;
        _trim = trim;
        _position = position;
        _resource = resource;
    }

    public void RampPan(double pan, double seconds, FadeCurve curve, bool stopAfter) =>
        _gain.RampPan(pan, seconds, curve, stopAfter);

    public void SetDuck(double gain, double seconds) => _gain.RampDuck((float)gain, seconds);

    public bool Devamp() => _trim?.Devamp() ?? false;

    public bool HasPendingLoop => _trim?.HasPendingLoop ?? false;

    public double Position
    {
        get
        {
            try { return _position(); }
            catch { return 0; }
        }
    }

    public bool IsFinished => _gain.IsFinished;

    public bool Paused { get => _gain.Paused; set => _gain.Paused = value; }

    public void RampTo(float gain, double seconds, FadeCurve curve, bool stopAfter, bool picture = true) =>
        _gain.RampTo(gain, seconds, curve, stopAfter);

    public void Stop() => _gain.Stop();

    public void Dispose() => _resource.Dispose();
}

/// <summary>Una cue que sona alhora per diverses sortides (sortida principal + enviaments): es controla com una de sola.</summary>
public sealed class MultiPlayback : IMediaPlayback
{
    private readonly IReadOnlyList<IMediaPlayback> _parts;

    public MultiPlayback(IReadOnlyList<IMediaPlayback> parts) => _parts = parts;

    public double Position => _parts[0].Position;
    public bool IsFinished => _parts.All(p => p.IsFinished);

    public bool Paused
    {
        get => _parts[0].Paused;
        set { foreach (var p in _parts) p.Paused = value; }
    }

    public void RampTo(float gain, double seconds, FadeCurve curve, bool stopAfter, bool picture = true)
    {
        foreach (var p in _parts) p.RampTo(gain, seconds, curve, stopAfter, picture);
    }

    public void RampPan(double pan, double seconds, FadeCurve curve, bool stopAfter)
    {
        foreach (var p in _parts) p.RampPan(pan, seconds, curve, stopAfter);
    }

    public void SetDuck(double gain, double seconds)
    {
        foreach (var p in _parts) p.SetDuck(gain, seconds);
    }

    public bool Devamp() => _parts.Select(p => p.Devamp()).ToList().Any(x => x);

    public bool HasPendingLoop => _parts.Any(p => p.HasPendingLoop);

    public void Stop()
    {
        foreach (var p in _parts) p.Stop();
    }

    public void Dispose()
    {
        foreach (var p in _parts) p.Dispose();
    }
}

/// <summary>
/// Entrada en directe (WASAPI) com a font de mostres float. Manté poca latència: si s'acumula
/// massa àudio (rellotges diferents entre entrada i sortida), descarta el més antic.
/// </summary>
public sealed class LiveInput : ISampleProvider, IDisposable
{
    private const double MaxLatency = 0.08;

    private readonly WasapiCapture _capture;
    private readonly WaveFormat _sourceFormat;
    private readonly float[] _ring;
    private readonly object _lock = new();
    private int _read, _count;

    public LiveInput(string deviceId)
    {
        using var enumerator = new MMDeviceEnumerator();
        var device = string.IsNullOrEmpty(deviceId)
            ? enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications)
            : enumerator.GetDevice(deviceId);
        _capture = new WasapiCapture(device, true, 10);
        _sourceFormat = _capture.WaveFormat;
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(_sourceFormat.SampleRate, _sourceFormat.Channels);
        _ring = new float[_sourceFormat.SampleRate * _sourceFormat.Channels]; // 1 s
        _capture.DataAvailable += OnData;
    }

    public WaveFormat WaveFormat { get; }

    public void Start() => _capture.StartRecording();

    private void OnData(object? sender, WaveInEventArgs e)
    {
        int bytesPerSample = _sourceFormat.BitsPerSample / 8;
        if (bytesPerSample == 0) return;
        int samples = e.BytesRecorded / bytesPerSample;
        bool isFloat = _sourceFormat.Encoding == WaveFormatEncoding.IeeeFloat
                       || (_sourceFormat is WaveFormatExtensible ext && ext.SubFormat == NAudio.Dmo.AudioMediaSubtypes.MEDIASUBTYPE_IEEE_FLOAT);
        int maxCount = (int)(MaxLatency * _sourceFormat.SampleRate) * _sourceFormat.Channels;

        lock (_lock)
        {
            for (int i = 0; i < samples; i++)
            {
                int o = i * bytesPerSample;
                float v = bytesPerSample switch
                {
                    4 when isFloat => BitConverter.ToSingle(e.Buffer, o),
                    4 => BitConverter.ToInt32(e.Buffer, o) / 2147483648f,
                    3 => ((e.Buffer[o] << 8 | e.Buffer[o + 1] << 16 | e.Buffer[o + 2] << 24) >> 8) / 8388608f,
                    2 => BitConverter.ToInt16(e.Buffer, o) / 32768f,
                    _ => 0f,
                };
                int write = (_read + _count) % _ring.Length;
                _ring[write] = v;
                if (_count < _ring.Length) _count++;
                else _read = (_read + 1) % _ring.Length;
            }
            // Massa endarrerit: salta endavant (sencer en trames).
            int excess = _count - maxCount;
            if (excess > 0)
            {
                excess -= excess % _sourceFormat.Channels;
                _read = (_read + excess) % _ring.Length;
                _count -= excess;
            }
        }
    }

    public int Read(float[] buffer, int offset, int count)
    {
        lock (_lock)
        {
            int n = Math.Min(count, _count);
            n -= n % WaveFormat.Channels;
            for (int i = 0; i < n; i++)
            {
                buffer[offset + i] = _ring[_read];
                _read = (_read + 1) % _ring.Length;
            }
            _count -= n;
            // Sense dades, silenci: l'entrada no s'acaba mai sola.
            Array.Clear(buffer, offset + n, count - n);
        }
        return count;
    }

    public void Dispose()
    {
        _capture.DataAvailable -= OnData;
        try { _capture.StopRecording(); } catch { /* ignorat */ }
        _capture.Dispose();
    }
}

/// <summary>Obre i manté totes les sortides de l'espectacle.</summary>
public sealed class OutputManager : IDisposable
{
    private readonly List<IMediaOutput> _outputs = new();
    private double _masterDb;

    public IReadOnlyList<IMediaOutput> Outputs => _outputs;

    /// <summary>Tanca les actuals i obre les configurades. Retorna els avisos.</summary>
    public List<string> Open(IEnumerable<OutputConfig> configs)
    {
        CloseAll();
        var warnings = new List<string>();
        foreach (var cfg in configs) OpenOne(cfg, warnings);
        return warnings;
    }

    /// <summary>Obre una sortida nova sense tocar les que ja hi ha.</summary>
    public void OpenOne(OutputConfig cfg, List<string> warnings)
    {
        var output = AudioOutput.Create(cfg);
        // Les pantalles han de trobar les sortides d'àudio per on surt el so dels vídeos.
        if (output is Escena.Video.VideoOutput video) video.AudioResolver = id => Find(id) as AudioOutput;
        try
        {
            output.Open();
            output.SetMasterVolume(_masterDb);
            if (output.Warning != null) warnings.Add(output.Warning);
            _outputs.Add(output);
        }
        catch (Exception ex)
        {
            output.Dispose();
            warnings.Add($"«{cfg.Name}»: {ex.Message}");
        }
    }

    public IMediaOutput? Resolve(MediaCue cue) =>
        _outputs.FirstOrDefault(o => o.Id == cue.OutputId && o.CanPlay(cue))
        ?? _outputs.FirstOrDefault(o => o.CanPlay(cue));

    public IMediaOutput? Find(Guid? id) => id == null ? null : _outputs.FirstOrDefault(o => o.Id == id);

    public void SetMasterVolume(double db)
    {
        _masterDb = db;
        foreach (var o in _outputs) o.SetMasterVolume(db);
    }

    public void CloseAll()
    {
        foreach (var o in _outputs) o.Dispose();
        _outputs.Clear();
    }

    public void Dispose() => CloseAll();
}
