using System.Runtime.InteropServices;
using System.Text;
using Escena.Model;

namespace Escena;

/// <summary>
/// Guió tècnic des de la línia d'ordres, sense obrir la finestra:
/// <c>Escena.exe --importa-guio guio.csv espectacle.show</c> (si l'espectacle no existeix, el crea; si existeix,
/// en deixa una còpia «.show.bak») i <c>Escena.exe --exporta-guio espectacle.show guio.csv</c>.
/// </summary>
public static class CueSheetCli
{
    public const string ImportArg = "--importa-guio";
    public const string ExportArg = "--exporta-guio";

    [DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int processId);

    public const string ConvertArg = "--converteix-guio";

    public static bool Handles(string[] args) => args.Length > 0 && (args[0] == ImportArg || args[0] == ExportArg || args[0] == ConvertArg);

    public static int Run(string[] args)
    {
        AttachConsole(-1); // escriu a la consola des d'on s'ha cridat (si n'hi ha)
        try { Console.OutputEncoding = Encoding.UTF8; } catch (IOException) { }
        if (args.Length < 3)
        {
            Console.WriteLine($"Ús: Escena.exe {ImportArg} guio.csv espectacle.show");
            Console.WriteLine($"     Escena.exe {ExportArg} espectacle.show guio.csv");
            Console.WriteLine($"     Escena.exe {ConvertArg} guio.md guio.csv");
            Console.WriteLine("(el guió tècnic pot ser .csv o .md)");
            return 2;
        }
        try
        {
            return args[0] switch
            {
                ImportArg => Import(args[1], args[2]),
                ExportArg => Export(args[1], args[2]),
                _ => Convert(args[1], args[2]),
            };
        }
        catch (Exception ex)
        {
            Console.WriteLine("Error: " + ex.Message);
            return 1;
        }
    }

    private static int Import(string csv, string show)
    {
        var warnings = new List<string>();
        var rows = CueSheet.Read(csv, warnings);
        ShowDocument doc;
        if (File.Exists(show))
        {
            doc = ShowFile.Load(show);
            File.Copy(show, show + ".bak", overwrite: true);
        }
        else doc = new ShowDocument { Outputs = { AudioOutputConfig.CreateDefault() } };

        Guid? OutputFor(MediaCue m)
        {
            if (!m.IsVisual) return doc.Outputs.OfType<AudioOutputConfig>().FirstOrDefault()?.Id;
            var video = doc.Outputs.OfType<VideoOutputConfig>().FirstOrDefault();
            if (video == null)
            {
                video = VideoOutputConfig.CreateDefault();
                doc.Outputs.Add(video);
            }
            return video.Id;
        }

        var report = CueSheet.Apply(doc.Cues, rows, Path.GetDirectoryName(Path.GetFullPath(csv))!, OutputFor);
        report.Warnings.InsertRange(0, warnings);
        doc.Cues = ShowTree.Normalize(doc.Cues);
        ShowFile.Save(show, doc);
        Console.WriteLine(report.Summary());
        Console.WriteLine("Desat: " + Path.GetFullPath(show));
        return 0;
    }

    /// <summary>Passa el guió tècnic d'un format a l'altre (.md ↔ .csv) sense tocar cap espectacle.</summary>
    private static int Convert(string source, string dest)
    {
        var warnings = new List<string>();
        var rows = CueSheet.Read(source, warnings);
        CueSheet.WriteRows(dest, rows, Path.GetFileNameWithoutExtension(dest));
        foreach (var w in warnings) Console.WriteLine("• " + w);
        Console.WriteLine($"{rows.Count} cues · escrit a {Path.GetFullPath(dest)}");
        return 0;
    }

    private static int Export(string show, string csv)
    {
        var doc = ShowFile.Load(show);
        CueSheet.Write(csv, doc.Cues);
        Console.WriteLine($"{doc.Cues.Count} cues · guió tècnic escrit a {Path.GetFullPath(csv)}");
        return 0;
    }
}
