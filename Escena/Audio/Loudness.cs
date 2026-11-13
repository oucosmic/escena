using NAudio.Wave;
using Escena.Model;

namespace Escena.Audio;

/// <summary>Mesura del volum d'un fitxer: sonoritat mitjana (sense els silencis) i pic.</summary>
public readonly record struct LoudnessInfo(double LevelDb, double PeakDb);

/// <summary>Resultat d'igualar una cue: el volum nou i si s'ha hagut de limitar per no saturar.</summary>
public readonly record struct NormalizeResult(PlayableCue Cue, double NewVolumeDb, bool Limited, string? Error);

/// <summary>
/// Iguala el volum de diverses cues d'àudio i vídeo. Mesura la sonoritat de cada fitxer (entre l'inici i el final
/// de la cue) com a mitjana d'energia en blocs de 400 ms, sense comptar els trossos gairebé en silenci
/// (una aproximació senzilla a com ho fan els mesuradors LUFS), i posa a cada cue el volum que la deixa al nivell triat.
/// Si per arribar-hi el pic passaria de −1 dBFS, es queda més baixa (no satura mai).
/// </summary>
public static class Loudness
{
    private const double BlockSeconds = 0.4;
    private const double SilenceDb = -60;
    public const double PeakCeilingDb = -1;

    public static LoudnessInfo Measure(string path, double startAt = 0, double endAt = 0)
    {
        using var stream = AudioReaders.Open(path);
        var samples = (ISampleProvider)stream;
        int channels = samples.WaveFormat.Channels, rate = samples.WaveFormat.SampleRate;
        if (startAt > 0) stream.CurrentTime = TimeSpan.FromSeconds(Math.Min(startAt, stream.TotalTime.TotalSeconds));
        long maxFrames = endAt > startAt ? (long)((endAt - startAt) * rate) : long.MaxValue;

        int blockFrames = Math.Max(1, (int)(BlockSeconds * rate));
        var buffer = new float[blockFrames * channels];
        double gatedEnergy = 0, peak = 0;
        long gatedBlocks = 0, frames = 0;
        double allEnergy = 0;
        long allBlocks = 0;
        int n;
        while (frames < maxFrames && (n = samples.Read(buffer, 0, buffer.Length)) > 0)
        {
            long left = maxFrames - frames;
            int count = left >= n / channels ? n : (int)left * channels;
            double sum = 0;
            for (int i = 0; i < count; i++)
            {
                double v = buffer[i];
                sum += v * v;
                if (Math.Abs(v) > peak) peak = Math.Abs(v);
            }
            frames += count / channels;
            double energy = sum / Math.Max(1, count);
            allEnergy += energy;
            allBlocks++;
            if (LevelDb(energy) > SilenceDb)
            {
                gatedEnergy += energy;
                gatedBlocks++;
            }
        }
        double mean = gatedBlocks > 0 ? gatedEnergy / gatedBlocks : allBlocks > 0 ? allEnergy / allBlocks : 0;
        return new LoudnessInfo(LevelDb(mean), peak > 0 ? 20 * Math.Log10(peak) : -120);
    }

    /// <summary>Nivell en dB d'una energia mitjana (mitjana dels quadrats de les mostres).</summary>
    private static double LevelDb(double meanSquare) => meanSquare > 0 ? 10 * Math.Log10(meanSquare) : -120;

    /// <summary>Volum que cal posar a la cue perquè soni a <paramref name="targetDb"/> sense passar del sostre de pic.</summary>
    public static (double VolumeDb, bool Limited) VolumeFor(LoudnessInfo info, double targetDb)
    {
        double wanted = targetDb - info.LevelDb;
        double maxByPeak = PeakCeilingDb - info.PeakDb;
        bool limited = wanted > maxByPeak;
        double volume = Math.Min(wanted, maxByPeak);
        return (Math.Round(Math.Clamp(volume, AudioMath.SilenceDb, 12), 1), limited || wanted > 12);
    }

    /// <summary>Mesura totes les cues (fora del fil de la interfície). No toca les cues: el resultat diu què cal posar.</summary>
    public static List<NormalizeResult> Plan(IEnumerable<PlayableCue> cues, double targetDb)
    {
        var results = new List<NormalizeResult>();
        foreach (var c in cues)
        {
            try
            {
                var info = Measure(c.FilePath, c.StartAt, c.EndAt);
                if (info.PeakDb <= -119) throw new InvalidDataException(Escena.L.T("no té so"));
                var (vol, limited) = VolumeFor(info, targetDb);
                results.Add(new NormalizeResult(c, vol, limited, null));
            }
            catch (Exception ex)
            {
                results.Add(new NormalizeResult(c, c.VolumeDb, false, ex.Message));
            }
        }
        return results;
    }
}
