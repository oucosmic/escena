using System.Globalization;
using System.Text;
using Escena.Model;

namespace Escena.QLab;

/// <summary>Resultat d'una exportació cap al QLab.</summary>
public sealed record QLabExportResult(string Folder, string ScriptPath, string ReportPath, int CueCount, int Copied, int Missing, IReadOnlyList<string> Warnings);

/// <summary>
/// Mòdul a part: exporta un espectacle d'Escena cap al QLab 5 (Mac).
/// El QLab no importa fitxers d'altres programes, però es pot programar amb AppleScript; per això
/// l'exportació genera una carpeta amb:
///  · <c>media/</c>: tots els fitxers de so, vídeo i imatge;
///  · <c>Importa a QLab.applescript</c>: un script que, executat al Mac amb un espai de treball obert, crea les cues;
///  · <c>Informe QLab.txt</c>: com fer-ho i què no es pot traslladar.
/// Només llegeix les cues: no modifica l'espectacle ni depèn de la interfície.
/// </summary>
public static class QLabExporter
{
    public const string ScriptName = "Importa a QLab.applescript";
    public const string ReportName = "Informe QLab.txt";
    public const string MediaFolder = "media";

    public static QLabExportResult Export(IReadOnlyList<Cue> cues, string destDir, string showName)
    {
        var w = new Writer(cues, showName);
        Directory.CreateDirectory(destDir);
        w.CopyMedia(Path.Combine(destDir, MediaFolder));
        string script = w.BuildScript();
        string scriptPath = Path.Combine(destDir, ScriptName);
        // Amb BOM: l'Editor de Scripts del Mac reconeix així els accents.
        File.WriteAllText(scriptPath, script, new UTF8Encoding(true));
        string reportPath = Path.Combine(destDir, ReportName);
        File.WriteAllText(reportPath, w.BuildReport(), new UTF8Encoding(true));
        return new QLabExportResult(destDir, scriptPath, reportPath, cues.Count, w.Copied, w.Missing, w.Warnings);
    }

    private sealed class Writer
    {
        private readonly IReadOnlyList<Cue> _cues;
        private readonly Dictionary<Guid, Cue> _byId;
        private readonly string _showName;
        private readonly StringBuilder _sb = new();
        private int _indent;
        private int _varCount;

        /// <summary>Fitxer d'origen → nom dins de media/.</summary>
        private readonly Dictionary<string, string> _media = new(StringComparer.OrdinalIgnoreCase);
        /// <summary>Cue d'Escena → variable AppleScript del fitxer (alias).</summary>
        private readonly Dictionary<Guid, string> _fileVar = new();
        /// <summary>Cue d'Escena → variable de la cue del QLab (el grup que l'embolcalla, si n'hi ha).</summary>
        private readonly Dictionary<Guid, string> _cueVar = new();
        /// <summary>Cue d'Escena → variable on han d'apuntar foses i aturades (el fitxer de dins, si està embolcallada).</summary>
        private readonly Dictionary<Guid, string> _targetVar = new();
        private readonly List<(string Var, string Number)> _numbers = new();
        private readonly List<(string Var, Guid Target, string Label)> _targets = new();

        public readonly List<string> Warnings = new();
        public int Copied, Missing;
        private bool _curveWarned, _outputsUsed;

        public Writer(IReadOnlyList<Cue> cues, string showName)
        {
            _cues = cues;
            _showName = showName;
            _byId = cues.GroupBy(c => c.Id).ToDictionary(g => g.Key, g => g.First());
        }

        // ================= Fitxers =================

        public void CopyMedia(string mediaDir)
        {
            Directory.CreateDirectory(mediaDir);
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var cue in _cues.OfType<MediaCue>())
            {
                if (cue is TextCue or MicCue || string.IsNullOrWhiteSpace(cue.FilePath)) continue;
                string source;
                try { source = Path.GetFullPath(cue.FilePath); }
                catch { source = cue.FilePath; }
                if (!File.Exists(source))
                {
                    Missing++;
                    Warn(cue, "el fitxer no es troba i no s'ha copiat: " + cue.FilePath);
                    continue;
                }
                if (!_media.TryGetValue(source, out var name))
                {
                    string baseName = Path.GetFileName(source);
                    name = baseName;
                    for (int n = 2; used.Contains(name); n++)
                        name = $"{Path.GetFileNameWithoutExtension(baseName)} ({n}){Path.GetExtension(baseName)}";
                    used.Add(name);
                    File.Copy(source, Path.Combine(mediaDir, name), overwrite: true);
                    Copied++;
                    _media[source] = name;
                }
                _fileVar[cue.Id] = "f" + (_fileVar.Count + 1);
                _fileName[cue.Id] = name;
            }
        }

