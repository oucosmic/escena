using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Escena.Audio;
using Escena.Model;
using Escena.Video;

namespace Escena;

/// <summary>Edita una còpia de les sortides (àudio i pantalles); només s'apliquen en prémer «D'acord».</summary>
public partial class OutputsWindow : Window
{
    private readonly ObservableCollection<OutputConfig> _items;
    private readonly List<MonitorInfo> _monitors = Monitors.List();
    private readonly Func<Guid, VideoOutput?>? _liveVideo;
    private readonly Dictionary<Guid, double[]> _originalCorners = new();
    private readonly TextBox[] _cornerBoxes = new TextBox[8];
    private readonly CheckBox _gridCheck = new() { Content = "Mostra la graella de prova a la pantalla" };
    private bool _loadingCorners;

    public OutputsWindow(IEnumerable<OutputConfig> outputs, Func<Guid, VideoOutput?>? liveVideo = null)
    {
        InitializeComponent();
        _liveVideo = liveVideo;
        _items = new ObservableCollection<OutputConfig>(outputs.Select(o => o.Clone()));
        foreach (var v in _items.OfType<VideoOutputConfig>()) _originalCorners[v.Id] = (double[])v.Corners.Clone();
        BuildCornerEditor();
        Closed += (_, _) => RestoreLivePreview();
        if (!_items.OfType<AudioOutputConfig>().Any()) _items.Insert(0, AudioOutputConfig.CreateDefault());
        ScreenCombo.ItemsSource = _monitors;
        OutputList.ItemsSource = _items;
        OutputList.SelectedIndex = 0;
    }

    public IReadOnlyList<OutputConfig> Result => _items.ToList();

    private OutputConfig? Current => OutputList.SelectedItem as OutputConfig;

