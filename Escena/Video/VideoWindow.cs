using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using Path = System.Windows.Shapes.Path;

namespace Escena.Video;

/// <summary>
/// Finestra de sortida de vídeo: fons negre i una capa per cada vídeo, imatge o text que es mostra
/// (la darrera a sobre, així es poden fer encadenats). A pantalla completa no agafa el focus
/// ni mostra el cursor; les tecles que hi arriben es passen a la finestra principal.
/// Amb correcció de projector, l'escena es dibuixa deformada sobre una malla (cantonades).
/// </summary>
public sealed class VideoWindow : Window
{
    private readonly Grid _layers = new() { Background = Brushes.Black, ClipToBounds = true };
    private readonly Grid _scene = new() { Background = Brushes.Black, ClipToBounds = true };
    private readonly Path _grid;
    private readonly Viewport3D _viewport = new() { Visibility = Visibility.Collapsed, ClipToBounds = true };
    private readonly MeshGeometry3D _mesh = new();
    private readonly TextBlock _ident;
    private readonly MonitorInfo _monitor;
    private readonly bool _fullscreen;
    private double[] _corners = new double[8];
    private bool _allowClose;

    public VideoWindow(string title, MonitorInfo monitor, bool fullscreen)
    {
        _monitor = monitor;
        _fullscreen = fullscreen;
        Title = "Escena — " + title;
        Background = Brushes.Black;
        WindowStartupLocation = WindowStartupLocation.Manual;
        ShowActivated = false;
        Icon = Application.Current?.MainWindow?.Icon;

        _ident = new TextBlock
        {
            Foreground = Brushes.White, FontSize = 48, FontWeight = FontWeights.Bold,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            TextAlignment = TextAlignment.Center, Visibility = Visibility.Collapsed,
        };
        _grid = BuildTestGrid();
        _scene.Children.Add(_layers);
        _scene.Children.Add(_grid);
        _scene.Children.Add(_ident);
        BuildViewport();

        // La malla 3D va a sobre de l'escena: quan hi ha correcció la tapa i la mostra deformada.
        var root = new Grid { Background = Brushes.Black };
        root.Children.Add(_scene);
        root.Children.Add(_viewport);
        root.SizeChanged += (_, _) => UpdateMesh();
        Content = root;

        if (fullscreen)
        {
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            Topmost = true;
            Cursor = Cursors.None;
        }
        else
        {
            Width = 960;
            Height = 540;
            // En finestra queda sempre per sobre d'Escena (però no de les altres aplicacions).
            if (Application.Current?.MainWindow is { IsLoaded: true } main && main != this) Owner = main;
        }

        SourceInitialized += (_, _) => Place();
        DpiChanged += (_, _) => Place();
        PreviewKeyDown += (_, e) => (Application.Current?.MainWindow as MainWindow)?.HandleShortcut(e);
        Closing += (_, e) => e.Cancel = !_allowClose; // només es tanca amb l'espectacle
    }

    public void AddLayer(UIElement layer) => _layers.Children.Add(layer);

    public void RemoveLayer(UIElement layer) => _layers.Children.Remove(layer);

    /// <summary>Mostra el nom de la sortida uns segons, per identificar la pantalla.</summary>
    public async void Identify(string text)
    {
        _ident.Text = text;
        _ident.Visibility = Visibility.Visible;
        await Task.Delay(2000);
        _ident.Visibility = Visibility.Collapsed;
    }

    public void ForceClose()
    {
        _allowClose = true;
        Close();
    }

    // ---- Correcció del projector (cantonades) ----

    /// <summary>Desplaçament de cada cantonada (fracció de l'amplada/alçada); tot 0 = imatge sense deformar.</summary>
    public void SetCorrection(double[] corners)
    {
        _corners = corners is { Length: 8 } ? (double[])corners.Clone() : new double[8];
        bool active = _corners.Any(c => Math.Abs(c) > 1e-6);
        _viewport.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
        UpdateMesh();
    }

    /// <summary>Graella de prova (amb la correcció aplicada) per alinear el projector.</summary>
    public void ShowTestGrid(bool show) => _grid.Visibility = show ? Visibility.Visible : Visibility.Collapsed;

    private static Path BuildTestGrid()
    {
        var g = new GeometryGroup();
        for (int i = 0; i <= 8; i++)
        {
            g.Children.Add(new LineGeometry(new Point(i * 12.5, 0), new Point(i * 12.5, 100)));
            g.Children.Add(new LineGeometry(new Point(0, i * 12.5), new Point(100, i * 12.5)));
        }
        g.Children.Add(new LineGeometry(new Point(0, 0), new Point(100, 100)));
        g.Children.Add(new LineGeometry(new Point(100, 0), new Point(0, 100)));
        g.Children.Add(new EllipseGeometry(new Point(50, 50), 25, 25));
        return new Path
        {
            Data = g, Stretch = Stretch.Fill, Stroke = Brushes.White, StrokeThickness = 2,
            Visibility = Visibility.Collapsed, IsHitTestVisible = false,
        };
    }