        private readonly Dictionary<Guid, string> _fileName = new();

        // ================= Script =================

        public string BuildScript()
        {
            L("-- " + _showName + " · exportat des d'Escena el " + DateTime.Now.ToString("d/M/yyyy H:mm", CultureInfo.InvariantCulture));
            L("-- Obre el QLab 5 amb un espai de treball, obre aquest fitxer amb l'Editor de Scripts i prem ▶ (Executa).");
            L("-- Les cues es creen al final de la llista de cues actual. Llegeix «" + ReportName + "».");
            L("");
            L("set baseFolder to \"\"");
            L("try");
            L("\tset baseFolder to my folderOf(POSIX path of (path to me))");
            L("end try");
            L("set mediaOK to false");
            L("if baseFolder is not \"\" then");
            L("\ttry");
            L("\t\tdo shell script \"test -d \" & quoted form of (baseFolder & \"" + MediaFolder + "\")");
            L("\t\tset mediaOK to true");
            L("\tend try");
            L("end if");
            L("if not mediaOK then set baseFolder to POSIX path of (choose folder with prompt \"Tria la carpeta exportada des d'Escena (la que conté «" + MediaFolder + "»)\")");
            L("set mediaFolder to baseFolder & \"" + MediaFolder + "/\"");
            L("set errs to {}");
            L("");
            L("-- Fitxers");
            foreach (var cue in _cues)
            {
                if (!_fileVar.TryGetValue(cue.Id, out var fv)) continue;
                L("set " + fv + " to missing value");
                Try(cue.DisplayName, "set " + fv + " to (POSIX file (mediaFolder & " + S(_fileName[cue.Id]) + ")) as alias");
            }
            L("");
            L("tell application id \"com.figure53.QLab.5\"");
            _indent++;
            L("activate");
            L("if (count of workspaces) is 0 then");
            L("\tdisplay dialog \"Obre o crea un espai de treball al QLab i torna a executar l'script.\" buttons {\"D'acord\"} default button 1");
            L("\treturn");
            L("end if");
            L("tell front workspace");
            _indent++;
            L("set theList to current cue list");

            foreach (var cue in _cues)
            {
                L("");
                L("-- " + OneLine(cue.TypeName + " · " + cue.DisplayName));
                string parent = cue.ParentId is Guid p && _cueVar.TryGetValue(p, out var pv) ? pv : "theList";
                EmitCue(cue, parent);
                CheckUntransferred(cue);
            }

            L("");
            L("-- Destins de les foses i les aturades");
            foreach (var (v, target, label) in _targets)
            {
                if (_targetVar.TryGetValue(target, out var tv)) Try(label, "set cue target of " + v + " to " + tv);
            }
            L("");
            L("-- Números (al final, perquè els que posa el QLab sol no hi xoquin)");
            foreach (var (v, number) in _numbers) Try(number, "set q number of " + v + " to " + S(number));

            _indent--;
            L("end tell");
            _indent--;
            L("end tell");
            L("");
            L("set msg to \"S'han importat " + _cues.Count + " cues d'Escena.\"");
            L("if (count of errs) > 0 then");
            L("\tset AppleScript's text item delimiters to return");
            L("\tif (count of errs) > 25 then");
            L("\t\tset shown to (items 1 thru 25 of errs) as text");
            L("\telse");
            L("\t\tset shown to errs as text");
            L("\tend if");
            L("\tset AppleScript's text item delimiters to \"\"");
            L("\tset msg to msg & return & return & (count of errs) & \" coses no s'han pogut posar:\" & return & shown");
            L("end if");
            L("display dialog msg buttons {\"D'acord\"} default button 1");
            L("");
            L("on folderOf(p)");
            L("\tset oldTID to AppleScript's text item delimiters");
            L("\tset AppleScript's text item delimiters to \"/\"");
            L("\tset parts to text items of p");
            L("\tset res to (items 1 thru -2 of parts) as text");
            L("\tset AppleScript's text item delimiters to oldTID");
            L("\treturn res & \"/\"");
            L("end folderOf");
            return _sb.ToString();
        }

