using System.Globalization;
using System.Text;
using Escena.Model;

namespace Escena;

/// <summary>Una fila del guió tècnic (tal com ve del CSV, sense interpretar).</summary>
public sealed class CueSheetRow
{
    public int Line { get; init; }
    public string Scene { get; init; } = "";
    public string Number { get; init; } = "";
    public string Type { get; init; } = "";
    public string Name { get; init; } = "";
    public string Entry { get; init; } = "";
    public string File { get; init; } = "";
    public string Target { get; init; } = "";
    public string Volume { get; init; } = "";
    public string FadeIn { get; init; } = "";
    public string FadeOut { get; init; } = "";
    public string Time { get; init; } = "";
    public string Loop { get; init; } = "";
    public string After { get; init; } = "";
    public string Notes { get; init; } = "";
}

/// <summary>Resultat d'aplicar un guió tècnic a un espectacle.</summary>
public sealed class CueSheetReport
{
    public int Created { get; set; }
    public int Updated { get; set; }
    /// <summary>Cues de l'espectacle (sense comptar grups) que el guió tècnic no esmenta.</summary>
    public int NotInSheet { get; set; }
    public List<string> Warnings { get; } = new();

    public string Summary()
    {
        var sb = new StringBuilder();
        sb.Append(L.T("Cues noves:")).Append(' ').Append(Created).Append(" · ")
          .Append(L.T("actualitzades:")).Append(' ').Append(Updated);
        if (NotInSheet > 0) sb.Append('\n').Append(NotInSheet).Append(' ').Append(L.T("cues de l'espectacle no surten al guió tècnic (no s'han tocat)."));
        if (Warnings.Count > 0)
        {
            sb.Append("\n\n").Append(L.T("Avisos:"));
            foreach (var w in Warnings.Take(25)) sb.Append("\n• ").Append(w);
            if (Warnings.Count > 25) sb.Append("\n• … ").Append(Warnings.Count - 25).Append(' ').Append(L.T("més"));
        }
        return sb.ToString();
    }
}

/// <summary>
/// Guió tècnic en CSV: una fila per cue (escena, número, tipus, nom, entrada, fitxer, destí, volum, foses, temps,
/// bucle, què passa després i notes). Es pot importar a un espectacle (crea les cues que falten i actualitza les que ja hi són,
/// buscant-les pel número o pel nom) i exportar-ne un des d'un espectacle. El format s'explica a «Guió tècnic.md».
/// </summary>
public static class CueSheet
{
    public static readonly string[] Header =
        { "Escena", "Núm", "Tipus", "Nom", "Entrada", "Fitxer", "Destí", "Volum", "Fosa entrada", "Fosa sortida", "Temps", "Bucle", "Després", "Notes" };

    public const string EntryPrefix = "Entrada: ";

    // ---------------- Lectura ----------------

    /// <summary>Una taula del fitxer: capçalera, files (amb el número de línia) i l'escena on és (el títol de sobre, al .md).</summary>
    private sealed record Table(List<string> Header, List<(int Line, List<string> Fields)> Rows, string Scene);

