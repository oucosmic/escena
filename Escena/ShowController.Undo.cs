using System.Collections;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Escena.Model;

namespace Escena;

/// <summary>
/// Desfer i refer de l'edició de cues (afegir, esborrar, moure, agrupar, enganxar i canviar propietats).
/// Funciona amb instantànies de la llista de cues: quan hi ha canvis i passa una estona sense tocar res,
/// l'estat d'abans es guarda com un pas. Així un seguit de canvis seguits (arrossegar un control, enganxar
/// diverses cues, escriure un nom) compta com un sol pas, i no cal que cada ordre sàpiga desfer-se.
/// En restaurar, les cues que ja hi eren es conserven (només se'n canvien les propietats): el que sona no s'atura.
/// Les sortides i el volum general no en formen part.
/// </summary>
public sealed partial class ShowController
{
    private const int MaxUndoSteps = 100;
    private const double UndoQuietSeconds = 0.6;

    private readonly List<string> _undo = new(), _redo = new();
    private string _undoBase = "[]";
    private bool _undoPending, _undoApplying;
    private double _undoQuiet;

    public bool CanUndo => _undo.Count > 0 || _undoPending;
    public bool CanRedo => _redo.Count > 0 && !_undoPending;

    private string SnapshotCues() => JsonSerializer.Serialize(Cues.ToList(), ShowFile.Options);

    /// <summary>Espectacle nou o obert: no hi ha res per desfer.</summary>
    private void ResetUndo()
    {
        _undo.Clear();
        _redo.Clear();
        _undoBase = SnapshotCues();
        _undoPending = false;
        NotifyUndo();
    }

    /// <summary>Hi ha hagut un canvi d'edició: es tancarà en un pas quan passi una estona sense més canvis.</summary>
    private void MarkUndoChange()
    {
        if (_undoApplying) return;
        bool was = _undoPending;
        _undoPending = true;
        _undoQuiet = 0;
        if (!was) NotifyUndo();
    }

    private void UpdateUndo(double dt)
    {
        if (!_undoPending) return;
        _undoQuiet += dt;
        if (_undoQuiet >= UndoQuietSeconds) CommitUndoStep();
    }

    /// <summary>Tanca els canvis pendents en un pas de desfer.</summary>
    private void CommitUndoStep()
    {
        if (!_undoPending) return;
        _undoPending = false;
        string now = SnapshotCues();
        if (now == _undoBase) { NotifyUndo(); return; } // el canvi s'ha desfet sol (p. ex. un valor que torna on era)
        _undo.Add(_undoBase);
        if (_undo.Count > MaxUndoSteps) _undo.RemoveAt(0);
        _redo.Clear();
        _undoBase = now;
        NotifyUndo();
    }

    public bool Undo()
    {
        CommitUndoStep();
        if (_undo.Count == 0)
        {
            Status = "No hi ha res per desfer.";
            return false;
        }
        string target = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        _redo.Add(_undoBase);
        RestoreCues(target);
        Status = "Desfet." + (_undo.Count > 0 ? $" ({_undo.Count} passos més)" : "");
        return true;
    }

    public bool Redo()
    {
        if (_undoPending || _redo.Count == 0)
        {
            Status = "No hi ha res per refer.";
            return false;
        }
        string target = _redo[^1];
        _redo.RemoveAt(_redo.Count - 1);
        _undo.Add(_undoBase);
        RestoreCues(target);
        Status = "Refet.";
        return true;
    }

    private void NotifyUndo()
    {
        OnPropertyChanged(nameof(CanUndo));
        OnPropertyChanged(nameof(CanRedo));
    }

    /// <summary>Deixa la llista de cues com a la instantània, reaprofitant les cues que ja existeixen.</summary>
    private void RestoreCues(string json)
    {
        var wanted = JsonSerializer.Deserialize<List<Cue>>(json, ShowFile.Options) ?? new List<Cue>();
        var existing = Cues.GroupBy(c => c.Id).ToDictionary(g => g.Key, g => g.First());
        Guid? selected = SelectedCue?.Id, standby = StandbyCue?.Id;
        int standbyIndex = StandbyCue != null ? Cues.IndexOf(StandbyCue) : -1;
        bool wasEmpty = Cues.Count == 0;

        _undoApplying = true;
        _moving = true;
        try
        {
            var result = new List<Cue>();
            foreach (var w in wanted)
            {
                if (existing.TryGetValue(w.Id, out var e) && e.GetType() == w.GetType())
                {
                    CopyState(w, e); // la mateixa cue (i la seva reproducció, si sona) amb les propietats d'abans
                    result.Add(e);
                    existing.Remove(w.Id);
                }
                else result.Add(w);
            }
            // Les que desapareixen: s'aturen com quan s'esborren.
            foreach (var gone in existing.Values)
            {
                if (gone.IsActive) StopCue(gone, 0);
                if (_previewCue == gone) StopPreview();
                _pending.RemoveAll(p => p.Source == gone);
                _delayed.RemoveAll(d => d.Cue == gone);
            }
            foreach (var c in Cues) c.PropertyChanged -= OnCuePropertyChanged;
            Cues.Clear();
            foreach (var c in result) Cues.Add(c);
        }
        finally
        {
            _moving = false;
            _undoApplying = false;
        }
        AfterTreeChange();
        VisibleCues.Refresh();
        UpdateDuplicateWarning();
        IsDirty = true;

        _selectedCue = null;
        SelectedCue = Cues.FirstOrDefault(c => c.Id == selected) ?? Cues.FirstOrDefault();
        // En espera: la mateixa cue; si ja no hi és, la del seu lloc. Si no n'hi havia cap (final de la llista),
        // continua sense, tret que la llista fos buida: llavors la primera, com en un espectacle acabat de començar.
        var newStandby = Cues.FirstOrDefault(c => c.Id == standby);
        if (newStandby == null && standby != null && Cues.Count > 0) newStandby = Cues[Math.Clamp(standbyIndex, 0, Cues.Count - 1)];
        if (standby == null && wasEmpty) newStandby = Cues.FirstOrDefault();
        StandbyCue = newStandby;

        _undoBase = json;
        _undoPending = false;
        NotifyUndo();
    }

    /// <summary>
    /// Copia l'estat desat d'una cue (o d'un dels seus objectes) sobre una altra del mateix tipus:
    /// les propietats que es desen al fitxer, les col·leccions de només lectura (bucles, enviaments) i els objectes de dins.
    /// </summary>
    private static void CopyState(object source, object target)
    {
        foreach (var p in source.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (p.GetIndexParameters().Length > 0 || !p.CanRead) continue;
            if (p.GetCustomAttribute<JsonIgnoreAttribute>() is { Condition: JsonIgnoreCondition.Always }) continue;
            object? value = p.GetValue(source);
            if (p.CanWrite && p.SetMethod is { IsPublic: true })
            {
                if (!Equals(p.GetValue(target), value)) p.SetValue(target, value);
            }
            else if (p.GetValue(target) is IList list && value is IList from)
            {
                list.Clear();
                foreach (var item in from) list.Add(item);
            }
            else if (value != null && p.GetValue(target) is { } inner && !p.PropertyType.IsValueType && p.PropertyType != typeof(string))
            {
                CopyState(value, inner);
            }
        }
    }
}
