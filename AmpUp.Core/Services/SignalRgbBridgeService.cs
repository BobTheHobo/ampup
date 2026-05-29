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

    public static string UserPluginDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "WhirlwindFX", "Plugins");

    public static string UserPluginPath => Path.Combine(UserPluginDirectory, "AmpUp_TurnUp_Bridge.js");

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

    public static string InstallUserPlugin()
    {
        var source = BundledPluginPath;
        if (!File.Exists(source))
            throw new FileNotFoundException("SignalRGB plugin template was not found.", source);

        Directory.CreateDirectory(UserPluginDirectory);
        File.Copy(source, UserPluginPath, overwrite: true);
        return UserPluginPath;
    }

    public void Dispose()
    {
        Stop();
    }
}