        private void EmitCue(Cue cue, string parent)
        {
            switch (cue)
            {
                case GroupCue g:
                {
                    string v = Make("group", parent, cue);
                    Common(v, cue);
                    Try(cue.DisplayName, "set mode of " + v + " to " + GroupMode(g.Mode));
                    if (g.Mode == Model.GroupMode.Playlist)
                    {
                        if (g.LoopPlaylist) Try(cue.DisplayName, "set playlist loop of " + v + " to true");
                        if (g.Shuffle) Try(cue.DisplayName, "set playlist shuffle of " + v + " to true");
                    }
                    _cueVar[cue.Id] = _targetVar[cue.Id] = v;
                    break;
                }
                case AudioCue or VideoCue or ImageCue or TextCue or MicCue:
                    EmitMedia((MediaCue)cue, parent);
                    break;
                case FadeCue f:
                    EmitFade(f, parent);
                    break;
                case ControlCue c:
                    EmitControl(c, parent);
                    break;
                case MemoCue:
                {
                    string v = Make("memo", parent, cue);
                    Common(v, cue);
                    _cueVar[cue.Id] = _targetVar[cue.Id] = v;
                    break;
                }
                case WaitCue wc:
                {
                    string v = Make("wait", parent, cue);
                    Common(v, cue);
                    Try(cue.DisplayName, "set duration of " + v + " to " + N(wc.WaitTime));
                    _cueVar[cue.Id] = _targetVar[cue.Id] = v;
                    break;
                }
                default:
                {
                    string v = Make("memo", parent, cue);
                    Common(v, cue);
                    _cueVar[cue.Id] = _targetVar[cue.Id] = v;
                    Warn(cue, "aquest tipus de cue no existeix al QLab: s'ha creat una cue Memo");
                    break;
                }
            }
        }

        // ---- Fitxers, textos i micròfons ----

        private void EmitMedia(MediaCue cue, string parent)
        {
            double displayTime = cue switch { ImageCue i => i.DisplayTime, TextCue t => t.DisplayTime, _ => 0 };
            bool wrap = cue.FadeIn > 0 || displayTime > 0;
            string label = cue.DisplayName;

            // El QLab no té fosa d'entrada ni temps en pantalla dins de la cue: es fa amb un grup
            // «línia de temps» que conté la cue (començant en silenci o transparent) i les foses.
            string container = parent, v;
            if (wrap)
            {
                container = Make("group", parent, cue);
                Common(container, cue);
                Try(label, "set mode of " + container + " to timeline");
                v = Make(QType(cue), container, null);
                Try(label, "set q name of " + v + " to " + S(cue.Name));
            }
            else
            {
                v = Make(QType(cue), parent, cue);
                Common(v, cue);
            }
            _cueVar[cue.Id] = wrap ? container : v;
            _targetVar[cue.Id] = v;

            if (_fileVar.TryGetValue(cue.Id, out var fv))
            {
                L("if " + fv + " is not missing value then");
                _indent++;
                Try(label, "set file target of " + v + " to " + fv);
                _indent--;
                L("end if");
            }

            double levelDb = cue switch { PlayableCue p => p.VolumeDb, MicCue m => m.VolumeDb, _ => 0 };
            bool hasAudio = cue is AudioCue or VideoCue or MicCue;
            bool visual = cue.IsVisual;

            if (cue is PlayableCue pc)
            {
                if (pc.StartAt > 0) Try(label, "set start time of " + v + " to " + N(pc.StartAt));
                if (pc.EndAt > pc.StartAt) Try(label, "set end time of " + v + " to " + N(pc.EndAt));
                if (pc.Loop) Try(label, "set infinite loop of " + v + " to true");
            }
            if (cue is AudioCue ac) EmitSlices(v, ac);
            if (cue is TextCue tc) EmitText(v, tc);
            if (cue is MicCue) Warn(cue, "cal triar l'entrada de micròfon al QLab (Audio Patch de la cue Mic)");

            if (cue.OutputId != null) _outputsUsed = true;

            if (cue.FadeIn > 0)
            {
                if (hasAudio) Try(label, "setLevel " + v + " row 0 column 0 db -120");
                if (visual) Try(label, "set opacity of " + v + " to 0");
                string f = Make("fade", container, null);
                Try(label, "set q name of " + f + " to " + S("Fosa d'entrada · " + cue.Name));
                Try(label, "set duration of " + f + " to " + N(cue.FadeIn));
                Try(label, "set cue target of " + f + " to " + v);
                if (hasAudio) Try(label, "setLevel " + f + " row 0 column 0 db " + N(levelDb));
                if (visual)
                {
                    Try(label, "set do opacity of " + f + " to true");
                    Try(label, "set opacity of " + f + " to 1");
                }
                CurveWarning(cue.FadeInCurve);
            }
            else if (hasAudio && Math.Abs(levelDb) > 0.001)
            {
                Try(label, "setLevel " + v + " row 0 column 0 db " + N(levelDb));
            }

            if (displayTime > 0)
            {
                double fadeOut = Math.Min(cue.FadeOut, displayTime);
                if (fadeOut > 0)
                {
                    string f = Make("fade", container, null);
                    Try(label, "set q name of " + f + " to " + S("Fosa de sortida · " + cue.Name));
                    Try(label, "set pre wait of " + f + " to " + N(displayTime - fadeOut));
                    Try(label, "set duration of " + f + " to " + N(fadeOut));
                    Try(label, "set cue target of " + f + " to " + v);
                    Try(label, "set do opacity of " + f + " to true");
                    Try(label, "set opacity of " + f + " to 0");
                    Try(label, "set stop target when done of " + f + " to true");
                    CurveWarning(cue.FadeOutCurve);
                }
                else
                {
                    string s = Make("stop", container, null);
                    Try(label, "set q name of " + s + " to " + S("Treu · " + cue.Name));
                    Try(label, "set pre wait of " + s + " to " + N(displayTime));
                    Try(label, "set cue target of " + s + " to " + v);
                }
            }
        }

