using Escena.Audio;
using Escena.Model;
using Escena.Video;

namespace Escena;

/// <summary>
/// Vigilància dels aparells durant la funció: sortides d'àudio que cauen (targeta desendollada, error del
/// controlador) i pantalles que canvien (projector desendollat o tornat a endollar).
/// </summary>
public sealed partial class ShowController
{
    private double _healthElapsed;

    /// <summary>Un cop per segon: si una sortida d'àudio ha caigut, la torna a obrir i avisa.</summary>
    private void UpdateHealth(double dt)
    {
        _healthElapsed += dt;
        if (_healthElapsed < 1) return;
        _healthElapsed = 0;

        foreach (var output in _outputManager.Outputs.OfType<AudioOutput>().ToList())
        {
            if (output.TakeFailure() is not { } why) continue;

            // El que sonava per aquesta sortida s'ha perdut: es dona per acabat (si no, quedaria «sonant» per sempre).
            foreach (var media in _playbacks.Keys.ToList())
            {
                bool uses = ReferenceEquals(_outputManager.Resolve(media), output)
                            || media is IAudioSource { Routing.Sends: var sends } && sends.Any(s => s.OutputId == output.Id);
                if (uses) FinishCue(media, natural: false);
            }

            string message;
            try
            {
                output.Open();
                message = $"«{output.Name}» ha caigut ({why}) i s'ha tornat a obrir"
                          + (output.Warning != null ? " pel dispositiu predeterminat." : ".");
            }
            catch (Exception ex)
            {
                message = $"«{output.Name}» ha caigut ({why}) i no s'ha pogut tornar a obrir: {ex.Message}";
            }
            OutputWarnings.Add(message);
            UpdateOutputSummary();
            Status = "Atenció: " + message + " Les cues que hi sonaven s'han aturat.";
        }
    }

    private void WatchDisplays() => Microsoft.Win32.SystemEvents.DisplaySettingsChanged += OnDisplaysChanged;

    private void UnwatchDisplays() => Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= OnDisplaysChanged;

    /// <summary>Windows avisa des d'un altre fil: la feina es fa al de la interfície, quan les pantalles ja s'han assentat.</summary>
    private void OnDisplaysChanged(object? sender, EventArgs e) =>
        _timer.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, RefreshDisplays);

    /// <summary>
    /// Torna a col·locar les finestres de vídeo: si el projector ha desaparegut, la finestra negra no es queda
    /// a pantalla completa tapant Escena; si torna, hi torna. El que s'hi mostrava es conserva.
    /// </summary>
    public void RefreshDisplays()
    {
        var moved = new List<string>();
        foreach (var video in _outputManager.Outputs.OfType<VideoOutput>())
        {
            try
            {
                if (!video.RefreshDisplay()) continue;
                moved.Add(video.Warning ?? video.Description);
            }
            catch (Exception ex) { moved.Add(ex.Message); }
        }
        if (moved.Count == 0) return;
        OutputWarnings.RemoveAll(w => w.Contains("la pantalla escollida no hi és"));
        OutputWarnings.AddRange(_outputManager.Outputs.OfType<VideoOutput>().Where(v => v.Warning != null).Select(v => v.Warning!));
        UpdateOutputSummary();
        Status = "Les pantalles han canviat: " + string.Join(" ", moved);
    }
}
