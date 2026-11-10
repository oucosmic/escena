using System.Globalization;
using System.Reflection;
using Escena.Model;

namespace Escena;

public enum BulkKind { Number, Bool, Choice, Text }

/// <summary>Una opció d'un camp en bloc (valor real i text que es veu).</summary>
public sealed record BulkOption(object? Value, string Label)
{
    public override string ToString() => Label;
}

/// <summary>
/// Una propietat que tenen totes les cues seleccionades. <see cref="Common"/> és el valor compartit,
/// o null si les cues el tenen diferent. <see cref="Apply"/> el posa a totes.
/// </summary>
public sealed class BulkField
{
    private readonly IReadOnlyList<Cue> _cues;
    private readonly string _property;

    internal BulkField(IReadOnlyList<Cue> cues, string property, string label, BulkKind kind, IReadOnlyList<BulkOption>? options)
    {
        _cues = cues;
        _property = property;
        Label = label;
        Kind = kind;
        Options = options ?? Array.Empty<BulkOption>();
    }

    public string Property => _property;
    public string Label { get; }
    public BulkKind Kind { get; }
    public IReadOnlyList<BulkOption> Options { get; }

    private static PropertyInfo Prop(Cue c, string name) => c.GetType().GetProperty(name)!;

    /// <summary>Valor comú, o null si n'hi ha de diferents.</summary>
    public object? Common
    {
        get
        {
            var values = _cues.Select(c => Prop(c, _property).GetValue(c)).ToList();
            var first = values[0];
            return values.All(v => Equals(v, first)) ? first : null;
        }
    }

    public bool IsMixed
    {
        get
        {
            var values = _cues.Select(c => Prop(c, _property).GetValue(c)).ToList();
            return values.Any(v => !Equals(v, values[0]));
        }
    }

    /// <summary>Text per mostrar el valor comú (buit si és diferent entre cues).</summary>
    public string CommonText => Common switch
    {
        null => "",
        double d => d.ToString("0.###", CultureInfo.CurrentCulture),
        var v => v.ToString() ?? "",
    };

    /// <summary>Posa el valor a totes les cues. Per als números accepta text («1,5» o «1.5»).</summary>
    public bool Apply(object? value)
    {
        if (Kind == BulkKind.Number)
        {
            if (value is string s)
            {
                if (!TryParseNumber(s, out double d)) return false;
                value = d;
            }
            if (value is not double) return false;
        }
        foreach (var c in _cues) Prop(c, _property).SetValue(c, value);
        return true;
    }

    public static bool TryParseNumber(string text, out double value) =>
        double.TryParse(text.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
}

/// <summary>Quines propietats es poden editar alhora en diverses cues.</summary>
public static class BulkEdit
{
    /// <summary>Propietats candidates (nom, etiqueta), en l'ordre en què es mostren.</summary>
    private static readonly (string Property, string Label)[] Candidates =
    {
        (nameof(Cue.PreWait), "Pre-espera (s)"),
        (nameof(Cue.PostWait), "Post-espera (s)"),
        (nameof(Cue.NextAction), "Després d'aquesta cue"),
        ("OutputId", "Sortida"),
        ("AudioOutputChoice", "So del vídeo per"),
        ("VolumeDb", "Volum (dB)"),
        ("Pan", "Panorama (−1 esquerra, 0 centre, +1 dreta)"),
        ("Loop", "Bucle"),
        ("FadeIn", "Fosa d'entrada (s)"),
        ("FadeInCurve", "Corba d'entrada"),
        ("FadeOut", "Fosa de sortida (s)"),
        ("FadeOutCurve", "Corba de sortida"),
        ("DisplayTime", "Temps en pantalla (s) — 0 = fins que s'aturi"),
        ("FontSize", "Mida de la lletra"),
        ("Color", "Color del text (#RRGGBB)"),
        ("Placement", "Posició del text"),
        ("BackgroundOpacity", "Franja fosca (0–1)"),
        ("ReplaceTexts", "Treu els altres textos"),
        ("FadeTime", "Temps de la fosa (s)"),
        ("TargetVolumeDb", "Volum final (dB)"),
        ("Curve", "Corba"),
        ("StopTarget", "Atura la cue en acabar"),
        ("UseOwnFade", "Fes servir la fosa de cada cue"),
        ("WaitTime", "Espera (s)"),
        ("Mode", "Mode del grup"),
        ("LoopPlaylist", "Torna a començar en acabar (llista)"),
        ("Shuffle", "Barreja (llista)"),
        (nameof(Cue.Notes), "Notes per a l'operador"),
    };

