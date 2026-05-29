using System.Net;
using System.Net.Sockets;
using AmpUp.Core.Models;

namespace AmpUp.Core.Services;

/// <summary>
/// Local UDP bridge used by the SignalRGB user plugin. SignalRGB renders the
/// canvas, sends 15 RGB pixels to localhost, and AmpUp keeps ownership of the
/// Turn Up serial port.
/// </summary>
public sealed class SignalRgbBridgeService : IDisposable
{
    public const int DefaultPort = 45333;
    public const int LedCount = 15;
    public const int FrameLength = LedCount * 3;
    private static readonly byte[] Magic = [(byte)'A', (byte)'U', (byte)'P', (byte)'1'];

    private readonly object _gate = new();
    private SignalRgbConfig _config;
    private UdpClient? _udp;
    private CancellationTokenSource? _cts;
    private Task? _listenTask;
    private Timer? _timeoutTimer;
    private int _framesReceived;
    private bool _hasActiveFrame;
    private int _port;

    public SignalRgbBridgeService(SignalRgbConfig config)
    {
        _config = config;
    }

    public bool IsRunning { get; private set; }
    public int Port => _port;
    public int FramesReceived => Volatile.Read(ref _framesReceived);
    public DateTime LastFrameUtc { get; private set; } = DateTime.MinValue;
    public bool HasActiveFrame => _hasActiveFrame;

    public event Action<byte[]>? FrameReceived;
    public event Action? FrameTimedOut;
    public event Action<string>? StatusChanged;

    public void UpdateConfig(SignalRgbConfig config)
    {
        _config = config;
        if (!config.Enabled)
        {
            Stop();
            return;
        }

        Start(config.BridgePort);
    }