        private void EmitSlices(string v, AudioCue cue)
        {
            var slices = cue.Slices.Where(s => s.IsValid).OrderBy(s => s.Start).ToList();
            if (slices.Count == 0) return;
            var markers = new List<string>();
            double last = 0;
            foreach (var s in slices)
            {
                if (s.Start < last - 0.001)
                {
                    Warn(cue, "un bucle es solapa amb l'anterior i no s'ha exportat");
                    continue;
                }
                // Al QLab cada marca és el FINAL d'un tros: el tros [inici, final] es repeteix.
                if (s.Start > 0.05 && s.Start > last + 0.001) markers.Add("{time:" + N(s.Start) + ", playCount:1}");
                markers.Add("{time:" + N(s.End) + ", playCount:" + (s.Repeats == 0 ? "-1" : (s.Repeats + 1).ToString(CultureInfo.InvariantCulture)) + "}");
                last = s.End;
            }
            Try(cue.DisplayName, "set slice markers of " + v + " to {" + string.Join(", ", markers) + "}");
        }

        private void EmitText(string v, TextCue cue)
        {
            string label = cue.DisplayName;
            Try(label, "set text of " + v + " to " + S(cue.Text));
            var (r, g, b) = ParseColor(cue.Color);
            string format = "{fontSize:" + N(cue.FontSize) + ", rgbaColor:{red:" + N(r) + ", green:" + N(g) + ", blue:" + N(b) + ", alpha:1}";
            if (cue.BackgroundOpacity > 0) format += ", backgroundRgbaColor:{red:0, green:0, blue:0, alpha:" + N(cue.BackgroundOpacity) + "}";
            format += "}";
            Try(label, "set text format of " + v + " to " + format);
            Try(label, "set text alignment of " + v + " to \"center\"");
            if (cue.Placement != TextPlacement.Center)
            {
                // Posició aproximada en una pantalla de 1080 línies (el QLab mesura des del centre).
                Try(label, "set translation y of " + v + " to " + (cue.Placement == TextPlacement.Top ? "400" : "-400"));
                Warn(cue, "comprova la posició del text (dalt/baix) a l'escenari del QLab");
            }
            if (cue.ReplaceTexts) Warn(cue, "«Treu els altres textos» no existeix al QLab: cal una cue Stop o Fade per treure el text anterior");
        }

