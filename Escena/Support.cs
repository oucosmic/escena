using System.Globalization;
using System.Windows;
using System.Windows.Data;
using Escena.Model;

namespace Escena;

public sealed record Option<T>(T Value, string Label);

/// <summary>Entrada fixa d'un desplegable de sortides (té Id i Name com les sortides de veritat).</summary>
public sealed record OutputChoice(Guid Id, string Name);

public static class Options
{
    public static IReadOnlyList<Option<NextAction>> NextActions { get; } = new[]
    {
        new Option<NextAction>(NextAction.None, L.T("Cap (espera el GO)")),
        new Option<NextAction>(NextAction.AutoContinue, L.T("Continua: dispara la següent en començar (+ post-espera)")),
        new Option<NextAction>(NextAction.AutoFollow, L.T("Segueix: dispara la següent en acabar (+ post-espera)")),
    };

    public static IReadOnlyList<Option<FadeCurve>> FadeCurves { get; } = new[]
    {
        new Option<FadeCurve>(FadeCurve.Linear, L.T("Lineal")),
        new Option<FadeCurve>(FadeCurve.Exponential, L.T("Exponencial (passos iguals de dB)")),
        new Option<FadeCurve>(FadeCurve.Quadratic, L.T("Quadràtica (lenta al principi)")),
        new Option<FadeCurve>(FadeCurve.InverseQuadratic, L.T("Quadràtica inversa (ràpida al principi)")),
        new Option<FadeCurve>(FadeCurve.SCurve, L.T("En S (suau als dos extrems)")),
    };

    public static IReadOnlyList<Option<ControlAction>> ControlActions { get; } = new[]
    {
        new Option<ControlAction>(ControlAction.Stop, L.T("Atura")),
        new Option<ControlAction>(ControlAction.Pause, L.T("Pausa")),
        new Option<ControlAction>(ControlAction.Resume, L.T("Reprèn")),
        new Option<ControlAction>(ControlAction.Devamp, L.T("Surt del bucle (continua fins al final)")),
    };

    public static IReadOnlyList<Option<GroupMode>> GroupModes { get; } = new[]
    {
        new Option<GroupMode>(GroupMode.Timeline, L.T("Línia de temps: totes alhora, cadascuna amb la seva pre-espera")),
        new Option<GroupMode>(GroupMode.Playlist, L.T("Llista: una darrere l'altra")),
        new Option<GroupMode>(GroupMode.StartFirstAndEnter, L.T("Entra: dispara la primera i el GO va a la segona")),
        new Option<GroupMode>(GroupMode.StartFirst, L.T("Primera: dispara la primera i el GO salta el grup")),
        new Option<GroupMode>(GroupMode.StartRandom, L.T("Aleatòria: dispara una fill a l'atzar")),
    };

    public static IReadOnlyList<Option<TextPlacement>> TextPlacements { get; } = new[]
    {
        new Option<TextPlacement>(TextPlacement.Top, L.T("A dalt")),
        new Option<TextPlacement>(TextPlacement.Center, L.T("Al mig")),
        new Option<TextPlacement>(TextPlacement.Bottom, L.T("A baix (sobretítols)")),
    };

    public static IReadOnlyList<Option<string>> Languages { get; } = new[]
    {
        new Option<string>("ca", "Català"),
        new Option<string>("en", "English"),
    };

    public static IReadOnlyList<Option<AudioDriver>> Drivers { get; } = new[]
    {
        new Option<AudioDriver>(AudioDriver.WasapiShared, L.T("WASAPI compartit (recomanat)")),
        new Option<AudioDriver>(AudioDriver.WasapiExclusive, L.T("WASAPI exclusiu (latència baixa)")),
        new Option<AudioDriver>(AudioDriver.Asio, L.T("ASIO (targetes professionals)")),
    };
}

/// <summary>Ajustos d'aquest ordinador (no de l'espectacle): p. ex. la sortida d'auriculars.</summary>
public sealed class AppSettings
{
    /// <summary>Dispositiu WASAPI per escoltar cues pels auriculars; buit = sense configurar.</summary>
    public string PreviewDeviceId { get; set; } = "";

    /// <summary>Idioma de la interfície («ca», «en»). Es canvia a Ajustos i s'aplica en tornar a obrir Escena.</summary>
    public string Language { get; set; } = "ca";

    /// <summary>Mentre Escena és obert, l'ordinador no s'adorm ni hiberna i la pantalla no s'apaga.</summary>
    public bool KeepAwake { get; set; } = true;

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Escena", "ajustos.json");

    public static AppSettings Load()
    {
        try { return System.Text.Json.JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new(); }
        catch { return new(); }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, System.Text.Json.JsonSerializer.Serialize(this));
        }
        catch { /* no és crític */ }
    }
}

/// <summary>Noms de tecla per a les tecles directes de les cues («F1», «Ctrl+1», «Num5»…).</summary>
public static class KeyNames
{
    public static string Format(System.Windows.Input.Key key, System.Windows.Input.ModifierKeys mods)
    {
        if (key is System.Windows.Input.Key.LeftCtrl or System.Windows.Input.Key.RightCtrl
            or System.Windows.Input.Key.LeftShift or System.Windows.Input.Key.RightShift
            or System.Windows.Input.Key.LeftAlt or System.Windows.Input.Key.RightAlt
            or System.Windows.Input.Key.LWin or System.Windows.Input.Key.RWin or System.Windows.Input.Key.None)
            return "";
        string name = key switch
        {
            >= System.Windows.Input.Key.D0 and <= System.Windows.Input.Key.D9 => ((int)(key - System.Windows.Input.Key.D0)).ToString(),
            >= System.Windows.Input.Key.NumPad0 and <= System.Windows.Input.Key.NumPad9 => "Num" + (int)(key - System.Windows.Input.Key.NumPad0),
            System.Windows.Input.Key.Return => "Enter",
            _ => key.ToString(),
        };
        string prefix = "";
        if (mods.HasFlag(System.Windows.Input.ModifierKeys.Control)) prefix += "Ctrl+";
        if (mods.HasFlag(System.Windows.Input.ModifierKeys.Alt)) prefix += "Alt+";
        if (mods.HasFlag(System.Windows.Input.ModifierKeys.Shift)) prefix += "Shift+";
        return prefix + name;
    }
}

/// <summary>Visible si el valor no és null (o al revés amb Invert).</summary>
public sealed class NullToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        (value != null) ^ Invert ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>bool → bool invers (per habilitar camps quan una casella està desmarcada).</summary>
public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not true;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not true;
}

/// <summary>Panorama −1…+1 → text «E 30 %», «Centre», «D 50 %».</summary>
public sealed class PanTextConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        double p = value is double d ? d : 0;
        if (Math.Abs(p) < 0.005) return "Centre";
        return (p < 0 ? "E " : "D ") + Math.Round(Math.Abs(p) * 100) + " %";
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Nivell dins dels grups → sagnat a l'esquerra (com al QLab).</summary>
public sealed class DepthIndentConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        new Thickness((value is int d ? d : 0) * 22, 0, 0, 0);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>La cue és un grup?</summary>
public sealed class IsGroupConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is GroupCue;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Igualtat de dos valors (per marcar la cue seleccionada a la botonera).</summary>
public sealed class EqualityMultiConverter : IMultiValueConverter
{
    public object Convert(object?[] values, Type targetType, object? parameter, CultureInfo culture) =>
        values.Length == 2 && ReferenceEquals(values[0], values[1]);

    public object[] ConvertBack(object? value, Type[] targetTypes, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
