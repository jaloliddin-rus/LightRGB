using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using LightRGB.Core;
using LightRGB.Effects;
using LightRGB.Tray;

namespace LightRGB.Web;

public sealed class WebUiServer : IDisposable
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private readonly DeviceManager _manager;
    private readonly string _wwwrootDir;
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private Thread? _acceptThread;

    public WebUiServer(DeviceManager manager, int port, string? wwwrootDir = null)
    {
        _manager = manager;
        _wwwrootDir = wwwrootDir ?? Path.Combine(AppContext.BaseDirectory, "wwwroot");
        _listener = new TcpListener(IPAddress.Loopback, port);
    }

    public int Port { get; private set; }
    public string BaseUrl => $"http://127.0.0.1:{Port}/";

    public void Start()
    {
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _acceptThread = new Thread(AcceptLoop)
        {
            IsBackground = true,
            Name = "LightRGB-WebUI"
        };
        _acceptThread.Start();
    }

    private void AcceptLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                var client = _listener.AcceptTcpClient();
                ThreadPool.QueueUserWorkItem(_ => HandleClient(client));
            }
            catch (SocketException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch
            {
                // Ignore transient accept errors
            }
        }
    }

    private void HandleClient(TcpClient client)
    {
        using (client)
        {
            try
            {
                client.NoDelay = true;
                client.ReceiveTimeout = 5000;
                client.SendTimeout = 5000;
                using var stream = client.GetStream();

                var (method, path, body) = ReadHttpRequest(stream);
                if (string.IsNullOrEmpty(method))
                    return;

                string cleanPath = path.Split('?', 2)[0];
                Route(stream, method, cleanPath, body);
            }
            catch
            {
                // Ignore broken client sockets
            }
        }
    }

    private void Route(NetworkStream stream, string method, string path, string body)
    {
        if (method == "OPTIONS")
        {
            SendResponse(stream, 204, "text/plain", []);
            return;
        }

        if (method == "GET" && (path == "/" || path == "/index.html"))
        {
            string htmlPath = Path.Combine(_wwwrootDir, "index.html");
            byte[] htmlBytes = File.Exists(htmlPath)
                ? File.ReadAllBytes(htmlPath)
                : Encoding.UTF8.GetBytes("<h1>LightRGB</h1><p>wwwroot/index.html not found.</p>");
            SendResponse(stream, 200, "text/html; charset=utf-8", htmlBytes);
            return;
        }

        if (method == "GET" && path == "/api/status")
        {
            SendJson(stream, 200, _manager.GetStatusSnapshot());
            return;
        }

        if (method == "GET" && path == "/api/canvas")
        {
            var frame = _manager.Canvas.GetDownsampledFrame(16, 10);
            SendJson(stream, 200, new
            {
                width = 16,
                height = 10,
                rgbBase64 = Convert.ToBase64String(frame)
            });
            return;
        }

        if (method == "POST" && path == "/api/effect")
        {
            var req = JsonSerializer.Deserialize<EffectSettings>(body, JsonOpts);
            if (req is null)
            {
                SendJson(stream, 400, new { error = "Invalid JSON" });
                return;
            }
            _manager.UpdateEffect(req);
            SendJson(stream, 200, new { ok = true, effect = _manager.Canvas.GetSettingsSnapshot() });
            return;
        }

        if (method == "POST" && path == "/api/settings")
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
            var root = doc.RootElement;
            int? fps = root.TryGetProperty("targetFps", out var fEl) && fEl.TryGetInt32(out int fv) ? fv : null;
            bool? skip = root.TryGetProperty("skipShutdownBlackout", out var sEl) && (sEl.ValueKind == JsonValueKind.True || sEl.ValueKind == JsonValueKind.False)
                ? sEl.GetBoolean()
                : null;
            bool? paused = root.TryGetProperty("paused", out var pEl) && (pEl.ValueKind == JsonValueKind.True || pEl.ValueKind == JsonValueKind.False)
                ? pEl.GetBoolean()
                : null;
            bool? startupApplyAndExit = root.TryGetProperty("startupApplyAndExit", out var saeEl) && (saeEl.ValueKind == JsonValueKind.True || saeEl.ValueKind == JsonValueKind.False)
                ? saeEl.GetBoolean()
                : null;
            if (fps.HasValue || skip.HasValue || paused.HasValue || startupApplyAndExit.HasValue)
                _manager.UpdateGlobalSettings(fps, skip, paused, startupApplyAndExit);
            if (root.TryGetProperty("syncAllBrightness", out var sabEl) && (sabEl.ValueKind == JsonValueKind.True || sabEl.ValueKind == JsonValueKind.False))
                _manager.SetAllDevicesSyncBrightness(sabEl.GetBoolean());
            SendJson(stream, 200, new { ok = true });
            return;
        }

        if (method == "POST" && path.StartsWith("/api/devices/", StringComparison.OrdinalIgnoreCase))
        {
            string devId = Uri.UnescapeDataString(path["/api/devices/".Length..]);
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
            var root = doc.RootElement;

            DeviceRunMode? mode = null;
            if (root.TryGetProperty("mode", out var mEl) && Enum.TryParse<DeviceRunMode>(mEl.GetString(), true, out var parsedMode))
                mode = parsedMode;

            string? staticColor = root.TryGetProperty("staticColor", out var scEl) ? scEl.GetString() : null;
            int? brightness = root.TryGetProperty("brightness", out var bEl) && bEl.TryGetInt32(out int bv) ? bv : null;
            bool? syncBrightness = root.TryGetProperty("syncBrightness", out var sbEl) && (sbEl.ValueKind == JsonValueKind.True || sbEl.ValueKind == JsonValueKind.False)
                ? sbEl.GetBoolean()
                : null;
            float? cx = root.TryGetProperty("canvasX", out var cxEl) && cxEl.TryGetSingle(out float cxv) ? cxv : null;
            float? cy = root.TryGetProperty("canvasY", out var cyEl) && cyEl.TryGetSingle(out float cyv) ? cyv : null;

            Dictionary<string, int>? channelCounts = null;
            if (root.TryGetProperty("channelLedCounts", out var chEl) && chEl.ValueKind == JsonValueKind.Object)
            {
                channelCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                foreach (var prop in chEl.EnumerateObject())
                {
                    if (prop.Value.TryGetInt32(out int cnt))
                        channelCounts[prop.Name] = cnt;
                }
            }

            Dictionary<string, JsonElement>? pluginParams = null;
            if (root.TryGetProperty("pluginParams", out var ppEl) && ppEl.ValueKind == JsonValueKind.Object)
            {
                pluginParams = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
                foreach (var prop in ppEl.EnumerateObject())
                    pluginParams[prop.Name] = prop.Value.Clone();
            }

            _manager.UpdateDevice(devId, mode, staticColor, cx, cy, channelCounts, pluginParams, brightness, syncBrightness);
            SendJson(stream, 200, new { ok = true });
            return;
        }

        if (method == "POST" && path == "/api/rescan")
        {
            bool started = _manager.RescanDevicesAsync(includeSmbus: true);
            SendJson(stream, 200, new { ok = true, started });
            return;
        }

        if (method == "POST" && path == "/api/startup")
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
            bool enabled = doc.RootElement.TryGetProperty("enabled", out var eEl)
                ? eEl.GetBoolean()
                : !StartupManager.IsEnabled();
            bool applyAndExit = _manager.Config.GetSnapshot().StartupApplyAndExit;
            bool ok = StartupManager.SetEnabled(enabled, _manager.DataDir, applyAndExit);
            SendJson(stream, 200, new
            {
                ok,
                startupEnabled = StartupManager.IsEnabled(),
                startupMode = StartupManager.GetStatusMode()
            });
            return;
        }

        SendJson(stream, 404, new { error = "Not found" });
    }

    private static (string Method, string Path, string Body) ReadHttpRequest(NetworkStream stream)
    {
        var buf = new byte[8192];
        using var ms = new MemoryStream();
        int headerEnd = -1;

        while (headerEnd < 0)
        {
            int read = stream.Read(buf, 0, buf.Length);
            if (read <= 0) return ("", "", "");
            ms.Write(buf, 0, read);
            if (ms.Length > 1024 * 1024) return ("", "", "");

            byte[] current = ms.GetBuffer();
            headerEnd = FindHeaderEnd(current, (int)ms.Length);
        }

        byte[] raw = ms.GetBuffer();
        int totalLen = (int)ms.Length;
        string headerText = Encoding.ASCII.GetString(raw, 0, headerEnd);
        string[] lines = headerText.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length == 0) return ("", "", "");

        string[] reqParts = lines[0].Split(' ', 3);
        if (reqParts.Length < 2) return ("", "", "");

        string method = reqParts[0].ToUpperInvariant();
        string path = reqParts[1];

        int contentLength = 0;
        for (int i = 1; i < lines.Length; i++)
        {
            int colon = lines[i].IndexOf(':');
            if (colon > 0 && lines[i][..colon].Trim().Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
            {
                int.TryParse(lines[i][(colon + 1)..].Trim(), out contentLength);
            }
        }

        int bodyStart = headerEnd + 4;
        int alreadyHave = totalLen - bodyStart;
        if (contentLength > alreadyHave)
        {
            int remaining = contentLength - alreadyHave;
            var extra = new byte[remaining];
            int pos = 0;
            while (pos < remaining)
            {
                int r = stream.Read(extra, pos, remaining - pos);
                if (r <= 0) break;
                pos += r;
            }
            ms.Write(extra, 0, pos);
        }

        string body = contentLength > 0
            ? Encoding.UTF8.GetString(ms.GetBuffer(), bodyStart, Math.Min(contentLength, (int)ms.Length - bodyStart))
            : "";

        return (method, path, body);
    }

    private static int FindHeaderEnd(byte[] buf, int len)
    {
        for (int i = 0; i <= len - 4; i++)
        {
            if (buf[i] == '\r' && buf[i + 1] == '\n' && buf[i + 2] == '\r' && buf[i + 3] == '\n')
                return i;
        }
        return -1;
    }

    private static void SendJson(NetworkStream stream, int statusCode, object payload)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(payload, JsonOpts);
        SendResponse(stream, statusCode, "application/json; charset=utf-8", bytes);
    }

    private static void SendResponse(NetworkStream stream, int statusCode, string contentType, byte[] body)
    {
        string statusText = statusCode switch
        {
            200 => "OK",
            204 => "No Content",
            400 => "Bad Request",
            404 => "Not Found",
            _ => "OK"
        };

        string headers =
            $"HTTP/1.1 {statusCode} {statusText}\r\n" +
            $"Content-Type: {contentType}\r\n" +
            $"Content-Length: {body.Length}\r\n" +
            "Access-Control-Allow-Origin: *\r\n" +
            "Access-Control-Allow-Methods: GET, POST, OPTIONS\r\n" +
            "Access-Control-Allow-Headers: Content-Type\r\n" +
            "Connection: close\r\n\r\n";

        byte[] headerBytes = Encoding.ASCII.GetBytes(headers);
        byte[] packet = new byte[headerBytes.Length + body.Length];
        Buffer.BlockCopy(headerBytes, 0, packet, 0, headerBytes.Length);
        if (body.Length > 0)
            Buffer.BlockCopy(body, 0, packet, headerBytes.Length, body.Length);
        stream.Write(packet, 0, packet.Length);
        stream.Flush();
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _listener.Stop(); } catch { }
        try { _acceptThread?.Join(1000); } catch { }
        _cts.Dispose();
    }
}
