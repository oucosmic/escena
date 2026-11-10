using System.Collections.Concurrent;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Escena.Audio;
using NAudio.Wave;

namespace Escena.Controls;

/// <summary>
/// Forma d'ona d'un fitxer amb els punts d'inici (verd) i final (vermell) arrossegables
/// i el capçal de reproducció. Clic esquerre: mou el marcador més proper. Clic dret: posa el final.
/// Doble clic: torna a tot el fitxer.
/// Zoom: roda del ratolí (al voltant del punter), Maj+roda o arrossegar amb el botó del mig per desplaçar,
/// i les ordres <see cref="ZoomInCommand"/>, <see cref="ZoomOutCommand"/>, <see cref="ZoomAllCommand"/>,
/// <see cref="ZoomStartCommand"/> i <see cref="ZoomEndCommand"/> (botons sota la forma d'ona).
/// </summary>
public sealed class WaveformView : FrameworkElement
{
    /// <summary>Resolució de la forma d'ona: punts per segon de fitxer (5 ms), perquè es pugui ampliar molt.</summary>
    private const int BinsPerSecond = 200;
    private const int MinBins = 1200, MaxBins = 2_000_000;
    /// <summary>El tros més curt que es pot veure en ampliar (segons).</summary>
    private const double MinView = 0.05;

    public static readonly RoutedUICommand ZoomInCommand = new("Amplia", "ZoomIn", typeof(WaveformView));
    public static readonly RoutedUICommand ZoomOutCommand = new("Redueix", "ZoomOut", typeof(WaveformView));
    public static readonly RoutedUICommand ZoomAllCommand = new("Tot el fitxer", "ZoomAll", typeof(WaveformView));
    public static readonly RoutedUICommand ZoomStartCommand = new("Amplia l'inici", "ZoomStart", typeof(WaveformView));
    public static readonly RoutedUICommand ZoomEndCommand = new("Amplia el final", "ZoomEnd", typeof(WaveformView));

    private double _viewStart, _viewLength; // tros visible (segons); _viewLength 0 = tot
    private double _panFromX, _panFromStart;
    private bool _panning;
    private static readonly ConcurrentDictionary<string, (float[] Peaks, double Duration)> Cache = new();

