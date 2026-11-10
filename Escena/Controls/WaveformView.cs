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
/// </summary>
public sealed class WaveformView : FrameworkElement
{
    private const int Bins = 1200;
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
        ToolTip = "Arrossega els marcadors: verd = inici, vermell = final.\nClic dret: posa el final aquí. Doble clic: tot el fitxer.";
    }

    private static T Freeze<T>(T f) where T : Freezable { f.Freeze(); return f; }

    private async void Load()
    {
        string? path = FilePath;
        _peaks = null;
        _error = null;
        _duration = 0;
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
        double framesPerBin = Math.Max(1, totalFrames / Bins);

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

    private double XOf(double seconds) => _duration > 0 ? seconds / _duration * ActualWidth : 0;

    private double SecondsAt(double x) => Math.Round(Math.Clamp(x / Math.Max(1, ActualWidth), 0, 1) * _duration, 2);

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
            for (int x = 0; x < columns; x++)
            {
                int b0 = x * _peaks.Length / columns;
                int b1 = Math.Max(b0 + 1, (x + 1) * _peaks.Length / columns);
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

        double xs = XOf(StartAt), xe = XOf(EffectiveEnd);
        if (xs > 0) dc.DrawRectangle(Shade, null, new Rect(0, 0, xs, h));
        if (xe < w) dc.DrawRectangle(Shade, null, new Rect(xe, 0, w - xe, h));
        dc.DrawLine(StartPen, new Point(xs, 0), new Point(xs, h));
        dc.DrawLine(EndPen, new Point(xe, 0), new Point(xe, h));

        if (ShowPlayhead)
        {
            double xp = XOf(StartAt + Elapsed);
            dc.DrawLine(HeadPen, new Point(xp, 0), new Point(xp, h));
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
        if (_dragMarker != 0 && IsMouseCaptured) MoveMarker(e.GetPosition(this).X);
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
            StartAt = Math.Min(t, EffectiveEnd - 0.05);
        }
        else if (_dragMarker == 2)
        {
            t = Math.Max(t, StartAt + 0.05);
            EndAt = t >= _duration - 0.01 ? 0 : t; // al final de tot = «fins al final»
        }
    }
}
