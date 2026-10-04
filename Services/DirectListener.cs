using System.Net;
using System.Net.Sockets;
using System.Text;

namespace AdoxicSound.Services;

/// <summary>
/// Listens for direct Adoxic Sound pushes (no server) and relays the MP3
/// to the receiver engine through a localhost HTTP endpoint.
/// </summary>
public sealed class DirectListener : IDisposable
{
    private readonly Logger _log;
    private TcpListener? _listen;
    private CancellationTokenSource? _cts;
    private Task? _loop;

    private TcpListener? _relay;
    private int _relayPort;
    private TcpClient? _sender;
    private CancellationTokenSource? _pumpCts;
    private readonly object _gate = new();

    public event Action<string>? DirectConnected; // sender IP
    public event Action? DirectEnded;
    public Func<bool>? CanAccept; // false = engine busy, refuse sender

    public bool Running { get; private set; }
    public int Port { get; private set; }
    public string? SenderIp { get; private set; }

    public DirectListener(Logger log) => _log = log;

    public void Start(int port)
    {
        Stop();
        Port = Math.Clamp(port, 1, 65535);
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        _listen = new TcpListener(IPAddress.Any, Port);
        _listen.Start();
        Running = true;
        _log.Info($"Listening for direct pushes on :{Port}");
        _loop = Task.Run(() => AcceptLoop(ct));
    }

    public void Stop()
    {
        Running = false;
        SenderIp = null;
        try { _cts?.Cancel(); } catch { }
        _cts = null;
        try { _listen?.Stop(); } catch { }
        _listen = null;
        StopPump();
        try { _relay?.Stop(); } catch { }
        _relay = null;
    }

    public void StopPump()
    {
        lock (_gate)
        {
            try { _pumpCts?.Cancel(); } catch { }
            _pumpCts = null;
            try { _sender?.Close(); } catch { }
            _sender = null;
        }
    }

    /// <summary>Local URL the receiver engine plays while a sender is connected.</summary>
    public string? RelayUrl
    {
        get
        {
            lock (_gate) { return _relayPort > 0 ? $"http://127.0.0.1:{_relayPort}/direct" : null; }
        }
    }

    private async Task AcceptLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listen!.AcceptTcpClientAsync(ct); }
            catch { return; }
            _ = Task.Run(() => HandleSender(client, ct));
        }
    }

    private async Task HandleSender(TcpClient client, CancellationToken ct)
    {
        var ip = (client.Client.RemoteEndPoint as IPEndPoint)?.Address.ToString() ?? "?";
        try
        {
            var s = client.GetStream();
            s.ReadTimeout = 10000;
            var magic = new byte[5];
            int got = 0;
            while (got < 5)
            {
                int n = await s.ReadAsync(magic, got, 5 - got, ct);
                if (n <= 0) throw new Exception("no handshake");
                got += n;
            }
            if (magic[0] != 'A' || magic[1] != 'D' || magic[2] != 'X' || magic[3] != 'M')
                throw new Exception("not an Adoxic direct stream");
            if (magic[4] != 1) throw new Exception($"unsupported version {magic[4]}");
        }
        catch
        {
            try { client.Close(); } catch { }
            _log.RxWarn($"Rejected direct connection from {ip}");
            return;
        }

        int relayPort;
        lock (_gate)
        {
            if (_sender != null || (CanAccept != null && !CanAccept()))
            {
                try { client.Close(); } catch { }
                _log.RxWarn($"Direct sender {ip} refused (busy)");
                return;
            }
            _sender = client;
            SenderIp = ip;
            _relay = new TcpListener(IPAddress.Loopback, 0);
            _relay.Start();
            relayPort = ((IPEndPoint)_relay.LocalEndpoint).Port;
            _relayPort = relayPort;
            _pumpCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        }
        _log.RxInfo($"Direct stream from {ip}");
        DirectConnected?.Invoke(ip);
        var pumpCt = _pumpCts.Token;

        // serve localhost HTTP clients (the receiver engine, incl. reconnects)
        try
        {
            var senderAlive = true;
            while (!pumpCt.IsCancellationRequested && senderAlive)
            {
                TcpClient vlc;
                try { vlc = await _relay.AcceptTcpClientAsync(pumpCt); }
                catch { break; }
                using (vlc)
                {
                    var vs = vlc.GetStream();
                    var req = new byte[1024];
                    try { await vs.ReadAsync(req, 0, req.Length, pumpCt); } catch { continue; }
                    var hdr = Encoding.ASCII.GetBytes("HTTP/1.0 200 OK\r\nContent-Type: audio/mpeg\r\nConnection: close\r\n\r\n");
                    try { await vs.WriteAsync(hdr, pumpCt); } catch { continue; }
                    var net = client.GetStream();
                    var buf = new byte[16384];
                    while (!pumpCt.IsCancellationRequested)
                    {
                        int n;
                        try { n = await net.ReadAsync(buf, 0, buf.Length, pumpCt); }
                        catch { senderAlive = false; break; }
                        if (n <= 0) { senderAlive = false; break; }
                        try { await vs.WriteAsync(buf.AsMemory(0, n), pumpCt); }
                        catch { break; } // engine stopped listening; wait for its reconnect
                    }
                }
            }
        }
        catch { }
        finally
        {
            lock (_gate)
            {
                _relayPort = 0;
                try { _relay?.Stop(); } catch { }
                _relay = null;
                try { _sender?.Close(); } catch { }
                _sender = null;
                SenderIp = null;
            }
            _log.RxInfo("Direct stream ended");
            DirectEnded?.Invoke();
        }
    }

    public void Dispose() => Stop();
}
