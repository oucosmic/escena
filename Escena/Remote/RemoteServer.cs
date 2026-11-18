using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Threading;
using Escena.Model;

namespace Escena.Remote;

/// <summary>
/// Control remot des del mòbil: un servidor web petit dins d'Escena (sense IIS ni permisos d'administrador)
/// que serveix la pàgina del comandament i parla amb ella per WebSocket. Només per a la xarxa local i amb PIN.
/// L'estat (què sona, què és en espera) s'envia uns quants cops per segon; les ordres (GO, atura, pànic…)
/// s'executen al fil de la interfície, igual que si es premés la tecla.
/// </summary>
public sealed class RemoteServer : IDisposable
{
    private readonly ShowController _show;
    private readonly Dispatcher _dispatcher;
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private readonly DispatcherTimer _timer;
    private readonly List<Client> _clients = new();
    private readonly object _lock = new();
    private readonly Dictionary<IPAddress, (int Fails, DateTime Until)> _fails = new();
    private string _lastState = "", _lastCues = "";
    private static readonly string Page = LoadPage();

    public int Port { get; private set; }
    public string Pin { get; private set; } = "";
    public bool IsRunning => _listener != null;

    /// <summary>Mòbils connectats i amb el PIN correcte.</summary>
    public int ClientCount { get { lock (_lock) return _clients.Count(c => c.Authed); } }

    /// <summary>Canvia el nombre de mòbils connectats (es crida al fil de la interfície).</summary>
    public event Action? ClientsChanged;

    private sealed class Client
    {
        public required WebSocket Socket { get; init; }
        public required IPAddress Address { get; init; }
        public bool Authed;
        public readonly SemaphoreSlim SendLock = new(1, 1);
    }