        // ---- Foses ----

        private void EmitFade(FadeCue fade, string parent)
        {
            string label = fade.DisplayName;
            string v = Make("fade", parent, fade);
            Common(v, fade);
            _cueVar[fade.Id] = _targetVar[fade.Id] = v;
            Try(label, "set duration of " + v + " to " + N(fade.FadeTime));
            var target = fade.TargetId is Guid t && _byId.TryGetValue(t, out var tc) ? tc : null;
            if (target == null)
            {
                Warn(fade, "la fosa no té cue destí");
                return;
            }
            _targets.Add((v, target.Id, label));
            bool targetAudio = target is AudioCue or VideoCue or MicCue;
            bool targetVisual = target is MediaCue m && m.IsVisual;
            if (fade.FadeVolume && targetAudio) Try(label, "setLevel " + v + " row 0 column 0 db " + N(fade.TargetVolumeDb));
            if (targetVisual && fade.AffectsPicture)
            {
                Try(label, "set do opacity of " + v + " to true");
                Try(label, "set opacity of " + v + " to " + N(DbToOpacity(fade.TargetVolumeDb)));
            }
            if (fade.StopTarget) Try(label, "set stop target when done of " + v + " to true");
            if (fade.FadePan) Warn(fade, "la fosa de panorama no es pot traslladar (al QLab es fa amb els nivells de la matriu)");
            CurveWarning(fade.Curve);
        }

        // ---- Atura / Pausa / Reprèn / Surt del bucle ----

        private void EmitControl(ControlCue c, string parent)
        {
            string label = c.DisplayName;
            var target = !c.AllCues && c.TargetId is Guid t && _byId.TryGetValue(t, out var tc) ? tc : null;
            string v;
            if (target == null)
            {
                if (!c.AllCues) Warn(c, "no té cue destí");
                string command = c.Action switch
                {
                    ControlAction.Pause => "pause front workspace",
                    ControlAction.Resume => "resume front workspace",
                    ControlAction.Stop when c.UseOwnFade || c.FadeTime > 0 => "panic front workspace",
                    ControlAction.Stop => "stop front workspace",
                    _ => "",
                };
                if (command == "")
                {
                    v = Make("memo", parent, c);
                    Common(v, c);
                    Warn(c, "«Surt del bucle» de totes les cues no existeix al QLab: cal una cue Devamp per a cada cue");
                }
                else
                {
                    v = Make("script", parent, c);
                    Common(v, c);
                    Try(label, "set script source of " + v + " to " + S("tell application id \"com.figure53.QLab.5\" to " + command));
                    if (command.StartsWith("panic"))
                        Warn(c, "atura-ho tot amb fosa: al QLab fa servir la durada de pànic de l'espai de treball");
                }
                _cueVar[c.Id] = _targetVar[c.Id] = v;
                return;
            }

            double fadeTime = c.Action == ControlAction.Stop
                ? (c.UseOwnFade ? (target as MediaCue)?.FadeOut ?? 0 : c.FadeTime)
                : 0;
            if (fadeTime > 0)
            {
                // Aturada amb fosa = cue Fade cap a silenci (i negre) que atura la cue en acabar.
                v = Make("fade", parent, c);
                Common(v, c);
                Try(label, "set duration of " + v + " to " + N(fadeTime));
                if (target is AudioCue or VideoCue or MicCue) Try(label, "setLevel " + v + " row 0 column 0 db -120");
                if (target is MediaCue m && m.IsVisual)
                {
                    Try(label, "set do opacity of " + v + " to true");
                    Try(label, "set opacity of " + v + " to 0");
                }
                Try(label, "set stop target when done of " + v + " to true");
            }
            else
            {
                string type = c.Action switch
                {
                    ControlAction.Pause => "pause",
                    ControlAction.Resume => "start", // al QLab, una cue Start sobre una cue en pausa la reprèn
                    ControlAction.Devamp => "devamp",
                    _ => "stop",
                };
                v = Make(type, parent, c);
                Common(v, c);
            }
            _targets.Add((v, target.Id, label));
            _cueVar[c.Id] = _targetVar[c.Id] = v;
        }

        // ---- Comú ----

