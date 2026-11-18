using System.Net.Sockets;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Escena.Remote;

/// <summary>Finestra del control remot: activar-lo, el codi QR per obrir-lo al mòbil, el PIN i els mòbils connectats.</summary>
public sealed class RemoteWindow : Window
{
    private readonly RemoteServer _server;
    private readonly ShowController _show;
    private readonly CheckBox _enabled = new() { Content = "Activa el control remot", FontSize = 14 };
    private readonly StackPanel _details = new();
    private readonly Image _qr = new() { Width = 220, Height = 220, Margin = new Thickness(0, 0, 16, 0) };
    private readonly TextBox _url = new() { IsReadOnly = true, FontSize = 16, BorderThickness = new Thickness(0), Background = Brushes.Transparent };
    private readonly TextBlock _pin = new() { FontSize = 30, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 2, 0, 0) };
    private readonly TextBlock _others = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Thickness(0, 6, 0, 0) };
    private readonly TextBlock _clients = new() { FontSize = 14, Margin = new Thickness(0, 10, 0, 0) };
    private readonly TextBox _port = new() { Width = 80, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly TextBlock _error = new() { TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x8A, 0x7A)), Margin = new Thickness(0, 8, 0, 0) };
    private bool _loading;

    public RemoteWindow(RemoteServer server, ShowController show)
    {
        _server = server;
        _show = show;
        Ui.StyleWindow(this, "Control remot des del mòbil", 600);
        ResizeMode = ResizeMode.NoResize;
        _others.Foreground = Ui.Brush("MutedBrush");
        _url.Foreground = _pin.Foreground = _clients.Foreground = Ui.Brush("TextBrush");

        var root = new StackPanel { Margin = new Thickness(16) };
        root.Children.Add(Ui.Header("CONTROL REMOT DES DEL MÒBIL"));
        root.Children.Add(Ui.Note("Fes GO, atura i tria la cue en espera des del mòbil o la tauleta, amb el navegador. El mòbil ha d'estar a la mateixa wifi que aquest ordinador. No cal instal·lar res."));
        _enabled.Margin = new Thickness(0, 12, 0, 8);
        root.Children.Add(_enabled);

        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(new Border { Background = Brushes.White, Child = _qr, Padding = new Thickness(0), VerticalAlignment = VerticalAlignment.Top });
        var info = new StackPanel { Width = 320 };
        info.Children.Add(Ui.Label("1. Llegeix el codi amb la càmera del mòbil, o escriu aquesta adreça al navegador:"));
        info.Children.Add(_url);
        info.Children.Add(Ui.Label("2. PIN (amb el codi QR no cal escriure'l):"));
        info.Children.Add(_pin);
        info.Children.Add(_others);
        info.Children.Add(_clients);
        row.Children.Add(info);
        _details.Children.Add(row);
        _details.Children.Add(Ui.Note("Al Chrome del mòbil, «Afegeix a la pantalla d'inici» hi posa una icona d'Escena. Si Windows pregunta pel tallafocs, permet l'accés a les xarxes privades. Per a la funció, la tecla d'espai de l'ordinador continua sent la manera més segura: si la wifi falla, el mòbil ho diu en vermell."));
        var portRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
        portRow.Children.Add(new TextBlock { Text = "Port:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0), Foreground = Ui.Brush("MutedBrush") });
        portRow.Children.Add(_port);
        var applyPort = Ui.Button("Aplica", (_, _) => ApplyPort(), 80);
        applyPort.Margin = new Thickness(6, 0, 0, 0);
        portRow.Children.Add(applyPort);
        var newPin = Ui.Button("PIN nou", (_, _) => NewPin(), 90);
        newPin.Margin = new Thickness(16, 0, 0, 0);
        portRow.Children.Add(newPin);
        _details.Children.Add(portRow);
        root.Children.Add(_details);
        root.Children.Add(_error);
        root.Children.Add(Ui.Buttons(Ui.Button("Tanca", (_, _) => Close(), 100)));
        Content = root;

        _loading = true;
        _enabled.IsChecked = _server.IsRunning;
        _port.Text = _show.MachineSettings.RemotePort.ToString();
        _loading = false;
        _enabled.Checked += (_, _) => Toggle(true);
        _enabled.Unchecked += (_, _) => Toggle(false);
        _server.ClientsChanged += Refresh;
        Closed += (_, _) => _server.ClientsChanged -= Refresh;
        Refresh();
    }

    private void Toggle(bool on)
    {
        if (_loading) return;
        _show.MachineSettings.RemoteEnabled = on;
        _show.MachineSettings.Save();
        if (on) StartServer(); else _server.Stop();
        Refresh();
    }

    private void StartServer()
    {
        _error.Text = "";
        var s = _show.MachineSettings;
        if (string.IsNullOrEmpty(s.RemotePin)) { s.RemotePin = RemoteServer.NewPin(); s.Save(); }
        try { _server.Start(s.RemotePort, s.RemotePin); }
        catch (SocketException ex)
        {
            _error.Text = string.Format(L.T("No s'ha pogut obrir el port {0}: {1} Prova'n un altre."), s.RemotePort, ex.Message);
            _loading = true;
            _enabled.IsChecked = false;
            _loading = false;
        }
    }

    private void ApplyPort()
    {
        if (!int.TryParse(_port.Text.Trim(), out int port) || port is < 1024 or > 65535)
        {
            _error.Text = L.T("El port ha de ser un número entre 1024 i 65535.");
            return;
        }
        _show.MachineSettings.RemotePort = port;
        _show.MachineSettings.Save();
        if (_server.IsRunning) StartServer();
        Refresh();
    }

    private void NewPin()
    {
        _show.MachineSettings.RemotePin = RemoteServer.NewPin();
        _show.MachineSettings.Save();
        if (_server.IsRunning) StartServer(); // els mòbils connectats hauran d'escriure el PIN nou
        Refresh();
    }

    private void Refresh()
    {
        bool on = _server.IsRunning;
        _details.IsEnabled = on;
        _details.Opacity = on ? 1 : 0.35;
        var addresses = RemoteServer.LocalAddresses();
        string host = addresses.FirstOrDefault() ?? "localhost";
        int port = on ? _server.Port : _show.MachineSettings.RemotePort;
        string url = $"http://{host}:{port}/";
        _url.Text = url;
        _pin.Text = on ? _server.Pin : "····";
        _qr.Source = on ? QrCode.Encode($"{url}#pin={_server.Pin}").ToBitmap(6) : null;
        _others.Text = addresses.Count > 1
            ? L.T("Si no hi arriba, prova amb una altra adreça d'aquest ordinador:") + " " + string.Join(", ", addresses.Skip(1).Select(a => $"http://{a}:{port}/"))
            : addresses.Count == 0 ? L.T("Aquest ordinador no sembla connectat a cap xarxa.") : "";
        int n = _server.ClientCount;
        _clients.Text = !on ? "" : n == 0 ? L.T("Cap mòbil connectat.") : string.Format(L.T("Mòbils connectats: {0}"), n);
    }
}