    private void BuildViewport()
    {
        var brush = new VisualBrush(_scene) { Stretch = Stretch.Fill };
        var model = new GeometryModel3D(_mesh, new DiffuseMaterial(brush));
        var group = new Model3DGroup();
        group.Children.Add(new AmbientLight(Colors.White));
        group.Children.Add(model);
        _viewport.Children.Add(new ModelVisual3D { Content = group });
    }

    /// <summary>Malla de 24×24 deformada amb una homografia: el rectangle de la imatge va a les cantonades corregides.</summary>
    private void UpdateMesh()
    {
        if (_viewport.Visibility != Visibility.Visible) return;
        double w = Math.Max(1, _scene.ActualWidth), h = Math.Max(1, _scene.ActualHeight);
        double aspect = h / w;
        var c = _corners;
        var map = Homography(0 + c[0], 0 + c[1], 1 + c[2], 0 + c[3], 1 + c[4], 1 + c[5], 0 + c[6], 1 + c[7]);

        const int n = 24;
        var positions = new Point3DCollection((n + 1) * (n + 1));
        var uvs = new PointCollection((n + 1) * (n + 1));
        var indices = new Int32Collection(n * n * 6);
        for (int j = 0; j <= n; j++)
        for (int i = 0; i <= n; i++)
        {
            double u = (double)i / n, v = (double)j / n;
            var (x, y) = map(u, v);
            positions.Add(new Point3D(x, -y * aspect, 0));
            uvs.Add(new Point(u, v));
        }
        for (int j = 0; j < n; j++)
        for (int i = 0; i < n; i++)
        {
            int a = j * (n + 1) + i, b = a + 1, d = a + n + 1, e = d + 1;
            indices.Add(a); indices.Add(d); indices.Add(b);
            indices.Add(b); indices.Add(d); indices.Add(e);
        }
        _mesh.Positions = positions;
        _mesh.TextureCoordinates = uvs;
        _mesh.TriangleIndices = indices;
        _viewport.Camera = new OrthographicCamera(new Point3D(0.5, -aspect / 2, 10), new Vector3D(0, 0, -1), new Vector3D(0, 1, 0), 1);
    }

    /// <summary>Homografia del quadrat unitat (u,v) al quadrilàter (0 dalt-esquerra, 1 dalt-dreta, 2 baix-dreta, 3 baix-esquerra).</summary>
    public static Func<double, double, (double X, double Y)> Homography(
        double x0, double y0, double x1, double y1, double x2, double y2, double x3, double y3)
    {
        double dx1 = x1 - x2, dx2 = x3 - x2, dx3 = x0 - x1 + x2 - x3;
        double dy1 = y1 - y2, dy2 = y3 - y2, dy3 = y0 - y1 + y2 - y3;
        double g = 0, hh = 0;
        if (Math.Abs(dx3) > 1e-12 || Math.Abs(dy3) > 1e-12)
        {
            double det = dx1 * dy2 - dx2 * dy1;
            if (Math.Abs(det) > 1e-12)
            {
                g = (dx3 * dy2 - dx2 * dy3) / det;
                hh = (dx1 * dy3 - dx3 * dy1) / det;
            }
        }
        double a = x1 - x0 + g * x1, b = x3 - x0 + hh * x3, cc = x0;
        double d = y1 - y0 + g * y1, e = y3 - y0 + hh * y3, f = y0;
        return (u, v) =>
        {
            double z = g * u + hh * v + 1;
            return ((a * u + b * v + cc) / z, (d * u + e * v + f) / z);
        };
    }

    private void Place()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        if (_fullscreen)
        {
            int ex = GetWindowLong(hwnd, GWL_EXSTYLE);
            SetWindowLong(hwnd, GWL_EXSTYLE, ex | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);
            SetWindowPos(hwnd, HWND_TOPMOST, _monitor.Left, _monitor.Top, _monitor.Width, _monitor.Height, SWP_NOACTIVATE);
        }
        else if (!_placedWindowed)
        {
            _placedWindowed = true;
            var dpi = VisualTreeHelper.GetDpi(this);
            int w = (int)(Width * dpi.DpiScaleX), h = (int)(Height * dpi.DpiScaleY);
            int x = _monitor.Left + (_monitor.Width - w) / 2, y = _monitor.Top + (_monitor.Height - h) / 2;
            SetWindowPos(hwnd, IntPtr.Zero, x, y, w, h, SWP_NOACTIVATE | SWP_NOZORDER);
        }
    }

    private bool _placedWindowed;

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_NOZORDER = 0x0004;
    private static readonly IntPtr HWND_TOPMOST = new(-1);

    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int value);
    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
}
