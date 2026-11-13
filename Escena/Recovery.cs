using System.Diagnostics;
using System.Text.Json;
using Escena.Model;

namespace Escena;

/// <summary>Estat de la sessió que es desa contínuament per poder continuar després d'una fallada.</summary>
public sealed class SessionState
{
    public int ProcessId { get; set; }
    public DateTime Updated { get; set; }
    /// <summary>Fitxer de l'espectacle obert (null si encara no s'ha desat mai).</summary>
    public string? ShowPath { get; set; }
    /// <summary>Hi ha una còpia amb canvis sense desar a la carpeta de recuperació.</summary>
    public bool HasUnsavedCopy { get; set; }
    public Guid? StandbyCueId { get; set; }
    public string? StandbyLabel { get; set; }
    public Guid? LastFiredCueId { get; set; }
    public string? LastFiredLabel { get; set; }

    /// <summary>Fitxers d'aquesta sessió a la carpeta de recuperació (els omple <see cref="Recovery.FindCrashedSession"/>).</summary>
    [System.Text.Json.Serialization.JsonIgnore] public string? SessionPath { get; set; }
    [System.Text.Json.Serialization.JsonIgnore] public string? CopyPath { get; set; }
}

/// <summary>
/// Còpia automàtica i recuperació. Mentre Escena és obert hi ha un fitxer de sessió; si en arrencar
/// encara hi és i el procés que el va escriure ja no existeix, Escena no es va tancar bé.
/// Els canvis sense desar es copien a part (mai no se sobreescriu el fitxer de l'espectacle sense demanar-ho).
/// </summary>
public static class Recovery
{
    private static readonly string Folder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Escena", "recuperacio");

    // Cada finestra d'Escena (procés) té els seus fitxers: així dues finestres obertes alhora no es
    // trepitgen la recuperació, i tancar-ne una no esborra la de l'altra.
    private static string SessionFile => Path.Combine(Folder, $"sessio-{Environment.ProcessId}.json");

    /// <summary>Còpia dels canvis sense desar d'aquesta finestra.</summary>
    public static string CopyFile => CopyFor(SessionFile);

    private static string CopyFor(string sessionFile)
    {
        string name = Path.GetFileNameWithoutExtension(sessionFile); // «sessio» (versions antigues) o «sessio-1234»
        return Path.Combine(Folder, "copia" + name["sessio".Length..] + ".show");
    }

    /// <summary>
    /// La sessió més recent que va acabar malament (null si totes es van tancar bé o encara són obertes).
    /// Les sessions mortes que no es poden recuperar s'esborren.
    /// </summary>
    public static SessionState? FindCrashedSession()
    {
        SessionState? best = null;
        try
        {
            if (!Directory.Exists(Folder)) return null;
            foreach (var file in Directory.GetFiles(Folder, "sessio*.json"))
            {
                SessionState? state;
                try { state = JsonSerializer.Deserialize<SessionState>(File.ReadAllText(file)); }
                catch { state = null; }
                if (state != null && (state.ProcessId == Environment.ProcessId || IsEscenaRunning(state.ProcessId)))
                    continue; // aquesta finestra o una altra que encara és oberta
                string copy = CopyFor(file);
                bool hasShow = state != null && ((state.ShowPath != null && File.Exists(state.ShowPath))
                                                 || (state.HasUnsavedCopy && File.Exists(copy)));
                if (!hasShow || state == null)
                {
                    TryDelete(file);
                    TryDelete(copy);
                    continue;
                }
                state.SessionPath = file;
                state.CopyPath = copy;
                if (best == null || state.Updated > best.Updated) best = state;
            }
        }
        catch { /* sense recuperació */ }
        return best;
    }

    /// <summary>Ja s'ha recuperat (o s'ha dit que no): els fitxers d'aquella sessió ja no calen.</summary>
    public static void Discard(SessionState state)
    {
        if (state.SessionPath != null) TryDelete(state.SessionPath);
        if (state.CopyPath != null) TryDelete(state.CopyPath);
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { /* ignorat */ }
    }

    private static bool IsEscenaRunning(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return !p.HasExited && p.ProcessName.Equals("Escena", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    public static void WriteSession(SessionState state)
    {
        try
        {
            Directory.CreateDirectory(Folder);
            state.ProcessId = Environment.ProcessId;
            state.Updated = DateTime.Now;
            string tmp = SessionFile + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(state));
            File.Move(tmp, SessionFile, overwrite: true);
        }
        catch { /* la recuperació mai no ha d'aturar l'espectacle */ }
    }

    public static bool SaveCopy(ShowDocument doc)
    {
        try
        {
            Directory.CreateDirectory(Folder);
            ShowFile.Save(CopyFile, doc);
            return true;
        }
        catch { return false; }
    }

    public static void DeleteCopy()
    {
        try { File.Delete(CopyFile); } catch { /* ignorat */ }
    }

    /// <summary>Tancament correcte: ja no cal recuperar res.</summary>
    public static void Clear()
    {
        try
        {
            File.Delete(SessionFile);
            File.Delete(CopyFile);
        }
        catch { /* ignorat */ }
    }
}