    public RemoteServer(ShowController show)
    {
        _show = show;
        _dispatcher = Dispatcher.CurrentDispatcher;
        _timer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher) { Interval = TimeSpan.FromMilliseconds(150) };
        _timer.Tick += (_, _) => Broadcast(force: false);
    }

    public void Start(int port, string pin)
    {
        Stop();
        Port = port;
        Pin = pin;
        _cts = new CancellationTokenSource();
        var listener = new TcpListener(IPAddress.Any, port);
        listener.Start(); // pot llançar SocketException si el port ja és ocupat
        _listener = listener;
        _ = AcceptLoop(listener, _cts.Token);
        _timer.Start();
    }

    public void Stop()
    {
        _timer.Stop();
        _cts?.Cancel();
        try { _listener?.Stop(); } catch { }
        _listener = null;
        List<Client> clients;
        lock (_lock) { clients = _clients.ToList(); _clients.Clear(); }
        foreach (var c in clients) { try { c.Socket.Abort(); } catch { } }
        _lastState = _lastCues = "";
        ClientsChanged?.Invoke();
    }

    public void Dispose() => Stop();

    /// <summary>PIN nou de 4 xifres.</summary>
    public static string NewPin() => RandomNumberGenerator.GetInt32(0, 10000).ToString("0000");

    /// <summary>Adreces IPv4 d'aquest ordinador a la xarxa local (la primera és la que té porta d'enllaç: wifi o cable).</summary>
    public static List<string> LocalAddresses()
    {
        var list = new List<(string Ip, bool Gateway)>();
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up || ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            var props = ni.GetIPProperties();
            bool gateway = props.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any));
            foreach (var ua in props.UnicastAddresses)
                if (ua.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(ua.Address)
                    && !ua.Address.ToString().StartsWith("169.254."))
                    list.Add((ua.Address.ToString(), gateway));
        }
        return list.OrderByDescending(a => a.Gateway).Select(a => a.Ip).Distinct().ToList();
    }

    // ---------------- HTTP i WebSocket ----------------

    private async Task AcceptLoop(TcpListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient tcp;
            try { tcp = await listener.AcceptTcpClientAsync(ct); }
            catch { break; }
            _ = HandleConnection(tcp, ct);
        }
    }

    private async Task HandleConnection(TcpClient tcp, CancellationToken ct)
    {
        using var _ = tcp;
        tcp.NoDelay = true;
        var address = (tcp.Client.RemoteEndPoint as IPEndPoint)?.Address ?? IPAddress.None;
        try
        {
            var stream = tcp.GetStream();
            var (path, headers) = await ReadRequest(stream, ct);
            if (path == null) return;

            if (path.StartsWith("/ws") && headers.TryGetValue("sec-websocket-key", out var key))
            {
                string accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(key.Trim() + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
                await WriteAscii(stream, "HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n" +
                                         $"Sec-WebSocket-Accept: {accept}\r\n\r\n", ct);
                var ws = WebSocket.CreateFromStream(stream, new WebSocketCreationOptions { IsServer = true, KeepAliveInterval = TimeSpan.FromSeconds(10) });
                await RunClient(new Client { Socket = ws, Address = address }, ct);
                return;
            }

            string body;
            string type;
            if (path == "/" || path.StartsWith("/?") || path.StartsWith("/index"))
            {
                body = Page.Replace("{{LANG}}", L.Language);
                type = "text/html; charset=utf-8";
            }
            else if (path.StartsWith("/manifest.json"))
            {
                body = """{"name":"Escena","short_name":"Escena","start_url":"/","display":"standalone","background_color":"#1B1C20","theme_color":"#1B1C20"}""";
                type = "application/manifest+json";
            }
            else
            {
                await WriteAscii(stream, "HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\nConnection: close\r\n\r\n", ct);
                return;
            }
            var bytes = Encoding.UTF8.GetBytes(body);
            await WriteAscii(stream, $"HTTP/1.1 200 OK\r\nContent-Type: {type}\r\nContent-Length: {bytes.Length}\r\n" +
                                     "Cache-Control: no-store\r\nConnection: close\r\n\r\n", ct);
            await stream.WriteAsync(bytes, ct);
        }
        catch { /* connexió tallada: no passa res */ }
    }

    private static async Task<(string? Path, Dictionary<string, string> Headers)> ReadRequest(NetworkStream stream, CancellationToken ct)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var buffer = new byte[8192];
        int total = 0;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        while (total < buffer.Length)
        {
            int n = await stream.ReadAsync(buffer.AsMemory(total), timeout.Token);
            if (n == 0) return (null, headers);
            total += n;
            string text = Encoding.ASCII.GetString(buffer, 0, total);
            int end = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            if (end < 0) continue;
            var lines = text[..end].Split("\r\n");
            var first = lines[0].Split(' ');
            if (first.Length < 2 || first[0] != "GET") return (null, headers);
            foreach (var line in lines.Skip(1))
            {
                int colon = line.IndexOf(':');
                if (colon > 0) headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
            }
            return (first[1], headers);
        }
        return (null, headers);
    }

    private static Task WriteAscii(NetworkStream stream, string text, CancellationToken ct) =>
        stream.WriteAsync(Encoding.ASCII.GetBytes(text), ct).AsTask();

    private async Task RunClient(Client client, CancellationToken ct)
    {
        lock (_lock) _clients.Add(client);
        var buffer = new byte[4096];
        try
        {
            while (client.Socket.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                var sb = new StringBuilder();
                WebSocketReceiveResult r;
                do
                {
                    r = await client.Socket.ReceiveAsync(buffer, ct);
                    if (r.MessageType == WebSocketMessageType.Close) return;
                    sb.Append(Encoding.UTF8.GetString(buffer, 0, r.Count));
                    if (sb.Length > 16384) return;
                } while (!r.EndOfMessage);
                await OnMessage(client, sb.ToString());
            }
        }
        catch { /* el mòbil ha marxat */ }
        finally
        {
            bool wasAuthed;
            lock (_lock) { _clients.Remove(client); wasAuthed = client.Authed; }
            try { client.Socket.Abort(); } catch { }
            if (wasAuthed) _ = _dispatcher.InvokeAsync(() =>
            {
                _show.Status = string.Format(L.T("Mòbil desconnectat ({0})."), client.Address);
                ClientsChanged?.Invoke();
            });
        }
    }

    private async Task OnMessage(Client client, string text)
    {
        JsonNode? msg;
        try { msg = JsonNode.Parse(text); } catch { return; }
        if (msg is not JsonObject o) return;

        if (!client.Authed)
        {
            string pin = o["pin"]?.GetValue<string>() ?? "";
            bool locked;
            lock (_lock) locked = _fails.TryGetValue(client.Address, out var f) && f.Until > DateTime.Now;
            if (!locked && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(pin), Encoding.UTF8.GetBytes(Pin)))
            {
                client.Authed = true;
                lock (_lock) _fails.Remove(client.Address);
                await Send(client, """{"type":"auth","ok":true}""");
                await _dispatcher.InvokeAsync(() =>
                {
                    _show.Status = string.Format(L.T("Mòbil connectat ({0})."), client.Address);
                    ClientsChanged?.Invoke();
                    Broadcast(force: true);
                });
            }
            else
            {
                lock (_lock)
                {
                    var f = _fails.GetValueOrDefault(client.Address);
                    f.Fails++;
                    if (f.Fails >= 5) { f.Until = DateTime.Now.AddSeconds(30); f.Fails = 0; }
                    _fails[client.Address] = f;
                }
                await Send(client, JsonSerializer.Serialize(new { type = "auth", ok = false, locked }));
            }
            return;
        }

        string cmd = o["cmd"]?.GetValue<string>() ?? "";
        string? id = o["id"]?.GetValue<string>();
        int delta = o["delta"]?.GetValue<int>() ?? 0;
        double value = 0;
        try { value = o["value"]?.GetValue<double>() ?? 0; } catch { return; }
        await _dispatcher.InvokeAsync(() => Execute(cmd, id, delta, value));
    }

    /// <summary>Executa una ordre del mòbil (al fil de la interfície).</summary>
    public void Execute(string cmd, string? id = null, int delta = 0, double value = 0)
    {
        switch (cmd)
        {
            case "go": _show.Go(); break;
            case "stopAll": _show.StopAllPressed(); break;
            case "panic": _show.Panic(); break;
            case "pauseAll": _show.TogglePauseAll(); break;
            case "standbyMove": _show.MoveStandby(Math.Sign(delta)); break;
            case "master":
                // Volum general en dB (el mateix control que la barra d'Escena: de −60 a +6).
                if (double.IsFinite(value)) _show.MasterVolumeDb = Math.Round(value, 1);
                break;
            case "standby":
                if (Guid.TryParse(id, out var g) && _show.Cues.FirstOrDefault(c => c.Id == g) is { } cue) _show.StandbyCue = cue;
                break;
            default: return;
        }
        Broadcast(force: true);
    }

    // ---------------- Estat ----------------

    private async Task Send(Client c, string json)
    {
        if (c.Socket.State != WebSocketState.Open) return;
        await c.SendLock.WaitAsync();
        try { await c.Socket.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, CancellationToken.None); }
        catch { }
        finally { c.SendLock.Release(); }
    }

    /// <summary>Envia l'estat (i la llista de cues, si ha canviat) a tots els mòbils. Es crida al fil de la interfície.</summary>
    private void Broadcast(bool force)
    {
        List<Client> targets;
        lock (_lock) targets = _clients.Where(c => c.Authed).ToList();
        if (targets.Count == 0) return;

        string cues = BuildCues();
        string state = BuildState();
        bool cuesChanged = force || cues != _lastCues, stateChanged = force || state != _lastState;
        _lastCues = cues;
        _lastState = state;
        foreach (var c in targets)
        {
            if (cuesChanged) _ = Send(c, cues);
            if (stateChanged) _ = Send(c, state);
        }
    }

    private string BuildCues()
    {
        var list = _show.Cues.Where(c => !c.IsHiddenByGroup).Select(c => new
        {
            id = c.Id.ToString(),
            num = c.Number,
            name = c.Name,
            type = c.TypeName,
            depth = c.Depth,
            color = CueLook.ColorCode(c),
        });
        return JsonSerializer.Serialize(new { type = "cues", cues = list });
    }

    private string BuildState()
    {
        var standby = _show.StandbyCue;
        var active = _show.ActiveCues.Select(c => new
        {
            id = c.Id.ToString(),
            name = c.DisplayName,
            time = TimeText(c),
            progress = Math.Round(c.Progress, 1),
            paused = c.IsPaused,
        }).ToList();
        return JsonSerializer.Serialize(new
        {
            type = "state",
            show = _show.FilePath != null ? Path.GetFileNameWithoutExtension(_show.FilePath) : L.T("Sense títol"),
            showMode = _show.IsShowMode,
            paused = _show.IsAllPaused,
            master = Math.Round(_show.MasterVolumeDb, 1),
            standby = standby == null ? null : new
            {
                id = standby.Id.ToString(),
                name = standby.DisplayName,
                notes = standby.Notes,
                type = standby.TypeName,
            },
            active,
        });
    }

    private static string TimeText(Cue c) => c switch
    {
        { State: CueState.PreWait } => string.Format(L.T("espera {0:0.0} s"), Math.Max(0, c.PreWait - c.WaitElapsed)),
        { Duration: > 0 } => "−" + TimeFormat.Format(Math.Max(0, c.Duration - c.Elapsed)),
        _ => TimeFormat.Format(c.Elapsed),
    };

    private static string LoadPage()
    {
        using var s = typeof(RemoteServer).Assembly.GetManifestResourceStream("Escena.Remote.remote.html");
        if (s == null) return "<h1>Escena</h1>";
        using var r = new StreamReader(s, Encoding.UTF8);
        return r.ReadToEnd();
    }
}