        /// <summary>Crea una cue, la deixa al final del seu pare i la retorna en una variable nova.</summary>
        private string Make(string type, string parent, Cue? numbered)
        {
            string v = "q" + (++_varCount);
            L("make type " + S(type));
            L("set " + v + " to last item of (selected as list)");
            Try(numbered?.DisplayName ?? type, "set q number of " + v + " to \"\"");
            Try(numbered?.DisplayName ?? type, "move " + v + " to end of " + parent);
            if (numbered != null && numbered.Number != "") _numbers.Add((v, numbered.Number));
            return v;
        }

        private void Common(string v, Cue cue)
        {
            string label = cue.DisplayName;
            Try(label, "set q name of " + v + " to " + S(cue.Name));
            if (cue.Notes != "") Try(label, "set notes of " + v + " to " + S(cue.Notes));
            if (cue.PreWait > 0) Try(label, "set pre wait of " + v + " to " + N(cue.PreWait));
            if (cue.PostWait > 0) Try(label, "set post wait of " + v + " to " + N(cue.PostWait));
            if (cue.NextAction != NextAction.None)
                Try(label, "set continue mode of " + v + " to " + (cue.NextAction == NextAction.AutoContinue ? "auto_continue" : "auto_follow"));
        }

        private void CheckUntransferred(Cue cue)
        {
            if (cue.Hotkey != "") Warn(cue, "tecla directa «" + cue.Hotkey + "»: cal posar-la a mà al QLab (Triggers → Hotkey)");
            double pan = cue switch { PlayableCue p => p.Pan, MicCue m => m.Pan, _ => 0 };
            if (cue is AudioCue or MicCue && Math.Abs(pan) > 0.001) Warn(cue, "panorama " + N(pan) + ": cal fer-lo amb els nivells de la matriu del QLab");
            if (cue is IAudioSource src)
            {
                if (src.Routing.Crosspoints.Count > 0) Warn(cue, "té una matriu d'encaminament pròpia: cal refer-la als nivells de la cue del QLab");
                if (src.Routing.Sends.Count > 0) Warn(cue, "té enviaments a altres sortides: al QLab es fan amb els nivells de sortida de la cue");
                if (src.Routing.DuckOthers) Warn(cue, "té ducking: al QLab cal fer-lo amb cues Fade");
            }
        }

        private void CurveWarning(FadeCurve curve)
        {
            if (curve == FadeCurve.Linear || _curveWarned) return;
            _curveWarned = true;
            Warnings.Add("General: les corbes de fosa no es poden posar per script; totes les foses queden amb la corba per defecte del QLab.");
        }

        private void Warn(Cue cue, string text) => Warnings.Add(cue.DisplayName + ": " + text);

        private void Try(string label, string statement)
        {
            L("try");
            L("\t" + statement);
            L("on error errMsg");
            L("\tset end of errs to " + S(OneLine(label) + ": ") + " & errMsg");
            L("end try");
        }

        private void L(string line)
        {
            if (line.Length > 0) _sb.Append('\t', _indent);
            _sb.Append(line).Append('\n');
        }

        // ================= Informe =================