    /// <param name="audioOutputs">Sortides d'àudio (per a «Sortida» de cues d'àudio).</param>
    /// <param name="videoOutputs">Pantalles (per a «Sortida» de cues visuals).</param>
    /// <param name="videoAudio">Opcions de «So del vídeo per» (amb el predeterminat de Windows).</param>
    public static List<BulkField> FieldsFor(IReadOnlyList<Cue> cues, IEnumerable<OutputConfig> outputs, IEnumerable<object>? videoAudio = null)
    {
        var fields = new List<BulkField>();
        if (cues.Count == 0) return fields;
        var outputList = outputs.ToList();
        foreach (var (name, label) in Candidates)
        {
            var props = cues.Select(c => c.GetType().GetProperty(name)).ToList();
            if (props.Any(p => p == null || !p.CanWrite || p.SetMethod?.IsPublic != true)) continue;
            var type = props[0]!.PropertyType;
            if (props.Any(p => p!.PropertyType != type)) continue;

            IReadOnlyList<BulkOption>? options = null;
            BulkKind kind;
            if (name == "OutputId")
            {
                // Només si totes surten pel mateix tipus de sortida (àudio o pantalla).
                var media = cues.OfType<MediaCue>().ToList();
                if (media.Count != cues.Count) continue;
                bool visual = media[0].IsVisual;
                if (media.Any(m => m.IsVisual != visual)) continue;
                options = outputList.Where(o => visual ? o is VideoOutputConfig : o is AudioOutputConfig)
                    .Select(o => new BulkOption((Guid?)o.Id, o.Name)).ToList();
                kind = BulkKind.Choice;
            }
            else if (name == "AudioOutputChoice")
            {
                options = (videoAudio ?? Array.Empty<object>()).Select(o => o switch
                {
                    OutputChoice ch => new BulkOption(ch.Id, ch.Name),
                    OutputConfig oc => new BulkOption(oc.Id, oc.Name),
                    _ => null,
                }).OfType<BulkOption>().ToList();
                kind = BulkKind.Choice;
            }
            else if (type == typeof(double)) kind = BulkKind.Number;
            else if (type == typeof(bool)) kind = BulkKind.Bool;
            else if (type == typeof(string)) kind = BulkKind.Text;
            else if (type.IsEnum)
            {
                options = EnumOptions(type);
                kind = BulkKind.Choice;
            }
            else continue;
            fields.Add(new BulkField(cues, name, L.T(label), kind, options));
        }
        return fields;
    }

    /// <summary>Opcions d'un enum amb els textos de <see cref="Escena.Options"/> (si n'hi ha) o el nom.</summary>
    private static IReadOnlyList<BulkOption> EnumOptions(Type enumType)
    {
        foreach (var p in typeof(Options).GetProperties(BindingFlags.Public | BindingFlags.Static))
        {
            if (p.GetValue(null) is not System.Collections.IEnumerable list) continue;
            var items = list.Cast<object>().ToList();
            if (items.Count == 0) continue;
            var t = items[0].GetType();
            if (!t.IsGenericType || t.GetGenericArguments()[0] != enumType) continue;
            return items.Select(i => new BulkOption(t.GetProperty("Value")!.GetValue(i), (string)t.GetProperty("Label")!.GetValue(i)!)).ToList();
        }
        return Enum.GetValues(enumType).Cast<object>().Select(v => new BulkOption(v, v.ToString()!)).ToList();
    }

    /// <summary>Numera les cues per ordre de llista: inici, inici+pas… Retorna els números posats.</summary>
    public static List<string> Renumber(IReadOnlyList<Cue> cuesInOrder, double start, double step)
    {
        var numbers = new List<string>();
        for (int i = 0; i < cuesInOrder.Count; i++)
        {
            string n = Math.Round(start + i * step, 3).ToString("0.###", CultureInfo.InvariantCulture);
            cuesInOrder[i].Number = n;
            numbers.Add(n);
        }
        return numbers;
    }
}
