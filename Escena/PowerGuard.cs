using System.Runtime.InteropServices;

namespace Escena;

/// <summary>
/// Evita que Windows adormi o hiberni l'ordinador, apagui la pantalla o posi l'estalvi de pantalla mentre
/// Escena és obert: el projector ha de continuar rebent imatge (negre) encara que no s'hagi disparat cap cue,
/// i un espectacle pot estar molta estona esperant el primer GO.
/// Windows ho desfà sol quan Escena es tanca (o es penja i es tanca).
/// </summary>
internal static class PowerGuard
{
    [Flags]
    private enum ExecutionState : uint
    {
        SystemRequired = 0x00000001,
        DisplayRequired = 0x00000002,
        Continuous = 0x80000000,
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern ExecutionState SetThreadExecutionState(ExecutionState flags);

    public static bool IsActive { get; private set; }

    /// <summary>Activa o desactiva la protecció. Cal cridar-ho des del fil de la interfície (que viu tota l'estona).</summary>
    public static void Apply(bool keepAwake)
    {
        var flags = keepAwake
            ? ExecutionState.Continuous | ExecutionState.SystemRequired | ExecutionState.DisplayRequired
            : ExecutionState.Continuous;
        IsActive = SetThreadExecutionState(flags) != 0 && keepAwake;
    }
}
