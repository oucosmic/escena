using Escena.Model;

namespace Escena;

/// <summary>Guió tècnic (CSV): importar-lo a l'espectacle obert i exportar-ne un.</summary>
public sealed partial class ShowController
{
    /// <summary>
    /// Aplica el guió tècnic a l'espectacle: crea les cues que falten i actualitza les que ja hi són.
    /// Tot plegat és un sol pas de desfer.
    /// </summary>
    public CueSheetReport ImportCueSheet(string csvPath)
    {
        var readWarnings = new List<string>();
        var rows = CueSheet.Read(csvPath, readWarnings);
        bool wasEmpty = Cues.Count == 0;
        CueSheetReport report;
        _moving = true;
        try
        {
            report = CueSheet.Apply(Cues, rows, Path.GetDirectoryName(Path.GetFullPath(csvPath))!, m => EnsureOutputFor(m)?.Id);
        }
        finally { _moving = false; }
        report.Warnings.InsertRange(0, readWarnings);
        AfterTreeChange();
        UpdateDuplicateWarning();
        if (wasEmpty && Cues.Count > 0) StandbyCue = Cues[0];
        Status = string.Format(L.T("Guió tècnic importat: {0} cues noves, {1} actualitzades."), report.Created, report.Updated);
        return report;
    }

    /// <summary>Escriu el guió tècnic de l'espectacle (les rutes dels fitxers, relatives a la carpeta del CSV).</summary>
    public void ExportCueSheet(string csvPath) => CueSheet.Write(csvPath, Cues.ToList());
}
