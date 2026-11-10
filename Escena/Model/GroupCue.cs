using System.Text.Json.Serialization;

namespace Escena.Model;

/// <summary>
/// Modes de grup (com al QLab). <see cref="All"/> i <see cref="Shuffle"/> només serveixen per obrir
/// espectacles de la versió 0.5 i es converteixen en obrir-los.
/// </summary>
public enum GroupMode { Timeline, Playlist, StartFirstAndEnter, StartFirst, StartRandom, All, Shuffle }

/// <summary>
/// Cue que <b>conté</b> altres cues (les seves fills), que a la llista van just a sota, sagnades.
/// Les fills es marquen amb <see cref="Cue.ParentId"/>; la llista de l'espectacle continua sent plana,
/// amb cada grup seguit de les seves fills (i les fills dels grups de dins, a continuació).
/// </summary>
public sealed class GroupCue : Cue
{
    private GroupMode _mode = GroupMode.Timeline;
    private bool _loopPlaylist, _shuffle, _isCollapsed;

    public GroupMode Mode
    {
        get => _mode;
        set
        {
            if (!Set(ref _mode, value)) return;
            OnPropertyChanged(nameof(IsPlaylist));
            OnPropertyChanged(nameof(TypeName));
            OnPropertyChanged(nameof(ModeHint));
        }
    }

    /// <summary>Llista: en acabar l'última, torna a començar (fins que s'atura el grup).</summary>
    public bool LoopPlaylist { get => _loopPlaylist; set => Set(ref _loopPlaylist, value); }

    /// <summary>Llista: ordre aleatori cada vegada que comença.</summary>
    public bool Shuffle { get => _shuffle; set => Set(ref _shuffle, value); }

    /// <summary>Plegat: les fills no es veuen a la llista (però funcionen igual).</summary>
    public bool IsCollapsed { get => _isCollapsed; set => Set(ref _isCollapsed, value); }

    /// <summary>Només per obrir espectacles 0.5 (grups que apuntaven a cues d'altres llocs de la llista).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<Guid>? Targets { get; set; }

    /// <summary>Només per obrir espectacles 0.5 (temps de la línia de temps antiga).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<Guid, double>? Offsets { get; set; }

    [JsonIgnore] public bool IsPlaylist => Mode == GroupMode.Playlist;

    [JsonIgnore]
    public override string TypeName => Escena.L.T("Grup") + " · " + Mode switch
    {
        GroupMode.Playlist => Escena.L.T("llista"),
        GroupMode.StartFirstAndEnter => Escena.L.T("entra"),
        GroupMode.StartFirst => Escena.L.T("primera"),
        GroupMode.StartRandom => Escena.L.T("aleatòria"),
        _ => Escena.L.T("alhora"),
    };

    [JsonIgnore]
    public string ModeHint => Mode switch
    {
        GroupMode.Playlist => Escena.L.T("Fa sonar les fills una darrere l'altra (es pot repetir i barrejar). El GO passa a la cue de després del grup."),
        GroupMode.StartFirstAndEnter => Escena.L.T("El GO dispara la primera fill i deixa en espera la segona: cada GO següent dispara la fill següent. Serveix per ordenar la llista en seccions."),
        GroupMode.StartFirst => Escena.L.T("El GO dispara només la primera fill; les altres van soles si estan encadenades (Continua / Segueix). El GO passa a la cue de després del grup."),
        GroupMode.StartRandom => Escena.L.T("Cada GO dispara una fill a l'atzar, sense repetir fins que han sonat totes. El GO passa a la cue de després del grup."),
        _ => Escena.L.T("Totes les fills comencen alhora; la pre-espera de cadascuna és el seu temps dins del grup. El GO passa a la cue de després del grup."),
    };
}
