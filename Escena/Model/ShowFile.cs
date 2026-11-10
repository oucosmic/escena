using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Escena.Model;

/// <summary>Ajustos que viatgen amb l'espectacle.</summary>
public sealed class ShowSettings : ObservableObject
{
    private int _goLockoutMs = 250;
    private double _panicFadeSeconds = 0.5;
    private int _autosaveSeconds = 30;

    /// <summary>Després d'un GO, s'ignoren els GO durant aquests mil·lisegons (contra els dobles tocs).</summary>
    public int GoLockoutMs { get => _goLockoutMs; set => Set(ref _goLockoutMs, Math.Clamp(value, 0, 5000)); }

    /// <summary>Durada de la fosa del primer toc de PÀNIC (el segon toc talla de cop).</summary>
    public double PanicFadeSeconds { get => _panicFadeSeconds; set => Set(ref _panicFadeSeconds, Math.Clamp(value, 0, 10)); }

    /// <summary>Cada quants segons es fa la còpia de seguretat dels canvis sense desar.</summary>
    public int AutosaveSeconds { get => _autosaveSeconds; set => Set(ref _autosaveSeconds, Math.Clamp(value, 5, 600)); }

    public void CopyFrom(ShowSettings o)
    {
        GoLockoutMs = o.GoLockoutMs;
        PanicFadeSeconds = o.PanicFadeSeconds;
        AutosaveSeconds = o.AutosaveSeconds;
    }
}

public sealed class ShowDocument
{
    public int Version { get; set; } = 5;
    public double MasterVolumeDb { get; set; }
    public ShowSettings Settings { get; set; } = new();
    public List<OutputConfig> Outputs { get; set; } = new();
    public List<Cue> Cues { get; set; } = new();
}

/// <summary>
/// Desa i obre espectacles (.show, JSON). Les rutes dels fitxers es guarden relatives a la carpeta
/// de l'espectacle, perquè es pugui moure a un altre ordinador. També obre els .escena de la v0.1.
/// </summary>
public static class ShowFile
{
    public const string Extension = ".show";
    public const string LegacyExtension = ".escena";

    internal static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    public static bool IsShowFile(string path)
    {
        string ext = Path.GetExtension(path);
        return ext.Equals(Extension, StringComparison.OrdinalIgnoreCase)
            || ext.Equals(LegacyExtension, StringComparison.OrdinalIgnoreCase);
    }

    public static void Save(string path, ShowDocument doc)
    {
        string baseDir = Path.GetDirectoryName(Path.GetFullPath(path))!;
        var node = JsonSerializer.SerializeToNode(doc, Options)!;

        foreach (var cue in node["cues"]!.AsArray())
        {
            if (TryGetFilePath(cue, out var p) && Path.IsPathRooted(p))
                cue!["filePath"] = Path.GetRelativePath(baseDir, p);
        }

        string tmp = path + ".tmp";
        File.WriteAllText(tmp, node.ToJsonString(Options));
        File.Move(tmp, path, overwrite: true);
    }

    public static ShowDocument Load(string path)
    {
        string baseDir = Path.GetDirectoryName(Path.GetFullPath(path))!;
        var node = JsonNode.Parse(File.ReadAllText(path)) ?? throw new InvalidDataException("El fitxer és buit.");

        if (node["cues"] is JsonArray cues)
        {
            foreach (var cue in cues)
            {
                if (cue is null) continue;
                if (TryGetFilePath(cue, out var p) && p.Length > 0 && !Path.IsPathRooted(p))
                    cue["filePath"] = Path.GetFullPath(Path.Combine(baseDir, p));

                // v0.1: «stopAll» → cue de control «Atura totes».
                if (cue["type"]?.GetValue<string>() == "stopAll")
                {
                    cue["type"] = "control";
                    cue["action"] = "Stop";
                    cue["allCues"] = true;
                    cue["useOwnFade"] = false;
                }
            }
        }

        var doc = node.Deserialize<ShowDocument>(Options) ?? throw new InvalidDataException("Format no vàlid.");
        if (doc.Outputs.Count == 0) doc.Outputs.Add(AudioOutputConfig.CreateDefault());
        // Grups: els de la 0.5 apuntaven a cues d'altres llocs; ara contenen les seves fills just a sota.
        ShowTree.MigrateLegacyGroups(doc.Cues);
        doc.Cues = ShowTree.Normalize(doc.Cues);
        // Espectacles anteriors a la numeració lliure: 1, 2, 3… segons la posició.
        if (doc.Cues.All(c => string.IsNullOrEmpty(c.Number)))
            for (int i = 0; i < doc.Cues.Count; i++) doc.Cues[i].Number = (i + 1).ToString();
        return doc;
    }

    private static bool TryGetFilePath(JsonNode? cue, out string path)
    {
        path = "";
        return cue?["filePath"] is JsonValue v && v.TryGetValue(out path!);
    }
}