    private static readonly Brush Background = Freeze(new SolidColorBrush(Color.FromRgb(0x15, 0x16, 0x1A)));
    private static readonly Pen WavePen = Freeze(new Pen(new SolidColorBrush(Color.FromRgb(0x5B, 0x9B, 0xF0)), 1));
    private static readonly Brush Shade = Freeze(new SolidColorBrush(Color.FromArgb(0xB0, 0x10, 0x10, 0x12)));
    private static readonly Pen StartPen = Freeze(new Pen(new SolidColorBrush(Color.FromRgb(0x3F, 0xC2, 0x66)), 2));
    private static readonly Pen EndPen = Freeze(new Pen(new SolidColorBrush(Color.FromRgb(0xE0, 0x55, 0x55)), 2));
    private static readonly Pen HeadPen = Freeze(new Pen(Brushes.White, 1.5));
    private static readonly Brush TextBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x9B, 0xA0, 0xA8)));

    private float[]? _peaks;
    private double _duration;
    private bool _loading;
    private string? _error;
    private int _dragMarker; // 0 cap, 1 inici, 2 final

    public static readonly DependencyProperty FilePathProperty = DependencyProperty.Register(
        nameof(FilePath), typeof(string), typeof(WaveformView),
        new FrameworkPropertyMetadata(null, (d, _) => ((WaveformView)d).Load()));

    public static readonly DependencyProperty StartAtProperty = DependencyProperty.Register(
        nameof(StartAt), typeof(double), typeof(WaveformView),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    public static readonly DependencyProperty EndAtProperty = DependencyProperty.Register(
        nameof(EndAt), typeof(double), typeof(WaveformView),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    public static readonly DependencyProperty ElapsedProperty = DependencyProperty.Register(
        nameof(Elapsed), typeof(double), typeof(WaveformView),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ShowPlayheadProperty = DependencyProperty.Register(
        nameof(ShowPlayhead), typeof(bool), typeof(WaveformView),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty SlicesProperty = DependencyProperty.Register(
        nameof(Slices), typeof(System.Collections.ObjectModel.ObservableCollection<Escena.Model.LoopSlice>), typeof(WaveformView),
        new FrameworkPropertyMetadata(null, (d, e) => ((WaveformView)d).OnSlicesChanged(e)));

    /// <summary>Trossos en bucle, que es pinten en groc.</summary>
    public System.Collections.ObjectModel.ObservableCollection<Escena.Model.LoopSlice>? Slices
    {
        get => (System.Collections.ObjectModel.ObservableCollection<Escena.Model.LoopSlice>?)GetValue(SlicesProperty);
        set => SetValue(SlicesProperty, value);
    }

    private static readonly Brush SliceBrush = Freeze(new SolidColorBrush(Color.FromArgb(0x50, 0xF0, 0xB4, 0x29)));
    private static readonly Pen SlicePen = Freeze(new Pen(new SolidColorBrush(Color.FromRgb(0xF0, 0xB4, 0x29)), 1.5));

    private void OnSlicesChanged(DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is System.Collections.ObjectModel.ObservableCollection<Escena.Model.LoopSlice> old)
        {
            old.CollectionChanged -= OnSliceCollectionChanged;
            foreach (var s in old) s.PropertyChanged -= OnSlicePropertyChanged;
        }
        if (e.NewValue is System.Collections.ObjectModel.ObservableCollection<Escena.Model.LoopSlice> now)
        {
            now.CollectionChanged += OnSliceCollectionChanged;
            foreach (var s in now) s.PropertyChanged += OnSlicePropertyChanged;
        }
        InvalidateVisual();
    }

    private void OnSliceCollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems != null) foreach (Escena.Model.LoopSlice s in e.OldItems) s.PropertyChanged -= OnSlicePropertyChanged;
        if (e.NewItems != null) foreach (Escena.Model.LoopSlice s in e.NewItems) s.PropertyChanged += OnSlicePropertyChanged;
        InvalidateVisual();
    }

    private void OnSlicePropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) => InvalidateVisual();

    public string? FilePath { get => (string?)GetValue(FilePathProperty); set => SetValue(FilePathProperty, value); }
    public double StartAt { get => (double)GetValue(StartAtProperty); set => SetValue(StartAtProperty, value); }
    public double EndAt { get => (double)GetValue(EndAtProperty); set => SetValue(EndAtProperty, value); }
    public double Elapsed { get => (double)GetValue(ElapsedProperty); set => SetValue(ElapsedProperty, value); }
    public bool ShowPlayhead { get => (bool)GetValue(ShowPlayheadProperty); set => SetValue(ShowPlayheadProperty, value); }

    public WaveformView()
    {
        Cursor = Cursors.IBeam;
        Focusable = false;
        ToolTip = Escena.L.T("Arrossega els marcadors: verd = inici, vermell = final.\nClic dret: posa el final aquí. Doble clic: tot el fitxer.\nRoda del ratolí: zoom. Maj+roda o botó del mig: desplaça.");
        CommandBindings.Add(new CommandBinding(ZoomInCommand, (_, _) => ZoomBy(0.5, ViewStart + ViewLength / 2), (_, e) => e.CanExecute = _duration > 0));
        CommandBindings.Add(new CommandBinding(ZoomOutCommand, (_, _) => ZoomBy(2, ViewStart + ViewLength / 2), (_, e) => e.CanExecute = IsZoomed));
        CommandBindings.Add(new CommandBinding(ZoomAllCommand, (_, _) => ResetView(), (_, e) => e.CanExecute = IsZoomed));
        CommandBindings.Add(new CommandBinding(ZoomStartCommand, (_, _) => ZoomAround(StartAt), (_, e) => e.CanExecute = _duration > 0));
        CommandBindings.Add(new CommandBinding(ZoomEndCommand, (_, _) => ZoomAround(EffectiveEnd), (_, e) => e.CanExecute = _duration > 0));
        Loaded += (_, _) => HookHorizontalWheel();
        Unloaded += (_, _) => UnhookHorizontalWheel();
    }

    // ---- Zoom ----

    /// <summary>Inici del tros visible (segons).</summary>
    public double ViewStart => _viewLength > 0 ? _viewStart : 0;

    /// <summary>Durada del tros visible (segons).</summary>
    public double ViewLength => _viewLength > 0 ? _viewLength : _duration;

    public bool IsZoomed => _viewLength > 0 && _viewLength < _duration - 1e-6;

    private void SetView(double start, double length)
    {
        if (_duration <= 0) return;
        length = Math.Clamp(length, Math.Min(MinView, _duration), _duration);
        start = Math.Clamp(start, 0, _duration - length);
        if (length >= _duration - 1e-6)
        {
            start = 0;
            length = 0;
        }
        _viewStart = start;
        _viewLength = length;
        InvalidateVisual();
        CommandManager.InvalidateRequerySuggested();
        PublishScroll();
    }

    // ---- Barra de desplaçament (es lliga des del XAML amb ElementName) ----

    private bool _publishing;

    private static readonly DependencyPropertyKey ScrollMaxKey = DependencyProperty.RegisterReadOnly(
        nameof(ScrollMax), typeof(double), typeof(WaveformView), new PropertyMetadata(0.0));
    public static readonly DependencyProperty ScrollMaxProperty = ScrollMaxKey.DependencyProperty;

    private static readonly DependencyPropertyKey ScrollViewportKey = DependencyProperty.RegisterReadOnly(
        nameof(ScrollViewport), typeof(double), typeof(WaveformView), new PropertyMetadata(1.0));
    public static readonly DependencyProperty ScrollViewportProperty = ScrollViewportKey.DependencyProperty;

    private static readonly DependencyPropertyKey IsZoomedKey = DependencyProperty.RegisterReadOnly(
        nameof(IsZoomedView), typeof(bool), typeof(WaveformView), new PropertyMetadata(false));
    public static readonly DependencyProperty IsZoomedViewProperty = IsZoomedKey.DependencyProperty;

    public static readonly DependencyProperty ScrollOffsetProperty = DependencyProperty.Register(
        nameof(ScrollOffset), typeof(double), typeof(WaveformView),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
            (d, e) => ((WaveformView)d).OnScrollOffsetChanged((double)e.NewValue)));

    /// <summary>Màxim de la barra: durada menys el tros visible (segons).</summary>
    public double ScrollMax => (double)GetValue(ScrollMaxProperty);

    /// <summary>Tros visible (segons), per a la mida del polze de la barra.</summary>
    public double ScrollViewport => (double)GetValue(ScrollViewportProperty);

    /// <summary>Hi ha zoom (la barra només es mostra llavors).</summary>
    public bool IsZoomedView => (bool)GetValue(IsZoomedViewProperty);

    /// <summary>Inici del tros visible, lligat a la barra.</summary>
    public double ScrollOffset { get => (double)GetValue(ScrollOffsetProperty); set => SetValue(ScrollOffsetProperty, value); }

    private void PublishScroll()
    {
        _publishing = true;
        try
        {
            SetValue(ScrollMaxKey, Math.Max(0, _duration - ViewLength));
            SetValue(ScrollViewportKey, Math.Max(0.001, ViewLength));
            SetValue(IsZoomedKey, IsZoomed);
            ScrollOffset = ViewStart;
        }
        finally { _publishing = false; }
    }

    private void OnScrollOffsetChanged(double value)
    {
        if (_publishing || !IsZoomed) return;
        SetView(value, ViewLength);
    }

    /// <summary>Desplaça la vista una fracció del tros visible (negatiu = cap a l'inici).</summary>
    public void Pan(double fraction)
    {
        if (IsZoomed) SetView(ViewStart + fraction * ViewLength, ViewLength);
    }

    // ---- Desplaçament horitzontal del touchpad o de la roda inclinada (WM_MOUSEHWHEEL, que WPF no rep) ----

    private const int WM_MOUSEHWHEEL = 0x020E;
    private System.Windows.Interop.HwndSource? _hwnd;

    private void HookHorizontalWheel()
    {
        UnhookHorizontalWheel();
        _hwnd = PresentationSource.FromVisual(this) as System.Windows.Interop.HwndSource;
        _hwnd?.AddHook(WndProc);
    }

    private void UnhookHorizontalWheel()
    {
        _hwnd?.RemoveHook(WndProc);
        _hwnd = null;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_MOUSEHWHEEL && IsMouseOver && IsZoomed)
        {
            int delta = (short)((wParam.ToInt64() >> 16) & 0xFFFF); // positiu = cap a la dreta
            Pan(delta / 120.0 * 0.15);
            handled = true;
        }
        return IntPtr.Zero;
    }

    /// <summary>Amplia (factor &lt; 1) o redueix (&gt; 1) mantenint <paramref name="anchor"/> al mateix lloc de la pantalla.</summary>
    public void ZoomBy(double factor, double anchor)
    {
        if (_duration <= 0) return;
        double len = ViewLength, start = ViewStart;
        double frac = len > 0 ? (anchor - start) / len : 0.5;
        double newLen = len * factor;
        SetView(anchor - frac * Math.Clamp(newLen, MinView, _duration), newLen);
    }

    /// <summary>Amplia uns 2 segons al voltant d'un punt (per afinar l'inici o el final).</summary>
    public void ZoomAround(double seconds)
    {
        double len = Math.Min(2, _duration);
        SetView(seconds - len / 2, len);
    }

    public void ResetView() => SetView(0, _duration);

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        if (_duration <= 0) return;
        double steps = e.Delta / 120.0;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
            Pan(-steps * 0.15);
        else
            ZoomBy(Math.Pow(0.8, steps), SecondsAt(e.GetPosition(this).X, round: false));
        e.Handled = true; // que no es desplaci el panell de propietats
    }

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.ChangedButton != MouseButton.Middle || !IsZoomed) return;
        _panning = true;
        _panFromX = e.GetPosition(this).X;
        _panFromStart = ViewStart;
        Cursor = Cursors.ScrollWE;
        CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.ChangedButton != MouseButton.Middle || !_panning) return;
        _panning = false;
        Cursor = Cursors.IBeam;
        ReleaseMouseCapture();
    }

    private static T Freeze<T>(T f) where T : Freezable { f.Freeze(); return f; }

    private async void Load()
    {
        string? path = FilePath;
        _peaks = null;
        _error = null;
        _duration = 0;
        _viewStart = _viewLength = 0;
        PublishScroll();
        CommandManager.InvalidateRequerySuggested();
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            _loading = false;
            InvalidateVisual();
            return;
        }
        if (Cache.TryGetValue(path, out var cached))
        {
            (_peaks, _duration) = cached;
            _loading = false;
            InvalidateVisual();
            return;
        }

        _loading = true;
        InvalidateVisual();
        try
        {
            var result = await Task.Run(() => Compute(path));
            Cache[path] = result;
            if (path != FilePath) return; // mentrestant s'ha canviat de cue
            (_peaks, _duration) = result;
        }
        catch (Exception ex)
        {
            if (path == FilePath) _error = ex.Message;
        }
        if (path == FilePath)
        {
            _loading = false;
            InvalidateVisual();
        }
    }

    private static (float[] Peaks, double Duration) Compute(string path)
    {
        using var stream = AudioReaders.Open(path);
        var samples = (ISampleProvider)stream;
        int channels = samples.WaveFormat.Channels;
        double duration = stream.TotalTime.TotalSeconds;
        double totalFrames = Math.Max(1, duration * samples.WaveFormat.SampleRate);
        int bins = (int)Math.Clamp(duration * BinsPerSecond, MinBins, MaxBins);
        double framesPerBin = Math.Max(1, totalFrames / bins);
        int Bins = bins;

        var peaks = new float[Bins];
        var buffer = new float[channels * 16384];
        long frame = 0;
        int n;
        while ((n = samples.Read(buffer, 0, buffer.Length)) > 0)
        {
            for (int i = 0; i < n; i++)
            {
                int bin = (int)((frame + i / channels) / framesPerBin);
                if (bin >= Bins) bin = Bins - 1;
                float a = Math.Abs(buffer[i]);
                if (a > peaks[bin]) peaks[bin] = a;
            }
            frame += n / channels;
        }
        float max = peaks.Max();
        if (max > 0 && max < 1) for (int i = 0; i < Bins; i++) peaks[i] /= max; // normalitzat per veure-ho bé
        return (peaks, duration);
    }

    private double EffectiveEnd => EndAt > StartAt && EndAt < _duration ? EndAt : _duration;

    private double XOf(double seconds) => ViewLength > 0 ? (seconds - ViewStart) / ViewLength * ActualWidth : 0;

    /// <summary>Temps al punt x. Amb zoom es pot afinar fins al mil·lisegon.</summary>
    private double SecondsAt(double x, bool round = true)
    {
        double t = ViewStart + Math.Clamp(x / Math.Max(1, ActualWidth), 0, 1) * ViewLength;
        return round ? Math.Round(t, ViewLength < 20 ? 3 : 2) : t;
    }

    private static readonly Brush OverviewBrush = Freeze(new SolidColorBrush(Color.FromArgb(0x60, 0x9B, 0xA0, 0xA8)));
    private static readonly Brush OverviewViewBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x5B, 0x9B, 0xF0)));

    private void DrawLabel(DrawingContext dc, string text, double x, double y, bool alignRight)
    {
        var ft = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface("Segoe UI"), 10, Brushes.White, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        double left = alignRight ? x - ft.Width : x;
        dc.DrawRectangle(Background, null, new Rect(left - 2, y, ft.Width + 4, ft.Height)); // que es llegeixi sobre l'ona
        dc.DrawText(ft, new Point(left, y));
    }

    private static string FormatTime(double s, double span) =>
        span < 20 ? TimeSpan.FromSeconds(s).ToString(@"m\:ss\.fff") : TimeSpan.FromSeconds(s).ToString(@"m\:ss\.f");

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        dc.DrawRectangle(Background, null, new Rect(0, 0, w, h));

        if (_peaks == null || _duration <= 0)
        {
            string text = _loading ? "Carregant la forma d'ona…" : _error != null ? "No es pot llegir: " + _error : "Sense fitxer";
            var ft = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                new Typeface("Segoe UI"), 12, TextBrush, VisualTreeHelper.GetDpi(this).PixelsPerDip);
            dc.DrawText(ft, new Point(8, (h - ft.Height) / 2));
            return;
        }

        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            double mid = h / 2;
            int columns = Math.Max(1, (int)w);
            // Només el tros visible: cada columna agafa el pic dels punts que hi caben.
            double binsPerSec = _peaks.Length / _duration;
            double firstBin = ViewStart * binsPerSec, binsVisible = ViewLength * binsPerSec;
            for (int x = 0; x < columns; x++)
            {
                int b0 = (int)(firstBin + x * binsVisible / columns);
                int b1 = Math.Max(b0 + 1, (int)(firstBin + (x + 1) * binsVisible / columns));
                float peak = 0;
                for (int b = b0; b < b1 && b < _peaks.Length; b++) peak = Math.Max(peak, _peaks[b]);
                double half = Math.Max(0.5, peak * (mid - 2));
                ctx.BeginFigure(new Point(x + 0.5, mid - half), false, false);
                ctx.LineTo(new Point(x + 0.5, mid + half), true, false);
            }
        }
        geometry.Freeze();
        dc.DrawGeometry(null, WavePen, geometry);

        if (Slices != null)
        {
            foreach (var s in Slices.Where(s => s.IsValid))
            {
                double x0 = XOf(Math.Min(s.Start, _duration)), x1 = XOf(Math.Min(s.End, _duration));
                dc.DrawRectangle(SliceBrush, null, new Rect(x0, 0, Math.Max(1, x1 - x0), h));
                dc.DrawLine(SlicePen, new Point(x0, 0), new Point(x0, h));
                dc.DrawLine(SlicePen, new Point(x1, 0), new Point(x1, h));
            }
        }

        dc.PushClip(new RectangleGeometry(new Rect(0, 0, w, h)));
        double xs = XOf(StartAt), xe = XOf(EffectiveEnd);
        if (xs > 0) dc.DrawRectangle(Shade, null, new Rect(0, 0, Math.Min(xs, w), h));
        if (xe < w) dc.DrawRectangle(Shade, null, new Rect(Math.Max(0, xe), 0, w - Math.Max(0, xe), h));
        dc.DrawLine(StartPen, new Point(xs, 0), new Point(xs, h));
        dc.DrawLine(EndPen, new Point(xe, 0), new Point(xe, h));

        if (ShowPlayhead)
        {
            double xp = XOf(StartAt + Elapsed);
            dc.DrawLine(HeadPen, new Point(xp, 0), new Point(xp, h));
        }
        dc.Pop();

        if (IsZoomed)
        {
            // Temps dels extrems i, a baix, quin tros del fitxer es veu.
            DrawLabel(dc, FormatTime(ViewStart, ViewLength), 3, 1, false);
            DrawLabel(dc, FormatTime(ViewStart + ViewLength, ViewLength), w - 3, 1, true);
            dc.DrawRectangle(OverviewBrush, null, new Rect(0, h - 3, w, 3));
            dc.DrawRectangle(OverviewViewBrush, null, new Rect(ViewStart / _duration * w, h - 3, Math.Max(2, ViewLength / _duration * w), 3));
        }
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        if (_duration <= 0) return;
        if (e.ClickCount == 2)
        {
            StartAt = 0;
            EndAt = 0;
            e.Handled = true;
            return;
        }
        double x = e.GetPosition(this).X;
        _dragMarker = Math.Abs(x - XOf(StartAt)) <= Math.Abs(x - XOf(EffectiveEnd)) ? 1 : 2;
        CaptureMouse();
        MoveMarker(x);
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_panning && IsMouseCaptured)
        {
            double dx = e.GetPosition(this).X - _panFromX;
            SetView(_panFromStart - dx / Math.Max(1, ActualWidth) * ViewLength, ViewLength);
            return;
        }
        if (_dragMarker != 0 && IsMouseCaptured)
        {
            double x = e.GetPosition(this).X;
            // Arrossegant un marcador fins a la vora, la vista es desplaça.
            if (IsZoomed && (x < 0 || x > ActualWidth))
                SetView(ViewStart + (x < 0 ? -1 : 1) * ViewLength * 0.05, ViewLength);
            MoveMarker(x);
        }
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        _dragMarker = 0;
        ReleaseMouseCapture();
    }

    protected override void OnMouseRightButtonDown(MouseButtonEventArgs e)
    {
        if (_duration <= 0) return;
        _dragMarker = 2;
        MoveMarker(e.GetPosition(this).X);
        _dragMarker = 0;
        e.Handled = true;
    }

    private void MoveMarker(double x)
    {
        double t = SecondsAt(x);
        if (_dragMarker == 1)
        {
            StartAt = Math.Min(t, EffectiveEnd - 0.01);
        }
        else if (_dragMarker == 2)
        {
            t = Math.Max(t, StartAt + 0.01);
            EndAt = t >= _duration - 0.001 ? 0 : t; // al final de tot = «fins al final»
        }
    }
}