    public static bool IsMarkdown(string path) =>
        Path.GetExtension(path).Equals(".md", StringComparison.OrdinalIgnoreCase) || Path.GetExtension(path).Equals(".markdown", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Llegeix el guió tècnic: un CSV (una taula) o un .md d'Obsidian, on cada taula amb les columnes «Tipus» i «Nom»
    /// és un tros del guió tècnic i el títol que té a sobre («## I. El sopar») és l'escena de les seves files.
    /// </summary>
    public static List<CueSheetRow> Read(string path, List<string> warnings)
    {
        string text = ReadText(path);
        var rows = new List<CueSheetRow>();
        if (IsMarkdown(path))
        {
            var tables = ParseMarkdownTables(text).Where(t => HasCueColumns(t.Header)).ToList();
            if (tables.Count == 0)
                throw new InvalidDataException(L.T("El fitxer no té cap taula amb les columnes «Tipus» i «Nom»."));
            foreach (var t in tables) ReadTable(t, rows, warnings);
            return rows;
        }
        var lines = SplitRecords(text);
        if (lines.Count == 0) throw new InvalidDataException(L.T("El guió tècnic és buit."));
        char sep = DetectSeparator(lines[0]);
        var table = new Table(ParseRecord(lines[0], sep),
            lines.Skip(1).Select((l, i) => (i + 2, ParseRecord(l, sep))).ToList(), "");
        if (!HasCueColumns(table.Header))
            throw new InvalidDataException(L.T("La primera fila ha de ser la capçalera, amb almenys les columnes «Tipus» i «Nom»."));
        ReadTable(table, rows, warnings);
        return rows;
    }

    private static bool HasCueColumns(List<string> header)
    {
        var keys = header.Select(Key).ToList();
        return keys.Intersect(new[] { "tipus", "tipo", "type" }).Any() && keys.Intersect(new[] { "nom", "nombre", "name" }).Any();
    }

    /// <summary>Taules d'un .md: línies que comencen per «|», amb la línia de guions després de la capçalera.</summary>
    private static List<Table> ParseMarkdownTables(string text)
    {
        var tables = new List<Table>();
        string scene = "";
        Table? current = null;
        var lines = text.Replace("\r\n", "\n").Split('\n');
        bool fenced = false;
        for (int i = 0; i < lines.Length; i++)
        {
            string t = lines[i].Trim();
            if (t.StartsWith("```") || t.StartsWith("~~~")) { fenced = !fenced; current = null; continue; }
            if (fenced) continue; // els blocs de codi (exemples) no compten
            if (t.StartsWith('|'))
            {
                var cells = SplitPipeRow(t);
                if (current == null)
                {
                    current = new Table(cells, new(), scene);
                    tables.Add(current);
                }
                else if (!cells.All(c => System.Text.RegularExpressions.Regex.IsMatch(c, @"^:?-{2,}:?$")))
                    current.Rows.Add((i + 1, cells));
                continue;
            }
            current = null;
            var heading = System.Text.RegularExpressions.Regex.Match(t, @"^(#{2,6})\s+(.+?)\s*#*$");
            if (heading.Success) scene = CleanCell(heading.Groups[2].Value);
        }
        return tables;
    }

    private static List<string> SplitPipeRow(string line)
    {
        var cells = new List<string>();
        var sb = new StringBuilder();
        string inner = line.Trim();
        if (inner.StartsWith('|')) inner = inner[1..];
        if (inner.EndsWith('|') && !inner.EndsWith("\\|")) inner = inner[..^1];
        for (int i = 0; i < inner.Length; i++)
        {
            if (inner[i] == '\\' && i + 1 < inner.Length && inner[i + 1] == '|') { sb.Append('|'); i++; }
            else if (inner[i] == '|') { cells.Add(CleanCell(sb.ToString())); sb.Clear(); }
            else sb.Append(inner[i]);
        }
        cells.Add(CleanCell(sb.ToString()));
        return cells;
    }

    /// <summary>Treu el format de Markdown d'una cel·la (negreta, cursiva, codi) i converteix «&lt;br&gt;» en salts de línia.</summary>
    private static string CleanCell(string s)
    {
        s = System.Text.RegularExpressions.Regex.Replace(s.Trim(), @"<br\s*/?>", "\n", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        s = System.Text.RegularExpressions.Regex.Replace(s, @"(\*\*|__)(.+?)\1", "$2");
        s = System.Text.RegularExpressions.Regex.Replace(s, @"`([^`]*)`", "$1");
        s = System.Text.RegularExpressions.Regex.Replace(s, @"(?<![\w*])\*(?!\s)(.+?)(?<!\s)\*(?![\w*])", "$1");
        return s.Trim();
    }

    private static void ReadTable(Table table, List<CueSheetRow> rows, List<string> warnings)
    {
        var header = table.Header.Select(Key).ToList();
        int Col(params string[] names)
        {
            foreach (var n in names)
            {
                int i = header.IndexOf(Key(n));
                if (i >= 0) return i;
            }
            return -1;
        }
        int cType = Col("Tipus", "Tipo", "Type");
        int cName = Col("Nom", "Nombre", "Name");
        int cScene = Col("Escena", "Scene"), cNum = Col("Núm", "Num", "Número", "Numero", "Cue"),
            cEntry = Col("Entrada", "Pie", "Rèplica", "Replica"), cFile = Col("Fitxer", "Arxiu", "Archivo", "File"),
            cTarget = Col("Destí", "Desti", "Target"), cVol = Col("Volum", "Volumen", "Volume"),
            cIn = Col("Fosa entrada", "Fade in"), cOut = Col("Fosa sortida", "Fade out"), cTime = Col("Temps", "Durada", "Time"),
            cLoop = Col("Bucle", "Loop"), cAfter = Col("Després", "Despres", "Continua", "Next"), cNotes = Col("Notes", "Notas");

        foreach (var (line, f) in table.Rows)
        {
            if (f.All(string.IsNullOrWhiteSpace)) continue;
            string Get(int c) => c >= 0 && c < f.Count ? f[c].Trim() : "";
            string scene = Get(cScene);
            var row = new CueSheetRow
            {
                Line = line,
                Scene = scene != "" ? scene : table.Scene, Number = Get(cNum), Type = Get(cType), Name = Get(cName), Entry = Get(cEntry),
                File = Get(cFile), Target = Get(cTarget), Volume = Get(cVol), FadeIn = Get(cIn), FadeOut = Get(cOut),
                Time = Get(cTime), Loop = Get(cLoop), After = Get(cAfter), Notes = Get(cNotes),
            };
            if (row.Type == "" && row.Name == "")
            {
                warnings.Add(string.Format(L.T("Fila {0}: sense tipus ni nom; no s'ha fet servir."), row.Line));
                continue;
            }
            rows.Add(row);
        }
    }

    /// <summary>Escriu files del guió tècnic en CSV o, si el fitxer acaba en .md, en Markdown (p. ex. per passar un .md a CSV).</summary>
    public static void WriteRows(string path, IReadOnlyList<CueSheetRow> rows, string title = "Guió tècnic")
    {
        if (IsMarkdown(path)) { WriteMarkdown(path, rows, title); return; }
        var sb = new StringBuilder();
        sb.AppendLine(string.Join(";", Header));
        foreach (var r in rows)
        {
            var fields = new[] { r.Scene, r.Number, r.Type, r.Name, r.Entry, r.File, r.Target, r.Volume, r.FadeIn, r.FadeOut, r.Time, r.Loop, r.After, r.Notes };
            sb.AppendLine(string.Join(";", fields.Select(Quote)));
        }
        System.IO.File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
    }

    /// <summary>UTF-8 (amb BOM o sense); si no ho és, el CSV que desa l'Excel en català (Windows-1252 ≈ Latin-1).</summary>
    private static string ReadText(string path)
    {
        var bytes = System.IO.File.ReadAllBytes(path);
        try { return new UTF8Encoding(false, true).GetString(bytes).TrimStart('﻿'); }
        catch (DecoderFallbackException) { return Encoding.Latin1.GetString(bytes); }
    }

    /// <summary>Separa el text en registres respectant els salts de línia que hi ha dins de cometes.</summary>
    private static List<string> SplitRecords(string text)
    {
        var list = new List<string>();
        var sb = new StringBuilder();
        bool quoted = false;
        foreach (char ch in text)
        {
            if (ch == '"') quoted = !quoted;
            if (!quoted && (ch == '\n' || ch == '\r'))
            {
                if (sb.Length > 0) list.Add(sb.ToString());
                sb.Clear();
                continue;
            }
            sb.Append(ch);
        }
        if (sb.Length > 0) list.Add(sb.ToString());
        return list;
    }

    private static char DetectSeparator(string header)
    {
        int semi = header.Count(c => c == ';'), comma = header.Count(c => c == ','), tab = header.Count(c => c == '\t');
        return tab > semi && tab > comma ? '\t' : semi >= comma ? ';' : ',';
    }

    private static List<string> ParseRecord(string line, char sep)
    {
        var fields = new List<string>();
        var sb = new StringBuilder();
        bool quoted = false;
        for (int i = 0; i < line.Length; i++)
        {
            char ch = line[i];
            if (quoted)
            {
                if (ch == '"' && i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; }
                else if (ch == '"') quoted = false;
                else sb.Append(ch);
            }
            else if (ch == '"') quoted = true;
            else if (ch == sep) { fields.Add(sb.ToString()); sb.Clear(); }
            else sb.Append(ch);
        }
        fields.Add(sb.ToString());
        return fields;
    }

    /// <summary>Clau per comparar textos sense majúscules ni accents («Destí» = «desti»).</summary>
    public static string Key(string s)
    {
        var norm = s.Trim().ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder();
        foreach (var ch in norm)
            if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark && ch != '·' && ch != '.') sb.Append(ch);
        return sb.ToString().Replace("  ", " ");
    }

    private static bool TryNumber(string s, out double value)
    {
        s = s.Trim().Replace(" ", "").Replace("dB", "", StringComparison.OrdinalIgnoreCase).TrimEnd('s');
        return double.TryParse(s.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    // ---------------- Tipus ----------------

    private enum Kind { Audio, Video, Image, Text, Fade, Stop, Pause, Wait, Memo, Light, Unknown }

    private static Kind KindOf(string type) => Key(type) switch
    {
        "so" or "audio" or "musica" or "efecte" or "sonido" or "sound" => Kind.Audio,
        "video" => Kind.Video,
        "imatge" or "imagen" or "image" or "foto" => Kind.Image,
        "text" or "texto" or "rotol" or "sobretitol" => Kind.Text,
        "fosa" or "fade" => Kind.Fade,
        "atura" or "stop" or "para" => Kind.Stop,
        "pausa" or "pause" => Kind.Pause,
        "espera" or "wait" => Kind.Wait,
        "nota" or "memo" or "escena" => Kind.Memo,
        "llum" or "llums" or "luz" or "light" => Kind.Light,
        _ => Kind.Unknown,
    };

    private static bool Fits(Cue cue, Kind kind) => kind switch
    {
        Kind.Audio => cue is AudioCue,
        Kind.Video => cue is VideoCue,
        Kind.Image => cue is ImageCue,
        Kind.Text => cue is TextCue,
        Kind.Fade => cue is FadeCue,
        Kind.Stop => cue is ControlCue { Action: ControlAction.Stop },
        Kind.Pause => cue is ControlCue { Action: ControlAction.Pause },
        Kind.Wait => cue is WaitCue,
        Kind.Memo or Kind.Light => cue is MemoCue,
        _ => false,
    };

    private static Cue Create(Kind kind) => kind switch
    {
        Kind.Audio => new AudioCue(),
        Kind.Video => new VideoCue(),
        Kind.Image => new ImageCue(),
        Kind.Text => new TextCue(),
        Kind.Fade => new FadeCue(),
        Kind.Stop => new ControlCue { Action = ControlAction.Stop },
        Kind.Pause => new ControlCue { Action = ControlAction.Pause },
        Kind.Wait => new WaitCue(),
        _ => new MemoCue(),
    };

    public const string LightPrefix = "Llum · ";

    // ---------------- Aplicar a un espectacle ----------------

    /// <summary>
    /// Aplica el guió tècnic a la llista de cues: cada fila busca la seva cue pel número (o, si no en té, pel nom)
    /// i l'actualitza amb les columnes que no són buides; si no la troba, en crea una de nova just després de la cue
    /// de la fila anterior. Quan canvia la columna «Escena», hi posa una cue Nota amb el nom de l'escena.
    /// </summary>
    /// <param name="baseDir">Carpeta del CSV: les rutes relatives dels fitxers parteixen d'aquí.</param>
    /// <param name="outputFor">Sortida per a una cue de fitxer nova (null = la primera adequada).</param>
    public static CueSheetReport Apply(IList<Cue> cues, IReadOnlyList<CueSheetRow> rows, string baseDir, Func<MediaCue, Guid?>? outputFor = null)
    {
        var report = new CueSheetReport();
        var touched = new HashSet<Cue>();
        var created = new HashSet<Cue>();
        Cue? anchor = null;
        Cue? lastPlayable = null;
        string scene = "";
        bool hadCues = cues.Count > 0;
        var pendingTargets = new List<(Cue Cue, CueSheetRow Row)>();

        void Place(Cue cue, bool numbered = true)
        {
            int index;
            if (anchor != null && cues.IndexOf(anchor) is var a and >= 0)
            {
                index = ShowTree.SubtreeEnd(cues, a);
                cue.ParentId = anchor.ParentId;
            }
            else index = hadCues && touched.Count == 0 && AnyRowMatches(cues, rows) ? 0 : cues.Count;
            if (numbered && string.IsNullOrEmpty(cue.Number)) cue.Number = NextNumber(cues, rows);
            if (cue is MediaCue m && outputFor != null) m.OutputId = outputFor(m);
            cues.Insert(index, cue);
            created.Add(cue);
            report.Created++;
        }

        foreach (var row in rows)
        {
            // Escena nova: una Nota amb el nom de l'escena (si no hi és ja).
            if (row.Scene != "" && Key(row.Scene) != Key(scene))
            {
                scene = row.Scene;
                var marker = cues.OfType<MemoCue>().FirstOrDefault(c => Key(c.Name) == Key(scene));
                if (marker == null)
                {
                    // Sense número: les marques d'escena no ocupen cap número del guió tècnic.
                    marker = new MemoCue { Name = scene };
                    Place(marker, numbered: false);
                }
                touched.Add(marker);
                anchor = marker;
            }

            var kind = KindOf(row.Type);
            if (kind == Kind.Unknown)
            {
                report.Warnings.Add(string.Format(L.T("Fila {0}: tipus «{1}» desconegut; no s'ha fet servir."), row.Line, row.Type));
                continue;
            }
            // Una fila «nota» igual que l'escena és la mateixa marca: ja està feta.
            if (kind == Kind.Memo && row.Scene != "" && Key(row.Name) == Key(scene) && row.Number == "") continue;

            string name = kind == Kind.Light && row.Name != "" && !row.Name.StartsWith(LightPrefix, StringComparison.OrdinalIgnoreCase)
                ? LightPrefix + row.Name : row.Name;

            Cue? cue = null;
            if (row.Number != "")
            {
                cue = cues.FirstOrDefault(c => !created.Contains(c) && string.Equals(c.Number, row.Number, StringComparison.OrdinalIgnoreCase));
                if (cue != null && !Fits(cue, kind))
                {
                    report.Warnings.Add(string.Format(L.T("Fila {0}: la cue {1} ja existeix i és d'un altre tipus ({2}); no s'ha tocat."), row.Line, row.Number, cue.TypeName));
                    anchor = cue;
                    continue;
                }
            }
            else if (name != "")
                cue = cues.FirstOrDefault(c => !created.Contains(c) && !touched.Contains(c) && Fits(c, kind) && Key(c.Name) == Key(name));

            bool isNew = cue == null;
            cue ??= Create(kind);
            if (isNew && row.Number != "") cue.Number = row.Number;
            if (name != "") cue.Name = name;
            else if (isNew) cue.Name = row.Type;

            Fill(cue, kind, row, baseDir, report, isNew);
            if (isNew) Place(cue);
            else if (touched.Add(cue)) report.Updated++;
            touched.Add(cue);
            anchor = cue;

            if (cue is FadeCue or ControlCue) pendingTargets.Add((cue, row));
            if (cue is FadeCue && row.Target == "" && lastPlayable != null) ((FadeCue)cue).TargetId ??= lastPlayable.Id;
            if (cue is PlayableCue or ImageCue or TextCue) lastPlayable = cue;
        }

        // Destins (al final, perquè poden apuntar a cues que surten més avall).
        foreach (var (cue, row) in pendingTargets)
        {
            if (row.Target == "")
            {
                if (cue is FadeCue { TargetId: null })
                    report.Warnings.Add(string.Format(L.T("Fila {0}: la fosa no té destí."), row.Line));
                continue;
            }
            var target = cues.FirstOrDefault(c => c != cue && string.Equals(c.Number, row.Target, StringComparison.OrdinalIgnoreCase))
                         ?? cues.FirstOrDefault(c => c != cue && Key(c.Name) == Key(row.Target));
            if (target == null)
            {
                report.Warnings.Add(string.Format(L.T("Fila {0}: no es troba la cue destí «{1}»."), row.Line, row.Target));
                continue;
            }
            switch (cue)
            {
                case FadeCue f: f.TargetId = target.Id; break;
                case ControlCue k:
                    k.TargetId = target.Id;
                    k.AllCues = false;
                    break;
            }
        }

        report.NotInSheet = cues.Count(c => c is not GroupCue && !touched.Contains(c));
        return report;
    }

    /// <summary>Número per a una cue nova sense número: el següent als de l'espectacle i als del guió tècnic.</summary>
    private static string NextNumber(IList<Cue> cues, IReadOnlyList<CueSheetRow> rows)
    {
        int max = int.TryParse(Cue.NextNumber(cues), out int n) ? n - 1 : 0;
        foreach (var r in rows)
            if (int.TryParse(new string(r.Number.TakeWhile(char.IsDigit).ToArray()), out int rn)) max = Math.Max(max, rn);
        return (max + 1).ToString();
    }

    private static bool AnyRowMatches(IList<Cue> cues, IReadOnlyList<CueSheetRow> rows) =>
        rows.Any(r => r.Number != "" && cues.Any(c => string.Equals(c.Number, r.Number, StringComparison.OrdinalIgnoreCase)));

    /// <summary>Posa a la cue els valors de les columnes que no són buides.</summary>
    private static void Fill(Cue cue, Kind kind, CueSheetRow row, string baseDir, CueSheetReport report, bool isNew)
    {
        void Warn(string column, string value) =>
            report.Warnings.Add(string.Format(L.T("Fila {0}: «{1}» no és un valor vàlid per a {2}."), row.Line, value, column));

        void Num(string value, string column, Action<double> set)
        {
            if (value == "") return;
            if (TryNumber(value, out double v)) set(v);
            else Warn(column, value);
        }

        // Notes: «Entrada: …» a la primera línia i després les notes.
        // Cada part es canvia per separat: una fila amb només notes no esborra l'entrada que ja hi havia.
        if (row.Entry != "" || row.Notes != "")
        {
            var (entry, notes) = SplitNotes(cue.Notes);
            if (row.Entry != "") entry = row.Entry;
            if (row.Notes != "") notes = row.Notes;
            cue.Notes = entry == "" ? notes : EntryPrefix + entry + (notes == "" ? "" : "\n" + notes);
        }

        // Què passa després.
        switch (Key(row.After))
        {
            case "": if (isNew) cue.NextAction = kind is Kind.Memo or Kind.Light ? NextAction.AutoContinue : NextAction.None; break;
            case "go": case "espera": case "no": cue.NextAction = NextAction.None; break;
            case "continua": case "cont": case "alhora": cue.NextAction = NextAction.AutoContinue; break;
            case "segueix": case "seg": case "en acabar": case "quan acabi": cue.NextAction = NextAction.AutoFollow; break;
            default: Warn(L.T("«Després» (GO, continua o segueix)"), row.After); break;
        }

        if (row.File != "" && cue is MediaCue media and not TextCue)
        {
            string path = Path.IsPathRooted(row.File) ? row.File : Path.GetFullPath(Path.Combine(baseDir, row.File));
            media.FilePath = path;
            if (!System.IO.File.Exists(path))
                report.Warnings.Add(string.Format(L.T("Fila {0}: no es troba el fitxer «{1}»."), row.Line, row.File));
        }
        else if (isNew && cue is PlayableCue or ImageCue)
            report.Warnings.Add(string.Format(L.T("Fila {0}: «{1}» encara no té fitxer."), row.Line, cue.Name));

        if (cue is MediaCue mc)
        {
            Num(row.FadeIn, L.T("«Fosa entrada»"), v => mc.FadeIn = v);
            Num(row.FadeOut, L.T("«Fosa sortida»"), v => mc.FadeOut = v);
        }

        switch (cue)
        {
            case PlayableCue p:
                Num(row.Volume, L.T("«Volum»"), v => p.VolumeDb = v);
                if (row.Loop != "") p.Loop = IsYes(row.Loop);
                break;
            case TextCue t:
                if (t.Text == "" || row.Name != "") t.Text = row.Name;
                Num(row.Time, L.T("«Temps»"), v => t.DisplayTime = v);
                break;
            case FadeCue f:
                Num(row.Volume, L.T("«Volum»"), v => { f.TargetVolumeDb = v; f.StopTarget = v <= AudioMath.SilenceDb; });
                if (row.Volume == "" && isNew) { f.TargetVolumeDb = AudioMath.SilenceDb; f.StopTarget = true; }
                Num(row.Time, L.T("«Temps»"), v => f.FadeTime = v);
                break;
            case ControlCue k:
                if (isNew && row.Target == "") k.AllCues = true;
                Num(row.Time, L.T("«Temps»"), v => { k.FadeTime = v; k.UseOwnFade = false; });
                break;
            case WaitCue w:
                Num(row.Time, L.T("«Temps»"), v => w.WaitTime = v);
                break;
        }
    }

    /// <summary>Separa les notes d'una cue en l'entrada (primera línia «Entrada: …») i la resta.</summary>
    public static (string Entry, string Notes) SplitNotes(string text)
    {
        if (!text.StartsWith(EntryPrefix)) return ("", text);
        int nl = text.IndexOf('\n');
        return ((nl < 0 ? text : text[..nl])[EntryPrefix.Length..].Trim(), nl < 0 ? "" : text[(nl + 1)..]);
    }

    private static bool IsYes(string s) => Key(s) is "si" or "s" or "x" or "yes" or "1" or "true" or "bucle";

    // ---------------- Exportar ----------------

    /// <summary>
    /// Escriu el guió tècnic de l'espectacle: en CSV (amb «;», UTF-8 amb BOM, com el vol l'Excel en català) o,
    /// si el fitxer acaba en .md, en taules de Markdown per a l'Obsidian (un títol «##» per escena).
    /// </summary>
    public static void Write(string path, IList<Cue> cues, string? baseDir = null)
    {
        var byId = cues.ToDictionary(c => c.Id);
        string dir = baseDir ?? Path.GetDirectoryName(Path.GetFullPath(path))!;
        var rows = new List<CueSheetRow>();
        foreach (var cue in cues)
        {
            if (cue is GroupCue or MicCue) continue;
            string type = cue switch
            {
                AudioCue => "so",
                VideoCue => "vídeo",
                ImageCue => "imatge",
                TextCue => "text",
                FadeCue => "fosa",
                ControlCue { Action: ControlAction.Pause } => "pausa",
                ControlCue { Action: ControlAction.Stop } => "atura",
                WaitCue => "espera",
                MemoCue m when m.Name.StartsWith(LightPrefix, StringComparison.OrdinalIgnoreCase) => "llum",
                MemoCue => "nota",
                _ => "",
            };
            if (type == "") continue;
            string name = type == "llum" ? cue.Name[LightPrefix.Length..] : cue.Name;

            var (entry, notes) = SplitNotes(cue.Notes);

            string file = cue is MediaCue { FilePath: not "" } mc and not TextCue
                ? (Path.IsPathRooted(mc.FilePath) ? Path.GetRelativePath(dir, mc.FilePath) : mc.FilePath) : "";
            if (!Path.IsPathRooted(file)) file = file.Replace('\\', '/'); // relatiu: amb «/», com s'escriu al guió
            Guid? targetId = cue switch { FadeCue f => f.TargetId, ControlCue { AllCues: false } k => k.TargetId, _ => null };
            string target = targetId is Guid t && byId.TryGetValue(t, out var tc) ? (tc.Number != "" ? tc.Number : tc.Name) : "";
            string volume = cue switch
            {
                PlayableCue p => p.VolumeDb == 0 ? "" : Fmt(p.VolumeDb), // 0 dB és el de sempre: no cal escriure'l
                FadeCue f => f.TargetVolumeDb <= AudioMath.SilenceDb ? "" : Fmt(f.TargetVolumeDb),
                _ => "",
            };
            string fadeIn = cue is MediaCue m1 && m1.FadeIn > 0 ? Fmt(m1.FadeIn) : "";
            string fadeOut = cue is MediaCue m2 && m2.FadeOut > 0 ? Fmt(m2.FadeOut) : "";
            string time = cue switch
            {
                FadeCue f => Fmt(f.FadeTime),
                WaitCue w => Fmt(w.WaitTime),
                TextCue tx when tx.DisplayTime > 0 => Fmt(tx.DisplayTime),
                ControlCue { Action: ControlAction.Stop, UseOwnFade: false } k => Fmt(k.FadeTime),
                _ => "",
            };
            string loop = cue is PlayableCue { Loop: true } ? "sí" : "";
            string after = cue.NextAction switch
            {
                NextAction.AutoContinue => cue is MemoCue ? "" : "continua",
                NextAction.AutoFollow => "segueix",
                _ => cue is MemoCue ? "GO" : "",
            };
            rows.Add(new CueSheetRow
            {
                Number = cue.Number, Type = type, Name = name, Entry = entry, File = file, Target = target, Volume = volume,
                FadeIn = fadeIn, FadeOut = fadeOut, Time = time, Loop = loop, After = after, Notes = notes,
            });
        }
        WriteRows(path, rows, Path.GetFileNameWithoutExtension(path));
    }

    /// <summary>Una fila «nota» sense número que continua sola: és una marca d'escena (al .md, un títol).</summary>
    private static bool IsSceneMarker(CueSheetRow r) => KindOf(r.Type) == Kind.Memo && r.Number == "" && Key(r.After) == "";

    private static void WriteMarkdown(string path, IReadOnlyList<CueSheetRow> rows, string title)
    {
        string[] Fields(CueSheetRow r) => new[] { r.Scene, r.Number, r.Type, r.Name, r.Entry, r.File, r.Target, r.Volume, r.FadeIn, r.FadeOut, r.Time, r.Loop, r.After, r.Notes };
        var cues = rows.Where(r => !IsSceneMarker(r)).ToList();
        // Columnes: les bàsiques sempre; la resta, només si alguna fila en fa servir.
        var always = new HashSet<int> { 1, 2, 3, 4 };
        var cols = Enumerable.Range(1, Header.Length - 1).Where(i => always.Contains(i) || cues.Any(r => Fields(r)[i] != "")).ToList();
        string Cell(string s) => s.Replace("|", "\\|").Replace("\r\n", "<br>").Replace("\n", "<br>");

        var sb = new StringBuilder();
        sb.AppendLine("# " + title).AppendLine();
        string? scene = null;
        bool inTable = false;
        void StartTable()
        {
            sb.AppendLine("| " + string.Join(" | ", cols.Select(i => Header[i])) + " |");
            sb.AppendLine("|" + string.Join("|", cols.Select(_ => "---")) + "|");
            inTable = true;
        }
        void Heading(string text)
        {
            if (inTable) sb.AppendLine();
            sb.AppendLine("## " + text).AppendLine();
            inTable = false;
        }
        foreach (var r in rows)
        {
            if (IsSceneMarker(r)) { Heading(r.Name); scene = r.Name; continue; }
            if (r.Scene != "" && r.Scene != scene) { Heading(r.Scene); scene = r.Scene; }
            if (!inTable) StartTable();
            var f = Fields(r);
            sb.AppendLine("| " + string.Join(" | ", cols.Select(i => Cell(f[i]))) + " |");
        }
        System.IO.File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
    }

    private static string Fmt(double v) => Math.Round(v, 2).ToString("0.##", CultureInfo.GetCultureInfo("ca-ES"));

    private static string Quote(string s) =>
        s.IndexOfAny(new[] { ';', '"', '\n', '\r' }) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
}
