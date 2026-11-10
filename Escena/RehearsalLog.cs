using System.Collections.ObjectModel;
using System.Globalization;
using Escena.Model;

namespace Escena;

/// <summary>Una cue disparada durant l'assaig.</summary>
public sealed class LogEntry
{
    public required DateTime Time { get; init; }
    public required Guid CueId { get; init; }
    public required string Number { get; init; }
    public required string Name { get; init; }
    /// <summary>GO, auto, grup, botonera, tecla, des de…</summary>
    public required string Source { get; init; }
    /// <summary>Segons des de l'entrada anterior (buit a la primera).</summary>
    public double? SincePrevious { get; init; }

    public string TimeText => Time.ToString("HH:mm:ss.f", CultureInfo.InvariantCulture);
    public string SincePreviousText => SincePrevious is { } s ? $"+{s:0.0} s" : "";
}

/// <summary>
/// Registre de quan es dispara cada cue, per ajustar les esperes amb dades reals.
/// També es desa a %APPDATA%\Escena\registre\AAAA-MM-DD.csv.
/// </summary>
public sealed class RehearsalLog
{
    public ObservableCollection<LogEntry> Entries { get; } = new();

    /// <summary>Desa cada entrada al fitxer del dia (les proves automàtiques ho desactiven).</summary>
    public static bool SaveToDisk { get; set; } = true;

    public static string Folder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Escena", "registre");

    public void Add(Cue cue, string source)
    {
        var now = DateTime.Now;
        var previous = Entries.Count > 0 ? Entries[^1] : null;
        var entry = new LogEntry
        {
            Time = now, CueId = cue.Id, Number = cue.Number, Name = cue.Name, Source = source,
            SincePrevious = previous != null ? (now - previous.Time).TotalSeconds : null,
        };
        Entries.Add(entry);
        if (Entries.Count > 5000) Entries.RemoveAt(0);
        if (!SaveToDisk) return;
        try
        {
            Directory.CreateDirectory(Folder);
            string file = Path.Combine(Folder, now.ToString("yyyy-MM-dd") + ".csv");
            bool header = !File.Exists(file);
            using var w = new StreamWriter(file, append: true, System.Text.Encoding.UTF8);
            if (header) w.WriteLine("hora;numero;nom;origen;segons_des_de_l_anterior");
            w.WriteLine(ToCsv(entry));
        }
        catch { /* el registre no ha de molestar mai l'espectacle */ }
    }

    public void Clear() => Entries.Clear();

    private static string ToCsv(LogEntry e) =>
        string.Join(";", e.TimeText, Quote(e.Number), Quote(e.Name), e.Source,
            e.SincePrevious?.ToString("0.00", CultureInfo.InvariantCulture) ?? "");

    private static string Quote(string s) => s.Contains(';') || s.Contains('"') ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;

    public void Export(string path)
    {
        using var w = new StreamWriter(path, append: false, System.Text.Encoding.UTF8);
        w.WriteLine("hora;numero;nom;origen;segons_des_de_l_anterior");
        foreach (var e in Entries) w.WriteLine(ToCsv(e));
    }
}
