namespace Escena.Model;

/// <summary>
/// Ajudes per a l'arbre de grups dins de la llista plana de cues: cada grup va seguit de les seves fills
/// (i les fills dels seus subgrups), de manera que un grup i tot el que conté són sempre un bloc seguit.
/// </summary>
public static class ShowTree
{
    public static Cue? Parent(IList<Cue> cues, Cue cue) =>
        cue.ParentId == null ? null : cues.FirstOrDefault(c => c.Id == cue.ParentId);

    /// <summary>És <paramref name="cue"/> dins (a qualsevol nivell) de <paramref name="ancestor"/>?</summary>
    public static bool IsDescendant(IList<Cue> cues, Cue cue, Cue ancestor)
    {
        var p = Parent(cues, cue);
        for (int guard = 0; p != null && guard < 64; guard++)
        {
            if (p == ancestor) return true;
            p = Parent(cues, p);
        }
        return false;
    }

    /// <summary>Índex just després de l'últim descendent de la cue de la posició <paramref name="index"/>.</summary>
    public static int SubtreeEnd(IList<Cue> cues, int index)
    {
        var root = cues[index];
        int i = index + 1;
        while (i < cues.Count && IsDescendant(cues, cues[i], root)) i++;
        return i;
    }

    /// <summary>La cue i tot el que conté, en ordre.</summary>
    public static List<Cue> Subtree(IList<Cue> cues, Cue cue)
    {
        int i = cues.IndexOf(cue);
        return i < 0 ? new() : cues.Skip(i).Take(SubtreeEnd(cues, i) - i).ToList();
    }

    /// <summary>Fills directes d'un grup, en ordre.</summary>
    public static List<Cue> Children(IList<Cue> cues, Cue group) => cues.Where(c => c.ParentId == group.Id).ToList();

    /// <summary>Germanes (mateix grup o llista principal), en ordre.</summary>
    public static List<Cue> Siblings(IList<Cue> cues, Cue cue) => cues.Where(c => c.ParentId == cue.ParentId).ToList();

    /// <summary>Calcula el nivell de cada cue i si queda amagada dins d'un grup plegat.</summary>
    public static void UpdateDepths(IList<Cue> cues)
    {
        var byId = cues.ToDictionary(c => c.Id);
        foreach (var c in cues)
        {
            int depth = 0;
            bool hidden = false;
            var id = c.ParentId;
            for (int guard = 0; id != null && byId.TryGetValue(id.Value, out var p) && guard < 64; guard++)
            {
                depth++;
                if (p is GroupCue { IsCollapsed: true }) hidden = true;
                id = p.ParentId;
            }
            c.Depth = depth;
            c.IsHiddenByGroup = hidden;
        }
    }

    /// <summary>
    /// Ordena la llista perquè cada grup vagi seguit de les seves fills, i treu les referències a grups
    /// que no existeixen o que farien un cercle.
    /// </summary>
    public static List<Cue> Normalize(IEnumerable<Cue> input)
    {
        var list = input.ToList();
        var byId = new Dictionary<Guid, Cue>();
        foreach (var c in list) byId.TryAdd(c.Id, c);
        foreach (var c in list)
        {
            if (c.ParentId is not { } pid || !byId.TryGetValue(pid, out var p) || p is not GroupCue || p == c)
            {
                c.ParentId = null;
                continue;
            }
            // Cercle (A dins de B dins de A): es trenca.
            var seen = new HashSet<Guid> { c.Id };
            for (var q = p; q != null; q = q.ParentId is { } qid && byId.TryGetValue(qid, out var next) ? next : null)
            {
                if (!seen.Add(q.Id)) { c.ParentId = null; break; }
            }
        }
        var children = list.Where(c => c.ParentId != null).GroupBy(c => c.ParentId!.Value).ToDictionary(g => g.Key, g => g.ToList());
        var result = new List<Cue>(list.Count);
        void Emit(Cue c)
        {
            if (result.Contains(c)) return;
            result.Add(c);
            if (children.TryGetValue(c.Id, out var kids)) foreach (var k in kids) Emit(k);
        }
        foreach (var c in list.Where(c => c.ParentId == null)) Emit(c);
        foreach (var c in list) Emit(c); // per si de cas, que no se'n perdi cap
        return result;
    }

    /// <summary>
    /// Espectacles 0.5: els grups apuntaven a cues d'altres llocs de la llista. Les cues passen a ser fills
    /// del grup (just a sota), «totes alhora» passa a línia de temps i «aleatori» a llista barrejada.
    /// Els temps de l'antiga línia de temps se sumen a la pre-espera de cada fill.
    /// </summary>
    public static void MigrateLegacyGroups(List<Cue> cues)
    {
        foreach (var g in cues.OfType<GroupCue>().ToList())
        {
            if (g.Mode == GroupMode.All) g.Mode = GroupMode.Timeline;
            if (g.Mode == GroupMode.Shuffle) { g.Mode = GroupMode.Playlist; g.Shuffle = true; }
            if (g.Targets == null) continue;
            foreach (var id in g.Targets.Distinct())
            {
                var member = cues.FirstOrDefault(c => c.Id == id);
                if (member == null || member == g || member.ParentId != null || IsDescendant(cues, g, member)) continue;
                member.ParentId = g.Id;
                if (g.Offsets != null && g.Offsets.TryGetValue(id, out var offset)) member.PreWait += offset;
            }
            g.Targets = null;
            g.Offsets = null;
        }
    }
}