        public string BuildReport()
        {
            var r = new StringBuilder();
            r.AppendLine("EXPORTACIÓ AL QLAB · " + _showName);
            r.AppendLine();
            r.AppendLine("COM IMPORTAR-HO");
            r.AppendLine("1. Copia aquesta carpeta sencera al Mac (amb la carpeta «media»).");
            r.AppendLine("2. Obre el QLab 5 i obre o crea un espai de treball. Les cues s'afegiran al final de la llista de cues actual.");
            r.AppendLine("3. Fes doble clic a «" + ScriptName + "»: s'obre l'Editor de Scripts. Prem ▶ (Executa).");
            r.AppendLine("   La primera vegada el Mac et demanarà permís perquè l'Editor de Scripts controli el QLab: digues que sí.");
            r.AppendLine("   Si no troba la carpeta «media», et demanarà que triïs aquesta carpeta.");
            r.AppendLine("4. En acabar, un missatge diu quantes cues s'han creat i què no s'ha pogut posar.");
            r.AppendLine("5. Al QLab, desa l'espai de treball. Si vols que els fitxers hi quedin copiats, fes File → Bundle Workspace.");
            r.AppendLine();
            r.AppendLine("COM QUEDA CADA COSA");
            r.AppendLine("· Número, nom, notes, pre-espera, post-espera, Continua (auto-continue) i Segueix (auto-follow).");
            r.AppendLine("· Àudio, vídeo i micròfon: fitxer, volum, inici, final, bucle i bucles dins del fitxer (slices; «repeticions 0» = bucle infinit fins a una cue Devamp).");
            r.AppendLine("· Imatges: cue Video amb la imatge. Textos: cue Text amb la mida i el color.");
            r.AppendLine("· Fosa d'entrada o temps en pantalla: un grup «Timeline» que conté la cue i les cues Fade (o Stop) que ho fan.");
            r.AppendLine("· Fosa: cue Fade amb el volum final, l'opacitat (vídeo i imatge) i «Stop target when done».");
            r.AppendLine("· Atura amb fosa: cue Fade cap a silenci que atura la cue. Atura sense fosa, Pausa, Reprèn (cue Start) i Surt del bucle (Devamp).");
            r.AppendLine("· Atura/Pausa/Reprèn totes: cue Script que ho fa a tot l'espai de treball.");
            r.AppendLine("· Espera: cue Wait. Grups: cue Group amb el mateix mode (Timeline, Playlist, Start first and enter, Start first, Start random).");
            r.AppendLine();
            r.AppendLine("QUÈ CAL FER A MÀ AL QLAB");
            r.AppendLine("· Sortides: les sortides d'Escena no passen al QLab. Configura els Audio Patches i els escenaris (Stages) del QLab i, si cal, canvia-ho a les cues.");
            if (_outputsUsed) r.AppendLine("  (Aquest espectacle fa servir sortides concretes en algunes cues: revisa-les.)");
            r.AppendLine("· Correcció del projector: es fa a l'escenari del QLab (Video Stage → Edit Geometry).");
            r.AppendLine("· El so dels vídeos i la posició dels textos depenen de l'escenari del QLab: revisa-ho.");
            r.AppendLine();
            if (Warnings.Count > 0)
            {
                r.AppendLine("AVISOS D'AQUEST ESPECTACLE (" + Warnings.Count + ")");
                foreach (var w in Warnings.Distinct()) r.AppendLine("· " + w);
            }
            else r.AppendLine("Cap avís: tot el que fa servir aquest espectacle es pot traslladar.");
            r.AppendLine();
            r.AppendLine("Nota: l'script s'ha fet seguint el diccionari AppleScript del QLab 5. Si alguna propietat no l'accepta la teva versió del QLab, l'script continua i ho diu al missatge final.");
            return r.ToString();
        }

        // ================= Utilitats =================

        private static string QType(MediaCue cue) => cue switch
        {
            AudioCue => "audio",
            MicCue => "mic",
            TextCue => "text",
            _ => "video", // vídeo i imatge
        };

        private static string GroupMode(GroupMode mode) => mode switch
        {
            Model.GroupMode.Playlist or Model.GroupMode.Shuffle => "playlist",
            Model.GroupMode.StartFirstAndEnter => "start_first_and_enter",
            Model.GroupMode.StartFirst => "start_first",
            Model.GroupMode.StartRandom => "start_random",
            _ => "timeline",
        };

        private static double DbToOpacity(double db) => db <= -59.5 ? 0 : Math.Clamp(Math.Pow(10, db / 20), 0, 1);

        private static (double R, double G, double B) ParseColor(string color)
        {
            string hex = color.TrimStart('#');
            if (hex.Length == 8) hex = hex[2..]; // #AARRGGBB
            if (hex.Length == 6 && int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int v))
                return (((v >> 16) & 255) / 255.0, ((v >> 8) & 255) / 255.0, (v & 255) / 255.0);
            return (1, 1, 1);
        }

        public static string N(double value) => Math.Round(value, 3).ToString("0.###", CultureInfo.InvariantCulture);

        /// <summary>Text literal d'AppleScript.</summary>
        public static string S(string text) =>
            "\"" + text.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r\n", "\n").Replace("\r", "\n").Replace("\n", "\\n").Replace("\t", "\\t") + "\"";

        private static string OneLine(string text) => text.Replace("\r", " ").Replace("\n", " ");
    }
}
