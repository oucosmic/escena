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

    private static string SessionFile => Path.Combine(Folder, "sessio.json");

    public static string CopyFile => Path.Combine(Folder, "copia.show");

    /// <summary>La sessió anterior si va acabar malament (null si es va tancar bé o encara és oberta).</summary>
    public static SessionState? FindCrashedSession()
    {
        try
        {
            if (!File.Exists(SessionFile)) return null;
            var state = JsonSerializer.Deserialize<SessionState>(File.ReadAllText(SessionFile));
            if (state == null || state.ProcessId == Environment.ProcessId) return null;
            if (IsEscenaRunning(state.ProcessId)) return null; // una altra finestra d'Escena encara oberta
            bool hasShow = (state.ShowPath != null && File.Exists(state.ShowPath))
                           || (state.HasUnsavedCopy && File.Exists(CopyFile));
            return hasShow ? state : null;
        }
        catch { return null; }
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
