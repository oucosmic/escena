using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using Escena.Audio;
using Escena.Model;

namespace Escena;

public partial class SettingsWindow : Window
{
    private readonly ShowController _show;

    public SettingsWindow(ShowController show)
    {
        InitializeComponent();
        _show = show;
        var culture = CultureInfo.CurrentCulture;
        LockoutBox.Text = show.Settings.GoLockoutMs.ToString(culture);
        PanicBox.Text = show.Settings.PanicFadeSeconds.ToString("0.0#", culture);
        AutosaveBox.Text = show.Settings.AutosaveSeconds.ToString(culture);

        var devices = AudioDevices.List(AudioDriver.WasapiShared).Where(d => d.Id != "").ToList();
        devices.Insert(0, new AudioDevice("", "La mateixa sortida que la cue (per defecte)"));
        PreviewCombo.ItemsSource = devices;
        PreviewCombo.SelectedValue = devices.Any(d => d.Id == show.MachineSettings.PreviewDeviceId)
            ? show.MachineSettings.PreviewDeviceId : "";
        PreviewCombo.SelectionChanged += (_, _) => UpdateWarning();
        UpdateWarning();

        LanguageCombo.ItemsSource = Options.Languages;
        LanguageCombo.SelectedValue = Options.Languages.Any(o => o.Value == show.MachineSettings.Language) ? show.MachineSettings.Language : "ca";
    }

    /// <summary>Avisa si els auriculars són el mateix dispositiu que una sortida del públic.</summary>
    private void UpdateWarning()
    {
        string id = PreviewCombo.SelectedValue as string ?? "";
        bool clash = id != "" && _show.Outputs.OfType<AudioOutputConfig>()
            .Any(o => o.Driver != AudioDriver.Asio && (o.DeviceId == id || (o.DeviceId == "" && IsDefaultDevice(id))));
        PreviewWarning.Text = id == ""
            ? "🎧 Escolta sona per on sona la cue. Va bé si l'espectacle ja surt pels teus auriculars; si surt pels altaveus de la sala, tria aquí un dispositiu a part."
            : clash ? "Aquest dispositiu també és una sortida de l'espectacle: el que escoltis sonarà pel mateix lloc." : "";
    }

    private static bool IsDefaultDevice(string id)
    {
        try
        {
            using var en = new NAudio.CoreAudioApi.MMDeviceEnumerator();
            using var d = en.GetDefaultAudioEndpoint(NAudio.CoreAudioApi.DataFlow.Render, NAudio.CoreAudioApi.Role.Multimedia);
            return d.ID == id;
        }
        catch { return false; }
    }

    private static bool TryParse(TextBox box, out double value) =>
        double.TryParse(box.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out value)
        || double.TryParse(box.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (!TryParse(LockoutBox, out var lockout) || !TryParse(PanicBox, out var panic) || !TryParse(AutosaveBox, out var autosave))
        {
            MessageBox.Show(this, "Revisa els números.", "Escena");
            return;
        }
        _show.Settings.GoLockoutMs = (int)Math.Round(lockout);
        _show.Settings.PanicFadeSeconds = panic;
        _show.Settings.AutosaveSeconds = (int)Math.Round(autosave);
        string preview = PreviewCombo.SelectedValue as string ?? "";
        if (preview != _show.MachineSettings.PreviewDeviceId) _show.SetPreviewDevice(preview);
        if (LanguageCombo.SelectedValue is string language && language != _show.MachineSettings.Language)
        {
            _show.MachineSettings.Language = language;
            _show.MachineSettings.Save();
            MessageBox.Show(this, L.T("Idioma de la interfície (s'aplica en tornar a obrir Escena)"), "Escena");
        }
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
