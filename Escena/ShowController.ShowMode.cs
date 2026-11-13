using Escena.Model;

namespace Escena;

/// <summary>
/// Mode funció: l'espectacle queda bloquejat per fer la funció. La interfície amaga l'edició (afegir, esborrar,
/// moure, propietats, sortides) i mostra en gran la cue en espera amb les notes, la de després i les que sonen.
/// Tot el que és de fer la funció continua igual: GO, aturar, pausa, pànic, tecles directes, botonera i triar
/// la cue en espera. No es desa amb l'espectacle: Escena sempre s'obre en mode edició.
/// </summary>
public sealed partial class ShowController
{
    private bool _showMode;
    private string _afterStandby = "";

    public bool IsShowMode
    {
        get => _showMode;
        set
        {
            if (!Set(ref _showMode, value)) return;
            OnPropertyChanged(nameof(IsEditMode));
            OnPropertyChanged(nameof(Title));
            UpdateAfterStandby();
            Status = value
                ? "Mode funció: l'edició està bloquejada. F9 per tornar a editar."
                : "Mode edició.";
        }
    }

    public bool IsEditMode => !IsShowMode;

    /// <summary>La cue que quedarà en espera després del proper GO (per al panell del mode funció).</summary>
    public string AfterStandby { get => _afterStandby; private set => Set(ref _afterStandby, value); }

    private void UpdateAfterStandby()
    {
        if (!_showMode) return;
        AfterStandby = StandbyCue == null ? ""
            : NextStandbyAfter(StandbyCue)?.DisplayName ?? L.T("— final de la llista —");
    }
}
