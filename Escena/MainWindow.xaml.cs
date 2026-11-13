using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Escena.Audio;
using Escena.Model;
using Microsoft.Win32;

namespace Escena;

public partial class MainWindow : Window
{
    private const string ShowFilter =
        "Espectacles (*.show)|*.show|Espectacles d'Escena 0.1 (*.escena)|*.escena|Tots els espectacles|*.show;*.escena";

    private readonly ShowController _show = new();

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _show;
        _show.PropertyChanged += (_, e) =>
        {
            if (!_show.IsListLayout) return;
            if (e.PropertyName == nameof(ShowController.SelectedCue) && _show.SelectedCue != null)
                CueGrid.ScrollIntoView(_show.SelectedCue);
            if (e.PropertyName == nameof(ShowController.StandbyCue) && _show.StandbyCue != null)
                CueGrid.ScrollIntoView(_show.StandbyCue); // l'operador sempre veu la cue en espera
        };
        Loaded += OnLoaded;
        // Que Windows no adormi l'ordinador ni apagui la pantalla mentre hi ha un espectacle obert.
        PowerGuard.Apply(_show.MachineSettings.KeepAwake);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Abans de res: si la vegada anterior Escena no es va tancar bé, oferir recuperar-ho.
        var crashed = Recovery.FindCrashedSession();
        _show.NewShow();
        if (crashed != null)
        {
            var dialog = new RecoveryWindow(crashed) { Owner = this };
            dialog.ShowDialog();
            if (dialog.Choice != RecoveryChoice.None)
            {
                try
                {
                    _show.Recover(crashed, dialog.Choice == RecoveryChoice.Continue);
                    // Ja és carregat; si tenia canvis sense desar, aquesta finestra en farà la seva còpia.
                    Recovery.Discard(crashed);
                }
                catch (Exception ex)
                {
                    // Els fitxers de recuperació es queden, per si es pot tornar a provar.
                    MessageBox.Show(this, "No s'ha pogut recuperar l'espectacle:\n\n" + ex.Message, "Escena",
                        MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            else Recovery.Discard(crashed);
        }
        else
        {
            var args = Environment.GetCommandLineArgs();
            if (args.Length > 1 && File.Exists(args[1])) OpenShow(args[1]);
        }
        CueGrid.Focus();
    }

    // ---------------- Cursor d'espera, ajustos, escolta, bucles ----------------

    private void SetStandby_Click(object sender, RoutedEventArgs e)
    {
        if (_show.SelectedCue != null) _show.StandbyCue = _show.SelectedCue;
    }

    private void CueGrid_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (FindAncestor<DataGridRow>(e.OriginalSource as DependencyObject)?.Item is Cue cue)
        {
            _show.StandbyCue = cue;
            e.Handled = true;
        }
    }

    private void Settings_Click(object sender, RoutedEventArgs e) =>
        new SettingsWindow(_show) { Owner = this }.ShowDialog();

    private void Preview_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is Cue cue) _show.TogglePreview(cue);
    }

    private void DevampCue_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is Cue cue) _show.DevampCue(cue);
    }

    private void AddSlice_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not AudioCue cue) return;
        // Per defecte: 4 s a partir de l'inici (o del final de l'últim bucle).
        double start = cue.Slices.Count > 0 ? cue.Slices.Max(s => s.End) : cue.StartAt;
        double limit = cue.EndAt > cue.StartAt ? cue.EndAt : cue.StartAt + cue.Duration;
        if (limit <= 0) limit = start + 4;
        double end = Math.Min(start + 4, limit);
        if (end - start < 0.5) { start = Math.Max(cue.StartAt, end - 4); }
        cue.Slices.Add(new LoopSlice { Start = Math.Round(start, 2), End = Math.Round(end, 2) });
    }

    private void RemoveSlice_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is LoopSlice slice && _show.SelectedCue is AudioCue cue)
            cue.Slices.Remove(slice);
    }

    // ---------------- Matriu, enviaments, tecles ----------------

    private void EditMatrix(IAudioSource cue, Guid? outputId, List<CrossPoint> current, Action<List<CrossPoint>> apply, string title)
    {
        int outs = _show.GetOutputChannels(outputId);
        var window = new MatrixWindow(title, cue.SourceChannels, outs, current, ch => _show.PlayChannelTone(outputId, ch)) { Owner = this };
        if (window.ShowDialog() == true) apply(window.Result);
    }

    private void MainMatrix_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not IAudioSource cue) return;
        string output = _show.Outputs.FirstOrDefault(o => o.Id == cue.OutputId)?.Name ?? "sortida principal";
        EditMatrix(cue, cue.OutputId, cue.Routing.Crosspoints, r => cue.Routing.Crosspoints = r, $"{((Cue)cue).DisplayName} → {output}");
    }

    private void SendMatrix_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not OutputSend send || _show.SelectedCue is not IAudioSource cue) return;
        string output = _show.Outputs.FirstOrDefault(o => o.Id == send.OutputId)?.Name ?? "enviament";
        EditMatrix(cue, send.OutputId, send.Crosspoints, r =>
        {
            send.Crosspoints = r;
            cue.Routing.Sends[cue.Routing.Sends.IndexOf(send)] = send; // marca l'espectacle com a modificat
        }, $"{((Cue)cue).DisplayName} → {output}");
    }

    private void AddSend_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not IAudioSource cue) return;
        var used = cue.Routing.Sends.Select(s => s.OutputId).Append(cue.OutputId).ToHashSet();
        var output = _show.Outputs.OfType<AudioOutputConfig>().FirstOrDefault(o => !used.Contains(o.Id))
                     ?? _show.Outputs.OfType<AudioOutputConfig>().FirstOrDefault();
        cue.Routing.Sends.Add(new OutputSend { OutputId = output?.Id });
        if (_show.Outputs.OfType<AudioOutputConfig>().Count() < 2)
            _show.Status = "Per enviar a una altra sortida, primer crea-la al botó 🔊 (+ Àudio).";
    }

    private void RemoveSend_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is OutputSend send && _show.SelectedCue is IAudioSource cue)
            cue.Routing.Sends.Remove(send);
    }

    private void ClearHotkey_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is Cue cue) cue.Hotkey = "";
    }

    /// <summary>Al camp «Tecla directa»: la tecla que es prem passa a ser la de la cue.</summary>
    private void CaptureHotkey(TextBox box, Key key, ModifierKeys mods)
    {
        if (box.DataContext is not Cue cue) return;
        if (key is Key.Back or Key.Delete)
        {
            cue.Hotkey = "";
            return;
        }
        string name = KeyNames.Format(key, mods);
        if (name.Length == 0) return; // només un modificador: espera la tecla
        if (ShowController.ReservedKeys.Contains(name))
        {
            _show.Status = $"La tecla {name} ja la fa servir Escena; tria'n una altra (F1–F12, lletres, números…).";
            return;
        }
        var other = _show.Cues.FirstOrDefault(c => c != cue && c.Hotkey.Equals(name, StringComparison.OrdinalIgnoreCase));
        cue.Hotkey = name;
        _show.Status = other != null
            ? $"Atenció: {other.DisplayName} també fa servir {name}; es dispararan totes dues."
            : $"{cue.DisplayName}: tecla {name}.";
        FocusCues();
    }

    // ---------------- Eines ----------------

    private void Tools_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { ContextMenu: { } menu } button)
        {
            menu.PlacementTarget = button;
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            // S'obre després que el botó deixi anar el ratolí; si no, el menú es tanca de seguida.
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, () => menu.IsOpen = true);
        }
    }

    private void Check_Click(object sender, RoutedEventArgs e) => new CheckWindow(_show) { Owner = this }.ShowDialog();

    private void StartFrom_Click(object sender, RoutedEventArgs e)
    {
        if (_show.Cues.Count == 0) return;
        var dialog = new StartFromWindow(_show) { Owner = this };
        if (dialog.ShowDialog() == true && dialog.ChosenCue != null)
            _show.StartFrom(dialog.ChosenCue, dialog.Seconds, dialog.IncludeEarlier);
        FocusCues();
    }

    private LogWindow? _logWindow;
    private StageWindow? _stageWindow;

    private void Log_Click(object sender, RoutedEventArgs e)
    {
        if (_logWindow == null)
        {
            _logWindow = new LogWindow(_show) { Owner = this };
            _logWindow.Closed += (_, _) => _logWindow = null;
            _logWindow.Show();
        }
        else _logWindow.Activate();
    }

    private void Stage_Click(object sender, RoutedEventArgs e)
    {
        if (_stageWindow == null)
        {
            _stageWindow = new StageWindow(_show);
            _stageWindow.Closed += (_, _) => _stageWindow = null;
            _stageWindow.Show();
        }
        else _stageWindow.Activate();
    }

    private void Pack_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Carpeta on es copiarà l'espectacle amb tots els seus fitxers",
            InitialDirectory = _show.FilePath != null ? Path.GetDirectoryName(_show.FilePath) : null,
        };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            var (copied, missing, path) = _show.PackShow(dialog.FolderName);
            MessageBox.Show(this,
                $"Espectacle empaquetat a:\n{path}\n\n{copied} fitxers copiats a la carpeta «Fitxers»." +
                (missing > 0 ? $"\n\nAtenció: {missing} fitxers no es troben i no s'han pogut copiar." : "") +
                "\n\nA partir d'ara treballes amb la còpia empaquetada. Pots copiar la carpeta sencera a un altre ordinador.",
                "Escena", MessageBoxButton.OK, missing > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "No s'ha pogut empaquetar:\n\n" + ex.Message, "Escena", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>Exporta l'espectacle cap al QLab 5: carpeta amb els fitxers, un AppleScript que crea les cues i un informe.</summary>
    private void ExportQLab_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = L.T("Carpeta on es crearà l'exportació per al QLab"),
            InitialDirectory = _show.FilePath != null ? Path.GetDirectoryName(_show.FilePath) : null,
        };
        if (dialog.ShowDialog(this) != true) return;
        string showName = _show.FilePath != null ? Path.GetFileNameWithoutExtension(_show.FilePath) : "Espectacle";
        string dest = Path.Combine(dialog.FolderName, showName + " (QLab)");
        try
        {
            var r = Escena.QLab.QLabExporter.Export(_show.Cues.ToList(), dest, showName);
            int warnings = r.Warnings.Distinct().Count();
            var answer = MessageBox.Show(this,
                L.T("Exportació per al QLab creada a:") + $"\n{r.Folder}\n\n" +
                $"{r.CueCount} cues · {r.Copied} " + L.T("fitxers copiats a «media»") +
                (r.Missing > 0 ? $"\n{r.Missing} " + L.T("fitxers no es troben.") : "") +
                (warnings > 0 ? $"\n{warnings} " + L.T("avisos de coses que cal fer a mà (vegeu l'informe).") : "") +
                "\n\n" + L.T("Copia la carpeta al Mac, obre el QLab 5 amb un espai de treball i executa «Importa a QLab.applescript».") +
                "\n\n" + L.T("Vols obrir la carpeta?"),
                "Escena", MessageBoxButton.YesNo, r.Missing > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information);
            if (answer == MessageBoxResult.Yes)
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(r.Folder) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, L.T("No s'ha pogut exportar:") + "\n\n" + ex.Message, "Escena", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>Un fitxer de text: cada bloc separat per una línia en blanc és un sobretítol (una cue de text).</summary>
    private void ImportSurtitles_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "Text (*.txt)|*.txt|Tots els fitxers|*.*" };
        if (dialog.ShowDialog(this) != true) return;
        var blocks = System.Text.RegularExpressions.Regex.Split(File.ReadAllText(dialog.FileName).Replace("\r\n", "\n"), @"\n\s*\n")
            .Select(b => b.Trim()).Where(b => b.Length > 0).ToList();
        int n = 0;
        foreach (var block in blocks)
        {
            string first = block.Split('\n')[0];
            _show.InsertCue(new TextCue
            {
                Name = "Sobretítol: " + (first.Length > 40 ? first[..40] + "…" : first),
                Text = block, Placement = TextPlacement.Bottom, FontSize = 54, BackgroundOpacity = 0.5,
                FadeIn = 0.2, FadeOut = 0.2, ReplaceTexts = true,
            });
            n++;
        }
        _show.Status = $"S'han creat {n} cues de sobretítols. Cada GO en mostra un i treu l'anterior.";
    }

    // ---------------- Fitxers ----------------

    private bool ConfirmDiscard()
    {
        if (!_show.IsDirty) return true;
        var answer = MessageBox.Show(this, "Hi ha canvis sense desar. Els vols desar?", "Escena",
            MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        return answer switch
        {
            MessageBoxResult.Yes => SaveShow(),
            MessageBoxResult.No => true,
            _ => false,
        };
    }

    private void OpenShow(string path)
    {
        try { _show.Load(path); }
        catch (Exception ex)
        {
            MessageBox.Show(this, "No s'ha pogut obrir l'espectacle:\n\n" + ex.Message, "Escena",
                MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        if (_show.OpenedNewerVersion)
            MessageBox.Show(this,
                "Aquest espectacle s'ha fet amb una versió d'Escena més nova que aquesta.\n\n" +
                "El pots fer servir, però si el deses des d'aquí es perdrà tot allò que aquesta versió no coneix. " +
                "Per conservar-ho tot, actualitza Escena o desa'l amb un altre nom.",
                "Escena", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private bool SaveShow(bool saveAs = false)
    {
        string? path = _show.FilePath;
        if (saveAs || path == null || !File.Exists(path) && !Directory.Exists(Path.GetDirectoryName(path)))
        {
            var dialog = new SaveFileDialog
            {
                Filter = "Espectacles (*.show)|*.show",
                DefaultExt = ShowFile.Extension,
                FileName = path != null ? Path.GetFileName(path) : "Espectacle" + ShowFile.Extension,
            };
            if (dialog.ShowDialog(this) != true) return false;
            path = dialog.FileName;
        }
        try
        {
            _show.Save(path);
            return true;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "No s'ha pogut desar:\n\n" + ex.Message, "Escena",
                MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
    }

    private void New_Click(object sender, RoutedEventArgs e)
    {
        if (ConfirmDiscard()) _show.NewShow();
    }

    private void Open_Click(object sender, RoutedEventArgs e)
    {
        if (!ConfirmDiscard()) return;
        var dialog = new OpenFileDialog { Filter = ShowFilter, FilterIndex = 3 };
        if (dialog.ShowDialog(this) == true) OpenShow(dialog.FileName);
    }

    private void Save_Click(object sender, RoutedEventArgs e) => SaveShow();

    private void SaveAs_Click(object sender, RoutedEventArgs e) => SaveShow(saveAs: true);

    private void Outputs_Click(object sender, RoutedEventArgs e)
    {
        var window = new OutputsWindow(_show.Outputs, _show.FindVideoOutput) { Owner = this };
        if (window.ShowDialog() != true || !_show.OutputsDiffer(window.Result)) return;
        // Aplicar sortides noves les torna a obrir totes: el que soni es talla.
        if (_show.ActiveCues.Count > 0 && MessageBox.Show(this,
                $"Hi ha {_show.ActiveCues.Count} cue(s) en marxa. Per aplicar els canvis de les sortides s'han d'aturar totes de cop.\n\nVols aplicar-los ara?",
                "Escena", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK)
        {
            _show.Status = "No s'han aplicat els canvis de les sortides.";
            return;
        }
        _show.ApplyOutputs(window.Result);
    }

    // ---------------- Vista ----------------

    private void ListLayout_Click(object sender, RoutedEventArgs e)
    {
        _show.Layout = LayoutMode.List;
        CueGrid.Focus();
    }

    private void CartLayout_Click(object sender, RoutedEventArgs e) => _show.Layout = LayoutMode.Cart;

    private void Tile_LeftClick(object sender, MouseButtonEventArgs e)
    {
        if (_dragged)
        {
            _dragged = false; // era un arrossegament, no un clic
            return;
        }
        if ((sender as FrameworkElement)?.DataContext is not Cue cue) return;
        Keyboard.ClearFocus();
        _show.ToggleCue(cue);
    }

    // ---------------- Reordenar arrossegant ----------------

    private const string CueDragFormat = "Escena.Cue";
    private const string CueListDragFormat = "Escena.CueList";
    private static readonly Brush DropMarkerBrush = new SolidColorBrush(Color.FromRgb(0x5B, 0x9B, 0xF0));
    private Point _dragStart;
    private Cue? _dragCandidate;
    private bool _dragged;
    private FrameworkElement? _dropMarker;
    private DropPosition _dropMarkerPosition;

    private Cue? _clickKeepsSelection;

    private void CueGrid_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        _dragged = false;
        _dragStart = e.GetPosition(this);
        _dragCandidate = FindAncestor<DataGridRow>(e.OriginalSource as DependencyObject)?.Item as Cue;
        _clickKeepsSelection = null;
        // Clic sobre una de diverses cues seleccionades: la selecció es conserva per poder-les arrossegar totes.
        // Si al final no s'arrossega, en deixar anar el botó queda seleccionada només aquesta (com sempre).
        if (_dragCandidate != null && e.ClickCount == 1 && Keyboard.Modifiers == ModifierKeys.None
            && CueGrid.SelectedItems.Count > 1 && CueGrid.SelectedItems.Contains(_dragCandidate)
            && e.OriginalSource is not TextBlock { Text: "▾" or "▸" })
        {
            _clickKeepsSelection = _dragCandidate;
            CueGrid.Focus();
            e.Handled = true;
        }
    }

    private void CueGrid_PreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        var cue = _clickKeepsSelection;
        _clickKeepsSelection = null;
        if (cue == null || _dragged || !_show.Cues.Contains(cue)) return;
        CueGrid.SelectedItems.Clear();
        CueGrid.SelectedItem = cue;
    }

    private void CueGrid_PreviewMouseMove(object sender, MouseEventArgs e) => TryStartDrag(CueGrid, e);

    private void Tile_MouseDown(object sender, MouseButtonEventArgs e)
    {
        _dragged = false;
        _dragStart = e.GetPosition(this);
        _dragCandidate = (sender as FrameworkElement)?.DataContext as Cue;
    }

    private void Tile_MouseMove(object sender, MouseEventArgs e) => TryStartDrag((DependencyObject)sender, e);

    private void TryStartDrag(DependencyObject source, MouseEventArgs e)
    {
        if (_dragCandidate == null || e.LeftButton != MouseButtonState.Pressed) return;
        var delta = e.GetPosition(this) - _dragStart;
        if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance * 2 &&
            Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance * 2) return;

        var cue = _dragCandidate;
        _dragCandidate = null;
        _dragged = true;
        var data = new DataObject(CueDragFormat, cue);
        // A la llista, si la cue arrossegada és una de diverses seleccionades, hi van totes.
        if (ReferenceEquals(source, CueGrid) && CueGrid.SelectedItems.Count > 1 && CueGrid.SelectedItems.Contains(cue))
            data.SetData(CueListDragFormat, CueGrid.SelectedItems.OfType<Cue>().ToList());
        try { DragDrop.DoDragDrop(source, data, DragDropEffects.Move); }
        finally { ClearDropMarker(); }
    }

    /// <summary>
    /// On cauria la cue arrossegada: davant o darrere d'una altra (al seu nivell) o, al mig d'un grup, a dins.
    /// Sense cue a sota, al final de la llista.
    /// </summary>
    private (Cue? Anchor, DropPosition Position, FrameworkElement? Marker) GetDropTarget(DragEventArgs e)
    {
        var source = e.OriginalSource as DependencyObject;
        if (FindAncestor<DataGridRow>(source) is { Item: Cue rowCue } row)
        {
            double y = e.GetPosition(row).Y / Math.Max(1, row.ActualHeight);
            var pos = rowCue is GroupCue && y > 0.25 && y < 0.75 ? DropPosition.Into
                : y >= 0.5 ? DropPosition.After : DropPosition.Before;
            return (rowCue, pos, row);
        }
        for (var d = source; d != null; d = TreeParent(d))
        {
            if (d is Border { Name: "Tile", DataContext: Cue cue } tile)
            {
                double x = e.GetPosition(tile).X / Math.Max(1, tile.ActualWidth);
                var pos = cue is GroupCue && x > 0.3 && x < 0.7 ? DropPosition.Into
                    : x >= 0.5 ? DropPosition.After : DropPosition.Before;
                return (cue, pos, tile);
            }
        }
        return (null, DropPosition.After, null);
    }

    private void ShowDropMarker(FrameworkElement? element, DropPosition position)
    {
        if (element == _dropMarker && position == _dropMarkerPosition) return;
        ClearDropMarker();
        _dropMarker = element;
        _dropMarkerPosition = position;
        switch (element)
        {
            case DataGridRow row:
                row.BorderBrush = DropMarkerBrush;
                row.BorderThickness = position switch
                {
                    DropPosition.Into => new Thickness(3),
                    DropPosition.After => new Thickness(0, 0, 0, 3),
                    _ => new Thickness(0, 3, 0, 0),
                };
                break;
            case Border tile:
                tile.BorderBrush = DropMarkerBrush;
                tile.BorderThickness = position switch
                {
                    DropPosition.Into => new Thickness(5),
                    DropPosition.After => new Thickness(2, 2, 7, 2),
                    _ => new Thickness(7, 2, 2, 2),
                };
                break;
        }
    }

    private void ClearDropMarker()
    {
        switch (_dropMarker)
        {
            case DataGridRow row:
                row.ClearValue(Control.BorderBrushProperty);
                row.ClearValue(Control.BorderThicknessProperty);
                break;
            case Border tile:
                tile.ClearValue(Border.BorderBrushProperty);
                tile.ClearValue(Border.BorderThicknessProperty);
                break;
        }
        _dropMarker = null;
    }

    private void Cues_DragLeave(object sender, DragEventArgs e) => ClearDropMarker();

    private static DependencyObject? TreeParent(DependencyObject d) =>
        d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);

    private static T? FindAncestor<T>(DependencyObject? d) where T : DependencyObject
    {
        for (; d != null; d = TreeParent(d))
            if (d is T found) return found;
        return null;
    }

    private void Tile_RightClick(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is Cue cue) _show.SelectedCue = cue;
        e.Handled = true;
    }

    // ---------------- Edició de cues ----------------

    /// <summary>Crea la cue adequada per a cada fitxer segons l'extensió (àudio, vídeo o imatge).</summary>
    private void AddMediaFiles(IEnumerable<string> files)
    {
        foreach (var file in files)
        {
            MediaCue? cue = AudioReaders.IsSupported(file) ? new AudioCue()
                : Video.VlcCore.IsVideo(file) ? new VideoCue()
                : Video.VlcCore.IsImage(file) ? new ImageCue()
                : null;
            if (cue == null) continue;
            cue.Name = Path.GetFileNameWithoutExtension(file);
            cue.FilePath = file;
            _show.InsertCue(cue);
        }
    }

    private void AddFromDialog(string filter)
    {
        var dialog = new OpenFileDialog { Filter = filter, Multiselect = true };
        if (dialog.ShowDialog(this) == true) AddMediaFiles(dialog.FileNames);
    }

    private void AddAudio_Click(object sender, RoutedEventArgs e) => AddFromDialog(AudioReaders.DialogFilter);

    private void AddVideo_Click(object sender, RoutedEventArgs e) => AddFromDialog(Video.VlcCore.VideoFilter);

    private void AddImage_Click(object sender, RoutedEventArgs e) => AddFromDialog(Video.VlcCore.ImageFilter);

    private static string FilterFor(MediaCue cue) => cue switch
    {
        VideoCue => Video.VlcCore.VideoFilter,
        ImageCue => Video.VlcCore.ImageFilter,
        _ => AudioReaders.DialogFilter,
    };

    /// <summary>L'última cue de fitxer fins a la posició d'inserció (destí per defecte de foses i controls).</summary>
    private MediaCue? LastMediaCue()
    {
        int index = _show.SelectedCue != null ? _show.Cues.IndexOf(_show.SelectedCue) : _show.Cues.Count - 1;
        return _show.Cues.Take(index + 1).OfType<MediaCue>().LastOrDefault();
    }

    private void AddFade_Click(object sender, RoutedEventArgs e)
    {
        var target = LastMediaCue();
        _show.InsertCue(new FadeCue
        {
            Name = target != null ? $"Fosa de «{target.Name}»" : "Fosa",
            TargetId = target?.Id,
        });
    }

    private const string DefaultControlName = "Atura-ho tot";

    /// <summary>
    /// Atura/Pausa nova: si hi ha una cue seleccionada, actua sobre aquella (en una fosa, sobre la cue de la fosa);
    /// sense cap cue seleccionada, sobre totes.
    /// </summary>
    private void AddControl_Click(object sender, RoutedEventArgs e)
    {
        var target = _show.SelectedCue switch
        {
            null or ControlCue => null,
            FadeCue f when f.TargetId is Guid t => _show.Cues.FirstOrDefault(c => c.Id == t),
            FadeCue => null,
            var c => c,
        };
        var control = target == null
            ? new ControlCue { Name = DefaultControlName, AllCues = true }
            : new ControlCue { AllCues = false, TargetId = target.Id };
        if (target != null) control.Name = $"{control.TypeName} «{target.Name}»";
        _show.InsertCue(control);
    }

    /// <summary>En triar una cue destí, la cue de control passa a actuar només sobre aquella.</summary>
    private void ControlTarget_DropDownClosed(object? sender, EventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ControlCue control || control.TargetId == null) return;
        control.AllCues = false;
        if (control.Name == DefaultControlName && _show.Cues.FirstOrDefault(c => c.Id == control.TargetId) is { } target)
            control.Name = $"{control.TypeName} «{target.Name}»";
    }

    /// <summary>En marcar «Totes», es treu la cue destí perquè no faci pensar que només actua sobre aquella.</summary>
    private void ControlAll_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ControlCue control || !control.AllCues) return;
        control.TargetId = null;
        if (control.Name.StartsWith(control.TypeName + " «")) control.Name = DefaultControlName;
    }

    private void AddText_Click(object sender, RoutedEventArgs e) =>
        _show.InsertCue(new TextCue { Name = "Text", Text = "Text", FadeIn = 0.5, FadeOut = 0.5 });

    private void AddMic_Click(object sender, RoutedEventArgs e) =>
        _show.InsertCue(new MicCue { Name = "Micròfon", FadeIn = 0.3, FadeOut = 1 });

    private void AddWait_Click(object sender, RoutedEventArgs e) =>
        _show.InsertCue(new WaitCue { Name = "Espera" });

    /// <summary>Amb diverses cues seleccionades (Ctrl/Maj + clic), les posa dins d'un grup nou; si no, crea un grup buit.</summary>
    private void AddGroup_Click(object sender, RoutedEventArgs e)
    {
        var selected = CueGrid.SelectedItems.OfType<Cue>().ToList();
        if (_show.IsListLayout && selected.Count > 1) _show.GroupCues(selected);
        else
        {
            _show.InsertCue(new GroupCue { Name = "Grup" });
            _show.Status = "Grup nou. Arrossega-hi cues a sobre per posar-les-hi (o selecciona'n unes quantes i prem «+ Grup»).";
        }
        FocusCues();
    }

    private void Ungroup_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is GroupCue group) _show.Ungroup(group);
    }

    private void RemoveFromGroup_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is Cue cue) _show.RemoveFromGroup(cue);
    }

    private void GroupToggle_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not GroupCue group) return;
        group.IsCollapsed = !group.IsCollapsed;
        _dragCandidate = null;
        e.Handled = true;
    }

    // ---------------- Copiar i enganxar ----------------

    /// <summary>Cues triades: les marcades a la llista o, a la botonera, la seleccionada.</summary>
    private List<Cue> SelectedCues()
    {
        var list = _show.IsListLayout ? CueGrid.SelectedItems.OfType<Cue>().ToList() : new List<Cue>();
        if (list.Count == 0 && _show.SelectedCue != null) list.Add(_show.SelectedCue);
        return list;
    }

    private bool CopySelection()
    {
        var cues = SelectedCues();
        if (cues.Count == 0) return false;
        string json = _show.SerializeCues(cues);
        var data = new DataObject();
        data.SetData(ShowController.ClipboardFormat, json);
        data.SetText(json);
        try { Clipboard.SetDataObject(data, copy: true); }
        catch (Exception ex)
        {
            _show.Status = "No s'ha pogut copiar: " + ex.Message;
            return false;
        }
        int n = _show.CopyBlock(cues).Count;
        _show.Status = n == 1 ? "S'ha copiat 1 cue." : $"S'han copiat {n} cues.";
        return true;
    }

    private void CopyCues_Click(object sender, RoutedEventArgs e) => CopySelection();

    private void CutCues_Click(object sender, RoutedEventArgs e)
    {
        var cues = SelectedCues();
        if (!CopySelection()) return;
        int n = _show.RemoveCues(cues);
        _show.Status = n == 1 ? "S'ha retallat 1 cue." : $"S'han retallat {n} cues.";
    }

    private void PasteCues_Click(object sender, RoutedEventArgs e)
    {
        string? json = null;
        try
        {
            if (Clipboard.ContainsData(ShowController.ClipboardFormat)) json = Clipboard.GetData(ShowController.ClipboardFormat) as string;
            else if (Clipboard.ContainsText() && Clipboard.GetText().TrimStart().StartsWith("[")) json = Clipboard.GetText();
        }
        catch (Exception ex)
        {
            _show.Status = "No s'ha pogut llegir el porta-retalls: " + ex.Message;
            return;
        }
        if (json == null)
        {
            _show.Status = "No hi ha cues copiades per enganxar.";
            return;
        }
        // Amb diverses cues marcades, s'enganxa després de l'última de la llista (com al QLab).
        var marked = SelectedCues();
        if (marked.Count > 1) _show.SelectedCue = marked.OrderBy(c => _show.Cues.IndexOf(c)).Last();
        SelectPasted(_show.PasteCues(json));
    }

    private void DuplicateCues_Click(object sender, RoutedEventArgs e) => SelectPasted(_show.DuplicateCues(SelectedCues()));

    /// <summary>Deixa marcades a la llista les cues que s'acaben d'enganxar.</summary>
    private void SelectPasted(List<Cue> roots)
    {
        if (roots.Count == 0 || !_show.IsListLayout) return;
        CueGrid.SelectedItems.Clear();
        foreach (var c in roots) if (!c.IsHiddenByGroup) CueGrid.SelectedItems.Add(c);
        CueGrid.ScrollIntoView(roots[^1]);
        FocusCues();
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        var selected = SelectedCues();
        if (selected.Count > 1)
        {
            int total = _show.CopyBlock(selected).Count;
            if (MessageBox.Show(this, $"S'esborraran {total} cues (incloses les que hi ha dins dels grups triats).",
                    "Escena", MessageBoxButton.OKCancel, MessageBoxImage.Warning) == MessageBoxResult.OK)
                _show.RemoveCues(selected);
            return;
        }
        var cue = _show.SelectedCue;
        if (cue == null) return;
        int inside = ShowTree.Subtree(_show.Cues, cue).Count - 1;
        if (inside > 0 && MessageBox.Show(this,
                $"«{cue.DisplayName}» conté {inside} cue(s). S'esborraran totes.\n\nSi només vols treure el grup i deixar-ne les cues, fes servir «Desagrupa».",
                "Escena", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK)
            return;
        _show.RemoveCue(cue);
    }

    private void MoveUp_Click(object sender, RoutedEventArgs e)
    {
        MoveSelection(-1);
    }

    private void MoveDown_Click(object sender, RoutedEventArgs e)
    {
        MoveSelection(+1);
    }

    /// <summary>Mou la cue seleccionada o, si n'hi ha diverses, totes alhora.</summary>
    private void MoveSelection(int delta)
    {
        var selected = SelectedCues();
        if (selected.Count > 1) SelectPasted(_show.MoveCues(selected, delta));
        else if (_show.SelectedCue != null) _show.MoveCue(_show.SelectedCue, delta);
    }

    private void BrowseFile_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not MediaCue cue) return;
        var dialog = new OpenFileDialog { Filter = FilterFor(cue) };
        if (!string.IsNullOrEmpty(cue.FilePath))
            dialog.InitialDirectory = Path.GetDirectoryName(cue.FilePath);
        if (dialog.ShowDialog(this) != true) return;
        bool rename = string.IsNullOrWhiteSpace(cue.Name) ||
                      cue.Name == Path.GetFileNameWithoutExtension(cue.FilePath);
        cue.FilePath = dialog.FileName;
        if (rename) cue.Name = Path.GetFileNameWithoutExtension(dialog.FileName);
    }

    private void PanSlider_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is PlayableCue cue) cue.Pan = 0;
        else if ((sender as FrameworkElement)?.DataContext is MicCue mic) mic.Pan = 0;
    }

    private void MasterSlider_DoubleClick(object sender, MouseButtonEventArgs e) => _show.MasterVolumeDb = 0;

    private void Cues_DragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(CueDragFormat))
        {
            var (_, position, marker) = GetDropTarget(e);
            ShowDropMarker(marker, position);
            e.Effects = DragDropEffects.Move;
        }
        else
        {
            e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        }
        e.Handled = true;
    }

    private void Cues_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(CueDragFormat) is Cue dragged)
        {
            var (anchor, dropAt, _) = GetDropTarget(e);
            ClearDropMarker();
            if (e.Data.GetData(CueListDragFormat) is List<Cue> { Count: > 1 } many)
                SelectPasted(_show.MoveCuesTo(many, anchor, dropAt));
            else
                _show.MoveCueTo(dragged, anchor, dropAt);
            e.Handled = true;
            return;
        }
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] files) return;
        var show = files.FirstOrDefault(ShowFile.IsShowFile);
        if (show != null)
        {
            if (ConfirmDiscard()) OpenShow(show);
            return;
        }
        AddMediaFiles(files.OrderBy(f => f));
    }

    // ---------------- Reproducció ----------------

    private void Go_Click(object sender, RoutedEventArgs e) => _show.Go();

    private void Pause_Click(object sender, RoutedEventArgs e) => _show.TogglePauseAll();

    private void StopAll_Click(object sender, RoutedEventArgs e) => _show.StopAllPressed();

    private void Panic_Click(object sender, RoutedEventArgs e) => _show.Panic();

    private void StopCue_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is Cue cue) _show.StopCue(cue);
    }

    private void PauseCue_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not Cue cue) return;
        if (cue.IsPaused) _show.ResumeCue(cue); else _show.PauseCue(cue);
    }

    // ---------------- Teclat ----------------

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e) => HandleShortcut(e);

    /// <summary>Dreceres de teclat. També les hi passen les finestres de vídeo.</summary>
    public void HandleShortcut(KeyEventArgs e)
    {
        object sender = this;
        var mods = Keyboard.Modifiers;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;

        // Camp «Tecla directa»: captura la tecla per a la cue.
        if (Keyboard.FocusedElement is TextBox { Tag: "hotkey" } hotkeyBox)
        {
            if (key is Key.Tab or Key.Escape) { if (key == Key.Escape) FocusCues(); e.Handled = key == Key.Escape; return; }
            CaptureHotkey(hotkeyBox, key, mods);
            e.Handled = true;
            return;
        }

        if (mods == ModifierKeys.Control)
        {
            // Mantenir Ctrl+V o Ctrl+D no ha d'enganxar ni duplicar sense parar (dins d'un camp de text, sí).
            if (e.IsRepeat && key is not (Key.Up or Key.Down) && Keyboard.FocusedElement is not TextBox)
            {
                e.Handled = true;
                return;
            }
            bool handled = true;
            switch (key)
            {
                case Key.N: New_Click(sender, e); break;
                case Key.O: Open_Click(sender, e); break;
                case Key.S: SaveShow(); break;
                case Key.Up: MoveUp_Click(sender, e); break;
                case Key.Down: MoveDown_Click(sender, e); break;
                case Key.G: AddGroup_Click(sender, e); break;
                // Copiar i enganxar cues, si no s'està escrivint en un camp (allà són per al text).
                case Key.C when Keyboard.FocusedElement is not TextBox: CopyCues_Click(sender, e); break;
                case Key.X when Keyboard.FocusedElement is not TextBox: CutCues_Click(sender, e); break;
                case Key.V when Keyboard.FocusedElement is not TextBox: PasteCues_Click(sender, e); break;
                case Key.D when Keyboard.FocusedElement is not TextBox: DuplicateCues_Click(sender, e); break;
                // Desfer i refer l'edició de cues (dins d'un camp de text, desfan el que s'hi escriu).
                case Key.Z when Keyboard.FocusedElement is not TextBox: Undo_Click(sender, e); break;
                case Key.Y when Keyboard.FocusedElement is not TextBox: Redo_Click(sender, e); break;
                default: handled = false; break;
            }
            if (handled)
            {
                e.Handled = true;
                return;
            }
        }
        if (mods == (ModifierKeys.Control | ModifierKeys.Shift) && key == Key.S)
        {
            SaveShow(saveAs: true);
            e.Handled = true;
            return;
        }
        if (mods == (ModifierKeys.Control | ModifierKeys.Shift) && key == Key.Z && Keyboard.FocusedElement is not TextBox)
        {
            Redo_Click(sender, e);
            e.Handled = true;
            return;
        }
        // El pànic funciona sempre, encara que s'estigui escrivint.
        if (mods == ModifierKeys.Shift && key == Key.Escape)
        {
            // Mantenir la tecla no compta com un segon toc (que tallaria de cop).
            if (!e.IsRepeat) _show.Panic();
            e.Handled = true;
            return;
        }

        // Mentre s'escriu en un camp, les tecles són per al camp (Enter confirma, Esc surt).
        if (Keyboard.FocusedElement is TextBox textBox)
        {
            if (key == Key.Enter && !textBox.AcceptsReturn)
            {
                textBox.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
                if (textBox.Tag is "bulk") FocusCues(); // camp en bloc: en sortir s'aplica a totes
                e.Handled = true;
            }
            else if (key == Key.Escape)
            {
                textBox.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
                FocusCues();
                e.Handled = true;
            }
            return;
        }
        if (Keyboard.FocusedElement is ComboBoxItem || (Keyboard.FocusedElement is ComboBox cb && cb.IsDropDownOpen))
            return;

        // Tecla mantinguda: Windows la repeteix sola. Només les fletxes es repeteixen; la resta (GO, Esc, pausa,
        // Supr, tecles directes…) compta un sol cop, perquè aguantar Esc no talli la fosa ni Espai dispari diverses cues.
        if (e.IsRepeat && key is not (Key.Up or Key.Down))
        {
            e.Handled = mods != ModifierKeys.Control;
            return;
        }

        // Tecles directes de les cues (mai no coincideixen amb les d'Escena: estan reservades).
        string keyName = KeyNames.Format(key, mods);
        if (keyName.Length > 0 && !ShowController.ReservedKeys.Contains(keyName) && _show.FireHotkey(keyName))
        {
            e.Handled = true;
            return;
        }
        if (mods == ModifierKeys.Control) return;

        // Maj+↑↓: canvia la cue seleccionada (la que s'edita), sense tocar la que està en espera.
        if (mods == ModifierKeys.Shift && key is Key.Up or Key.Down)
        {
            SelectRelative(key == Key.Up ? -1 : 1);
            e.Handled = true;
            return;
        }

        switch (key)
        {
            case Key.Space: _show.Go(); break;
            case Key.Escape: _show.StopAllPressed(); break;
            case Key.P: _show.TogglePauseAll(); break;
            case Key.E:
                if (_show.SelectedCue != null) _show.TogglePreview(_show.SelectedCue);
                break;
            case Key.Enter:
                if (_show.SelectedCue != null) _show.StandbyCue = _show.SelectedCue;
                break;
            case Key.Delete: Delete_Click(sender, e); break;
            case Key.F5: ListLayout_Click(sender, e); break;
            case Key.F6: CartLayout_Click(sender, e); break;
            case Key.Up:
            case Key.Down:
                // ↑↓ mouen la cue en espera (la del proper GO).
                _show.MoveStandby(key == Key.Up ? -1 : 1);
                break;
            default:
                return;
        }
        e.Handled = true;
    }

    private void FocusCues()
    {
        if (_show.IsListLayout) CueGrid.Focus();
        else Keyboard.ClearFocus();
    }

    private void SelectRelative(int delta)
    {
        // Només les cues que es veuen: les de dins d'un grup plegat se salten.
        var visible = _show.Cues.Where(c => !c.IsHiddenByGroup).ToList();
        if (visible.Count == 0) return;
        // Al final de la llista (sense selecció), ↑ torna a l'última cue.
        int index = _show.SelectedCue != null && visible.IndexOf(_show.SelectedCue) is var i and >= 0 ? i + delta
            : delta < 0 ? visible.Count - 1 : 0;
        _show.SelectedCue = visible[Math.Clamp(index, 0, visible.Count - 1)];
    }

    private void Undo_Click(object sender, RoutedEventArgs e)
    {
        if (_show.Undo()) QueueBulkRefresh();
    }

    private void Redo_Click(object sender, RoutedEventArgs e)
    {
        if (_show.Redo()) QueueBulkRefresh();
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        // A mitja funció, tancar per error (Alt+F4, la creu) ho tallaria tot.
        if (_show.ActiveCues.Count > 0 && MessageBox.Show(this,
                $"Hi ha {_show.ActiveCues.Count} cue(s) en marxa. Si tanques Escena s'aturaran de cop.\n\nVols tancar igualment?",
                "Escena", MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel) != MessageBoxResult.OK)
        {
            e.Cancel = true;
            return;
        }
        if (!ConfirmDiscard())
        {
            e.Cancel = true;
            return;
        }
        _show.Dispose();
    }
}
