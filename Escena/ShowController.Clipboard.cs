using System.Text.Json;
using Escena.Model;

namespace Escena;

/// <summary>Copiar, retallar, enganxar i duplicar cues (també diverses alhora i entre espectacles).</summary>
public partial class ShowController
{
    /// <summary>Format propi del porta-retalls (a més del text JSON, per poder enganxar en una altra finestra d'Escena).</summary>
    public const string ClipboardFormat = "Escena.Cues";

    /// <summary>
    /// Les cues triades que no són dins d'una altra de les triades, en ordre de llista, cadascuna amb tot el
    /// que conté (si és un grup).
    /// </summary>
    public List<Cue> CopyBlock(IEnumerable<Cue> selection)
    {
        var set = selection.Where(Cues.Contains).ToList();
        var roots = set.Where(c => !set.Any(o => o != c && ShowTree.IsDescendant(Cues, c, o)))
            .OrderBy(Cues.IndexOf).ToList();
        var block = new List<Cue>();
        foreach (var r in roots)
            foreach (var c in ShowTree.Subtree(Cues, r))
                if (!block.Contains(c)) block.Add(c);
        return block;
    }

    /// <summary>Les cues (amb el que contenen) en JSON, per al porta-retalls.</summary>
    public string SerializeCues(IEnumerable<Cue> selection) =>
        JsonSerializer.Serialize(CopyBlock(selection), ShowFile.Options);

    /// <summary>
    /// Enganxa cues copiades: van després de la seleccionada (i del que contingui), al seu nivell, com una cue nova.
    /// Reben identificadors nous; si el número ja hi és, en reben un de nou. Les foses i aturades que apunten a una
    /// cue copiada alhora passen a apuntar a la còpia. Retorna les cues de més amunt que s'han enganxat.
    /// </summary>
    public List<Cue> PasteCues(string json)
    {
        List<Cue>? pasted;
        try { pasted = JsonSerializer.Deserialize<List<Cue>>(json, ShowFile.Options); }
        catch (JsonException) { pasted = null; }
        if (pasted == null || pasted.Count == 0) return new List<Cue>();

        var map = pasted.ToDictionary(c => c.Id, _ => Guid.NewGuid());
        var oldParents = pasted.ToDictionary(c => c.Id, c => c.ParentId);
        var numbers = Cues.Select(c => c.Number).Where(n => n != "").ToHashSet(StringComparer.OrdinalIgnoreCase);

        // On van: com una cue nova (després de la seleccionada, al seu nivell).
        int index = Cues.Count;
        Guid? parent = null;
        if (SelectedCue != null && Cues.IndexOf(SelectedCue) is var s and >= 0)
        {
            index = ShowTree.SubtreeEnd(Cues, s);
            parent = SelectedCue.ParentId;
        }

        var roots = new List<Cue>();
        _moving = true;
        try
        {
            foreach (var cue in pasted)
            {
                Guid oldId = cue.Id;
                cue.Id = map[oldId];
                if (oldParents[oldId] is Guid op && map.TryGetValue(op, out var np)) cue.ParentId = np;
                else
                {
                    cue.ParentId = parent;
                    roots.Add(cue);
                }
                switch (cue)
                {
                    case FadeCue f when f.TargetId is Guid t && map.TryGetValue(t, out var nt): f.TargetId = nt; break;
                    case ControlCue k when k.TargetId is Guid t && map.TryGetValue(t, out var nt): k.TargetId = nt; break;
                    case GroupCue g:
                        g.Targets = null;
                        g.Offsets = null;
                        break;
                }
                if (cue is MediaCue media && Outputs.All(o => o.Id != media.OutputId))
                    media.OutputId = EnsureOutputFor(media)?.Id; // ve d'un altre espectacle
                if (cue is VideoCue v && v.AudioOutputId is Guid a && Outputs.All(o => o.Id != a)) v.AudioOutputId = null;
                if (cue.Number != "" && numbers.Contains(cue.Number)) cue.Number = "";
                if (cue.Number == "")
                {
                    cue.Number = NextFreeNumber(numbers);
                }
                numbers.Add(cue.Number);
                Cues.Insert(index++, cue);
            }
        }
        finally { _moving = false; }
        AfterTreeChange();
        UpdateDuplicateWarning();
        if (roots.Count > 0) SelectedCue = roots[0];
        if (StandbyCue == null && Cues.Count == pasted.Count) StandbyCue = Cues[0];
        Status = pasted.Count == 1 ? "S'ha enganxat 1 cue." : $"S'han enganxat {pasted.Count} cues.";
        return roots;
    }

    /// <summary>Esborra les cues triades (i el que contenen). Retorna quantes n'han sortit.</summary>
    public int RemoveCues(IEnumerable<Cue> selection)
    {
        var block = CopyBlock(selection);
        var roots = block.Where(c => c.ParentId == null || block.All(o => o.Id != c.ParentId)).ToList();
        foreach (var r in roots) RemoveCue(r);
        return block.Count;
    }

    /// <summary>Duplica les cues triades just després (sense tocar el porta-retalls).</summary>
    public List<Cue> DuplicateCues(IEnumerable<Cue> selection)
    {
        var block = CopyBlock(selection);
        if (block.Count == 0) return new List<Cue>();
        // Les còpies van després de l'última triada, al nivell d'aquella.
        var lastRoot = block.Where(c => c.ParentId == null || block.All(o => o.Id != c.ParentId)).Last();
        SelectedCue = lastRoot;
        return PasteCues(SerializeCues(block));
    }

    private static string NextFreeNumber(HashSet<string> used)
    {
        int max = 0;
        foreach (var n in used)
        {
            var digits = new string(n.TakeWhile(char.IsDigit).ToArray());
            if (int.TryParse(digits, out int v)) max = Math.Max(max, v);
        }
        return (max + 1).ToString();
    }
}