    public void Start(int port)
    {
        port = NormalizePort(port);
        lock (_gate)
        {
            if (IsRunning && _port == port) return;

            StopLocked();
            _port = port;
            _cts = new CancellationTokenSource();
            try
            {
                _udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, port));
                IsRunning = true;
                _listenTask = Task.Run(() => ListenLoopAsync(_udp, _cts.Token));
                _timeoutTimer = new Timer(_ => CheckForTimeout(), null, 500, 500);
                Logger.Log($"SignalRGB bridge listening on 127.0.0.1:{port}");
                StatusChanged?.Invoke($"Listening on {port}");
            }
            catch (Exception ex)
            {
                StopLocked();
                Logger.Log($"SignalRGB bridge failed to start on {port}: {ex.Message}");
                StatusChanged?.Invoke($"Failed: {ex.Message}");
            }
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            StopLocked();
        }
    }

    private void StopLocked()
    {
        bool hadActiveFrame = _hasActiveFrame;
        IsRunning = false;
        _hasActiveFrame = false;

        try { _timeoutTimer?.Dispose(); } catch { }
        _timeoutTimer = null;
        try { _cts?.Cancel(); } catch { }
        try { _udp?.Dispose(); } catch { }
        try { _cts?.Dispose(); } catch { }
        _udp = null;
        _cts = null;
        _listenTask = null;

        if (hadActiveFrame)
            FrameTimedOut?.Invoke();

        StatusChanged?.Invoke("Disabled");
    }

    private async Task ListenLoopAsync(UdpClient udp, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var result = await udp.ReceiveAsync(ct);
                if (TryParseFrame(result.Buffer, out var frame))
                {
                    LastFrameUtc = DateTime.UtcNow;
                    Interlocked.Increment(ref _framesReceived);
                    if (!_hasActiveFrame)
                    {
                        _hasActiveFrame = true;
                        Logger.Log("SignalRGB bridge receiving frames");
                        StatusChanged?.Invoke("Receiving frames");
                    }
                    FrameReceived?.Invoke(frame);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (Exception ex)
            {
                Logger.Log($"SignalRGB bridge receive error: {ex.Message}");
            }
        }
    }

    private void CheckForTimeout()
    {
        if (!_hasActiveFrame) return;
        if ((DateTime.UtcNow - LastFrameUtc).TotalMilliseconds < 1500) return;

        _hasActiveFrame = false;
        Logger.Log("SignalRGB bridge frame timeout; returning to AmpUp lighting");
        FrameTimedOut?.Invoke();
        StatusChanged?.Invoke("Listening");
    }

    private static bool TryParseFrame(byte[] packet, out byte[] frame)
    {
        frame = [];
        if (packet.Length < 5) return false;
        for (int i = 0; i < Magic.Length; i++)
            if (packet[i] != Magic[i]) return false;

        int count = packet[4];
        if (count <= 0) return false;
        int rgbBytes = count * 3;
        if (packet.Length < 5 + rgbBytes) return false;

        frame = new byte[FrameLength];
        Buffer.BlockCopy(packet, 5, frame, 0, Math.Min(FrameLength, rgbBytes));
        return true;
    }

    private static int NormalizePort(int port) => port is >= 1024 and <= 65535 ? port : DefaultPort;

    private const string PluginFileName = "AmpUp_TurnUp_Bridge.js";

    public static string UserPluginDirectory => ActiveSignalRgbPluginDirectory ?? LegacyUserPluginDirectory;

    public static string UserPluginPath => Path.Combine(UserPluginDirectory, PluginFileName);

    public static string LegacyUserPluginDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "WhirlwindFX", "Plugins");

    public static string LegacyUserPluginPath => Path.Combine(LegacyUserPluginDirectory, PluginFileName);

    private static string? ActiveSignalRgbPluginDirectory
    {
        get
        {
            var root = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "VortxEngine");

            if (!Directory.Exists(root)) return null;

            var appDir = Directory.GetDirectories(root, "app-*")
                .Select(path => new DirectoryInfo(path))
                .Where(dir => Directory.Exists(Path.Combine(dir.FullName, "Signal-x64", "Plugins")))
                .OrderByDescending(dir => dir.LastWriteTimeUtc)
                .FirstOrDefault();

            return appDir == null
                ? null
                : Path.Combine(appDir.FullName, "Signal-x64", "Plugins", "AmpUp");
        }
    }

    public static string BundledPluginPath
    {
        get
        {
            var baseDir = AppContext.BaseDirectory;
            var candidates = new[]
            {
                Path.Combine(baseDir, "integrations", "signalrgb", "AmpUp_TurnUp_Bridge.js"),
                Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "integrations", "signalrgb", "AmpUp_TurnUp_Bridge.js")),
                Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", "integrations", "signalrgb", "AmpUp_TurnUp_Bridge.js")),
            };

            return candidates.FirstOrDefault(File.Exists) ?? candidates[0];
        }
    }

    public static string InstallUserPlugin(SignalRgbConfig? config = null)
    {
        var source = BundledPluginPath;
        if (!File.Exists(source))
            throw new FileNotFoundException("SignalRGB plugin template was not found.", source);

        string content = File.ReadAllText(source);
        string canvasShape = NormalizeCanvasShape(config?.CanvasShape);
        content = content.Replace(
            "const ampUpCanvasShape = \"Classic Strip\";",
            $"const ampUpCanvasShape = \"{canvasShape}\";");

        Directory.CreateDirectory(UserPluginDirectory);
        File.WriteAllText(UserPluginPath, content);

        if (!string.Equals(UserPluginPath, LegacyUserPluginPath, StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                if (File.Exists(LegacyUserPluginPath))
                    File.Delete(LegacyUserPluginPath);
            }
            catch (Exception ex)
            {
                Logger.Log($"SignalRGB bridge could not remove legacy plugin copy: {ex.Message}");
            }
        }

        return UserPluginPath;
    }

    private static string NormalizeCanvasShape(string? canvasShape) => canvasShape switch
    {
        "Knob Grid" => "Knob Grid",
        "Arc" => "Arc",
        "Matrix" => "Matrix",
        "Wide Strip" => "Wide Strip",
        _ => "Classic Strip",
    };

    public void Dispose()
    {
        Stop();
    }
}