    private void OutputList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var current = Current;
        DataContext = current;
        AudioEditor.Visibility = current is AudioOutputConfig ? Visibility.Visible : Visibility.Collapsed;
        VideoEditor.Visibility = current is VideoOutputConfig ? Visibility.Visible : Visibility.Collapsed;
        NameBox.IsEnabled = current != null;
        TestResult.Text = "";
        if (current is AudioOutputConfig) RefreshDevices();
        if (current is VideoOutputConfig video)
        {
            SelectScreen(video);
            LoadCorners(video);
        }
    }

    // ---- Correcció del projector ----

    private void BuildCornerEditor()
    {
        string[] names = { "Dalt a l'esquerra", "Dalt a la dreta", "Baix a la dreta", "Baix a l'esquerra" };
        var muted = (Brush)FindResource("MutedBrush");
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(90) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(90) });
        grid.RowDefinitions.Add(new RowDefinition());
        var hx = new TextBlock { Text = "→ horitzontal", Foreground = muted, FontSize = 11 };
        var hy = new TextBlock { Text = "↓ vertical", Foreground = muted, FontSize = 11 };
        Grid.SetColumn(hx, 1);
        Grid.SetColumn(hy, 2);
        grid.Children.Add(hx);
        grid.Children.Add(hy);
        for (int r = 0; r < 4; r++)
        {
            grid.RowDefinitions.Add(new RowDefinition());
            var label = new TextBlock { Text = names[r], VerticalAlignment = VerticalAlignment.Center };
            Grid.SetRow(label, r + 1);
            grid.Children.Add(label);
            for (int k = 0; k < 2; k++)
            {
                int index = r * 2 + k;
                var box = new TextBox
                {
                    Margin = new Thickness(3), TextAlignment = TextAlignment.Center,
                    ToolTip = "Percentatge de la pantalla. ↑↓: 0,1 % · Maj+↑↓: 1 %",
                };
                box.LostFocus += (_, _) => CornerEdited(index);
                box.PreviewKeyDown += (_, e) => CornerKey(index, e);
                _cornerBoxes[index] = box;
                Grid.SetRow(box, r + 1);
                Grid.SetColumn(box, k + 1);
                grid.Children.Add(box);
            }
        }
        CornerPanel.Children.Add(grid);
        _gridCheck.Checked += (_, _) => PreviewCorners();
        _gridCheck.Unchecked += (_, _) => PreviewCorners();
        CornerPanel.Children.Add(_gridCheck);
        var reset = new Button { Content = "Sense correcció", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 6, 0, 0) };
        reset.Click += (_, _) =>
        {
            if (Current is not VideoOutputConfig cfg) return;
            cfg.Corners = new double[8];
            LoadCorners(cfg);
            PreviewCorners();
        };
        CornerPanel.Children.Add(reset);
        CornerPanel.Children.Add(new TextBlock
        {
            Text = "Mou cada cantonada de la imatge perquè quadri amb la pantalla quan el projector està descentrat o inclinat. "
                   + "Es veu en directe a la pantalla si la sortida ja està oberta. Positiu = cap a la dreta o cap avall.",
            Foreground = muted, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0),
        });
    }

    private void LoadCorners(VideoOutputConfig cfg)
    {
        _loadingCorners = true;
        for (int i = 0; i < 8; i++)
            _cornerBoxes[i].Text = (cfg.Corners[i] * 100).ToString("0.0#", System.Globalization.CultureInfo.CurrentCulture);
        _loadingCorners = false;
    }

    private void CornerEdited(int index)
    {
        if (_loadingCorners || Current is not VideoOutputConfig cfg) return;
        string text = _cornerBoxes[index].Text.Replace("%", "").Trim();
        if (!double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.CurrentCulture, out double v)
            && !double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out v))
            v = 0;
        var corners = (double[])cfg.Corners.Clone();
        corners[index] = Math.Clamp(v, -40, 40) / 100;
        cfg.Corners = corners;
        LoadCorners(cfg);
        PreviewCorners();
    }

    private void CornerKey(int index, System.Windows.Input.KeyEventArgs e)
    {
        var key = e.Key;
        if (key is not (System.Windows.Input.Key.Up or System.Windows.Input.Key.Down or System.Windows.Input.Key.Enter)) return;
        if (Current is not VideoOutputConfig cfg) return;
        if (key == System.Windows.Input.Key.Enter)
        {
            CornerEdited(index);
        }
        else
        {
            bool big = System.Windows.Input.Keyboard.Modifiers.HasFlag(System.Windows.Input.ModifierKeys.Shift);
            double step = (big ? 1 : 0.1) / 100;
            var corners = (double[])cfg.Corners.Clone();
            corners[index] = Math.Clamp(corners[index] + (key == System.Windows.Input.Key.Up ? -step : step), -0.4, 0.4);
            cfg.Corners = corners;
            LoadCorners(cfg);
            PreviewCorners();
        }
        e.Handled = true;
    }

    private void PreviewCorners()
    {
        if (Current is not VideoOutputConfig cfg) return;
        var live = _liveVideo?.Invoke(cfg.Id);
        if (live == null)
        {
            TestResult.Text = "Aquesta pantalla encara no està oberta: la correcció s'aplicarà en prémer «D'acord».";
            return;
        }
        live.PreviewCorrection(cfg.Corners, _gridCheck.IsChecked == true);
    }

    /// <summary>En tancar: treu la graella i, si s'ha cancel·lat, torna a la correcció d'abans.</summary>
    private void RestoreLivePreview()
    {
        foreach (var (id, original) in _originalCorners)
        {
            var live = _liveVideo?.Invoke(id);
            if (live == null) continue;
            var edited = _items.OfType<VideoOutputConfig>().FirstOrDefault(v => v.Id == id)?.Corners ?? original;
            live.PreviewCorrection(DialogResult == true ? edited : original, false);
        }
    }

    // ---- Àudio ----

    private void DriverCombo_SelectionChanged(object sender, SelectionChangedEventArgs e) => RefreshDevices();

    private void RefreshDevices()
    {
        if (Current is not AudioOutputConfig cfg) return;
        string keep = cfg.DeviceId;
        var devices = AudioDevices.List(cfg.Driver);
        if (cfg.Driver == AudioDriver.Asio && devices.Count == 0)
            devices.Add(new AudioDevice("", "(no hi ha cap controlador ASIO instal·lat)"));
        DeviceCombo.ItemsSource = devices;
        // Si el dispositiu desat no hi és (canvi de controlador o d'ordinador), es queda el predeterminat.
        cfg.DeviceId = devices.Any(d => d.Id == keep) ? keep : devices.FirstOrDefault()?.Id ?? "";
        DeviceCombo.SelectedValue = cfg.DeviceId;
    }

    // ---- Pantalles ----

    private void SelectScreen(VideoOutputConfig cfg)
    {
        // Pantalla buida o que no hi és: la primera ampliada (o la principal).
        if (_monitors.All(m => m.DeviceName != cfg.ScreenId))
            cfg.ScreenId = (_monitors.FirstOrDefault(m => !m.IsPrimary) ?? _monitors.First()).DeviceName;
        ScreenCombo.SelectedValue = cfg.ScreenId;
        UpdateScreenHint();
    }

    private void ScreenCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Current is VideoOutputConfig cfg && ScreenCombo.SelectedItem is MonitorInfo m && e.AddedItems.Count > 0
            && e.RemovedItems.Count > 0)
        {
            // En canviar de pantalla: ampliada → pantalla completa; principal → finestra.
            cfg.Fullscreen = !m.IsPrimary;
        }
        UpdateScreenHint();
    }

    private void UpdateScreenHint()
    {
        ScreenHint.Text = ScreenCombo.SelectedItem is MonitorInfo { IsPrimary: true }
            ? "A la pantalla principal, millor en finestra: així continues veient Escena. Pots moure-la i canviar-ne la mida."
            : _monitors.Count == 1
                ? "Només hi ha una pantalla connectada. Connecta el projector i torna a obrir aquesta finestra."
                : "A la pantalla ampliada (projector) es posa a pantalla completa i en negre.";
    }

    private async void Identify_Click(object sender, RoutedEventArgs e)
    {
        var windows = _monitors.Select(m =>
        {
            var w = new Window
            {
                WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false,
                Topmost = true, ShowActivated = false, Background = new SolidColorBrush(Color.FromArgb(230, 20, 20, 24)),
                AllowsTransparency = true, Width = 520, Height = 220, WindowStartupLocation = WindowStartupLocation.Manual,
                Content = new TextBlock
                {
                    Text = $"{m.Index}\n{(m.IsPrimary ? "principal" : "ampliada")}", Foreground = Brushes.White,
                    FontSize = 56, FontWeight = FontWeights.Bold, TextAlignment = TextAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                },
            };
            w.SourceInitialized += (_, _) =>
            {
                var dpi = VisualTreeHelper.GetDpi(w);
                // Centrat a la pantalla (coordenades de Windows convertides a unitats WPF de la finestra).
                w.Left = (m.Left + m.Width / 2.0) / dpi.DpiScaleX - w.Width / 2;
                w.Top = (m.Top + m.Height / 2.0) / dpi.DpiScaleY - w.Height / 2;
            };
            w.Show();
            return w;
        }).ToList();
        await Task.Delay(2500);
        foreach (var w in windows) w.Close();
    }

    // ---- Comú ----

    private void AddAudio_Click(object sender, RoutedEventArgs e) =>
        AddItem(new AudioOutputConfig { Name = $"Àudio {_items.OfType<AudioOutputConfig>().Count() + 1}" });

    private void AddVideo_Click(object sender, RoutedEventArgs e)
    {
        int n = _items.OfType<VideoOutputConfig>().Count();
        AddItem(new VideoOutputConfig { Name = n == 0 ? "Pantalla" : $"Pantalla {n + 1}" });
    }

    private void AddItem(OutputConfig cfg)
    {
        _items.Add(cfg);
        OutputList.SelectedItem = cfg;
    }

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (Current == null) return;
        if (Current is AudioOutputConfig && _items.OfType<AudioOutputConfig>().Count() == 1)
        {
            MessageBox.Show(this, "Cal com a mínim una sortida d'àudio.", "Escena");
            return;
        }
        _items.Remove(Current);
        OutputList.SelectedIndex = 0;
    }

    private void Test_Click(object sender, RoutedEventArgs e)
    {
        if (Current == null) return;
        IMediaOutput output = AudioOutput.Create(Current);
        try
        {
            output.Open();
            output.Test();
            TestResult.Text = (output.Warning ?? (Current is VideoOutputConfig
                ? "El nom ha d'aparèixer a la pantalla escollida."
                : "Ha de sonar un to de 440 Hz.")) + "\n" + output.Description;
        }
        catch (Exception ex)
        {
            TestResult.Text = "Error: " + ex.Message;
            output.Dispose();
            return;
        }
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.2) };
        timer.Tick += (_, _) => { timer.Stop(); output.Dispose(); };
        timer.Start();
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        foreach (var cfg in _items.Where(c => string.IsNullOrWhiteSpace(c.Name)))
            cfg.Name = cfg is VideoOutputConfig ? "Pantalla" : "Sortida";
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
