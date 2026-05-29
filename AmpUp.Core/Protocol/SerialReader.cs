using System.IO.Ports;
using AmpUp.Core;

namespace AmpUp.Core.Protocol;

public class KnobEvent { public int Idx; public int Value; public bool IsBatch; }
public class ButtonEvent { public int Idx; public bool IsDown; }

public class SerialReader : IDisposable
{
    private SerialPort? _port;
    private readonly string _portName;
    private readonly int _baud;
    private readonly List<byte> _buf = new();
    private CancellationTokenSource _cts = new();
    private bool _running;
    private int _connectionState;
    private int _reconnectRequested;
    private long _lastReadUtcTicks;

    // Jitter deadzone: only fire OnKnob if value changed by >= this many ADC counts
    private const int JitterDeadzone = 5;
    private static readonly TimeSpan ReadStallTimeout = TimeSpan.FromSeconds(5);
    private readonly int[] _lastFiredValues = { -1, -1, -1, -1, -1 };

    public event Action<KnobEvent>? OnKnob;
    public event Action<ButtonEvent>? OnButton;
    public event Action<bool>? OnConnectionChanged;

    /// <summary>The underlying serial port, available after connection for RGB writes.</summary>
    public SerialPort? Port => _port;
    public DateTime LastReadUtc
    {
        get
        {
            long ticks = System.Threading.Interlocked.Read(ref _lastReadUtcTicks);
            return ticks > 0 ? new DateTime(ticks, DateTimeKind.Utc) : DateTime.MinValue;
        }
    }

    public SerialReader(string portName, int baud)
    {
        _portName = portName;
        _baud = baud;
    }

    public void Start()
    {
        _running = true;
        var oldCts = _cts;
        _cts = new CancellationTokenSource();
        oldCts.Dispose();
        Task.Run(() => ConnectLoop(_cts.Token));
    }

    /// <summary>
    /// Forces the current serial handle to close so the connect loop can reopen it.
    /// CH343 handles can survive sleep/resume while writes stop reaching the device.
    /// </summary>
    public void RequestReconnect(string reason)
    {
        if (!_running) return;

        System.Threading.Interlocked.Exchange(ref _reconnectRequested, 1);
        Logger.Log($"Serial reconnect requested: {reason}");
        NotifyConnectionChanged(false);
        CloseCurrentPort();
    }

    private async Task ConnectLoop(CancellationToken ct)
    {
        while (_running && !ct.IsCancellationRequested)
        {
            try
            {
                // Try configured port first, then auto-scan all ports
                string? portName = await FindDevicePort(ct);
                if (portName == null)
                {
                    if (_running && !ct.IsCancellationRequested)
                        await Task.Delay(2000, ct).ContinueWith(_ => { });
                    continue;
                }

                System.Threading.Interlocked.Exchange(ref _reconnectRequested, 0);
                var port = new SerialPort(portName, _baud)
                {
                    ReadTimeout = 500,
                    WriteTimeout = 500,
                    DtrEnable = true,
                    RtsEnable = true
                };
                try
                {
                    port.Open();
                }
                catch
                {
                    port.Dispose();
                    throw;
                }

                _port = port;
                MarkReadActivity();
                Logger.Log($"Connected to {portName} @ {_baud} baud");
                NotifyConnectionChanged(true);
                _buf.Clear();

                // Request device info + knob positions (FE 01 FF)
                TrySendInfoRequest(port);

                await ReadLoop(ct);
            }
            catch (Exception ex)
            {
                bool requested = System.Threading.Interlocked.Exchange(ref _reconnectRequested, 0) == 1;
                Logger.Log(requested
                    ? $"Serial reconnecting: {ex.Message}"
                    : $"Serial error: {ex.Message}");
                NotifyConnectionChanged(false);
                CloseCurrentPort();

                if (_running && !ct.IsCancellationRequested)
                {
                    int delayMs = requested ? 250 : 5000;
                    await Task.Delay(delayMs, ct).ContinueWith(_ => { });
                }
            }
            finally
            {
                if (System.Threading.Interlocked.Exchange(ref _reconnectRequested, 0) == 1)
                    CloseCurrentPort();
            }
        }
    }

    /// <summary>
    /// Tries the configured port first. If it fails, scans all available COM ports
    /// and probes each for Turn Up protocol frames (health ping, device ID, knob batch).
    /// </summary>
    private async Task<string?> FindDevicePort(CancellationToken ct)
    {
        // Always try configured port first even if GetPortNames() doesn't list it —
        // the registry-based enumeration can briefly miss a port after process restart
        var allPorts = SerialPort.GetPortNames();
        var candidates = new List<string> { _portName };
        foreach (var p in allPorts)
        {
            if (p != _portName)
                candidates.Add(p);
        }

        foreach (var portName in candidates)
        {
            if (ct.IsCancellationRequested) return null;

            try
            {
                using var probe = new SerialPort(portName, _baud)
                {
                    ReadTimeout = 500,
                    WriteTimeout = 500,
                    DtrEnable = true,
                    RtsEnable = true
                };
                probe.Open();
                TrySendInfoRequest(probe);

                // Listen for up to 2 seconds for a valid Turn Up frame
                var probeBuf = new byte[64];
                var data = new List<byte>();
                var deadline = DateTime.UtcNow.AddSeconds(2);

                while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
                {
                    try
                    {
                        int n = await probe.BaseStream.ReadAsync(probeBuf, 0, probeBuf.Length, ct)
                            .WaitAsync(TimeSpan.FromMilliseconds(500), ct);
                        for (int i = 0; i < n; i++) data.Add(probeBuf[i]);

                        if (ContainsTurnUpFrame(data))
                        {
                            Logger.Log($"Turn Up device detected on {portName}");
                            probe.Close();
                            return portName;
                        }
                    }
                    catch (TimeoutException) { }
                    catch (OperationCanceledException) { return null; }
                }

                probe.Close();
            }
            catch { }
        }

        Logger.Log("Turn Up device not found on any COM port");
        return null;
    }

    /// <summary>
    /// Checks if the byte buffer contains any valid Turn Up protocol frame.
    /// </summary>
    private static bool ContainsTurnUpFrame(List<byte> data)
    {
        for (int i = 0; i < data.Count - 2; i++)
        {
            if (data[i] != 0xFE) continue;

            foreach (var (id, len) in MessageTypes)
            {
                if (i + len > data.Count) continue;
                if (data[i + 1] == id && data[i + len - 1] == 0xFF)
                    return true;
            }
        }
        return false;
    }

    private async Task ReadLoop(CancellationToken ct)
    {
        var tmp = new byte[64];
        var lastReadUtc = DateTime.UtcNow;
        while (_running && !ct.IsCancellationRequested && _port?.IsOpen == true)
        {
            try
            {
                var port = _port;
                if (port == null || !port.IsOpen)
                    break;

                // SerialPort.BaseStream.ReadAsync can hang forever on some USB/serial
                // stalls. Use the SerialPort timeout so the connection loop can recover.
                int n = port.Read(tmp, 0, tmp.Length);
                if (n <= 0)
                    continue;

                lastReadUtc = DateTime.UtcNow;
                MarkReadActivity(lastReadUtc);
                for (int i = 0; i < n; i++) _buf.Add(tmp[i]);

                // Security: prevent unbounded buffer growth from malformed data
                if (_buf.Count > 256)
                {
                    Logger.Log($"Serial buffer overflow ({_buf.Count} bytes), clearing");
                    _buf.Clear();
                }

                ParseFrames();
            }
            catch (TimeoutException)
            {
                var idleFor = DateTime.UtcNow - lastReadUtc;
                if (idleFor >= TimeSpan.FromSeconds(2))
                {
                    var port = _port;
                    if (port?.IsOpen == true)
                        TrySendInfoRequest(port);
                }

                if (idleFor >= ReadStallTimeout)
                {
                    throw new System.IO.IOException(
                        $"No Turn Up serial data for {idleFor.TotalSeconds:0.0}s on {_port?.PortName ?? _portName}; reconnecting");
                }

                try { await Task.Delay(50, ct); }
                catch (OperationCanceledException) { break; }
            }
            catch (OperationCanceledException) { break; }
            catch { throw; }
        }
    }

    private static void TrySendInfoRequest(SerialPort port)
    {
        try { port.Write(new byte[] { 0xFE, 0x01, 0xFF }, 0, 3); }
        catch { }
    }

    private void MarkReadActivity(DateTime? utc = null)
        => System.Threading.Interlocked.Exchange(ref _lastReadUtcTicks, (utc ?? DateTime.UtcNow).Ticks);

    private void NotifyConnectionChanged(bool connected)
    {
        int next = connected ? 1 : 0;
        if (System.Threading.Interlocked.Exchange(ref _connectionState, next) == next)
            return;

        OnConnectionChanged?.Invoke(connected);
    }

    private void CloseCurrentPort()
    {
        var port = System.Threading.Interlocked.Exchange(ref _port, null);
        if (port == null) return;

        try { port.Close(); } catch { }
        try { port.Dispose(); } catch { }
    }

    // Message table (from decompiled TurnUpBox.dll):
    //   ID=0x02  len=3  Health/ping response: fe 02 ff
    //   ID=0x03  len=6  Knob value:           fe 03 [idx] [hi] [lo] ff
    //   ID=0x04  len=13 Knob batch (connect):  fe 04 [5x hi+lo] ff
    //   ID=0x06  len=4  Button press (down):   fe 06 [idx] ff
    //   ID=0x07  len=4  Button release (up):   fe 07 [idx] ff
    //   ID=0x08  len=7  Device ID:             fe 08 [4 bytes] ff
    private static readonly (byte Id, int Len)[] MessageTypes =
    {
        (0x02, 3),  // health
        (0x03, 6),  // knob
        (0x04, 13), // knob batch
        (0x06, 4),  // button down
        (0x07, 4),  // button up
        (0x08, 7),  // device id
    };

    private void ParseFrames()
    {
        while (_buf.Count >= 3)
        {
            // Find start byte
            if (_buf[0] != 0xFE) { _buf.RemoveAt(0); continue; }

            // Try to match a known message type
            bool matched = false;
            foreach (var (id, len) in MessageTypes)
            {
                if (_buf.Count < len) continue;
                if (_buf[1] != id) continue;
                if (_buf[len - 1] != 0xFF) continue;

                // Valid frame — extract and handle
                var frame = _buf.GetRange(0, len).ToArray();
                _buf.RemoveRange(0, len);
                HandleFrame(id, frame);
                matched = true;
                break;
            }

            if (!matched)
            {
                // No known message matched — check if we might need more data
                bool mightMatch = false;
                foreach (var (id, len) in MessageTypes)
                {
                    if (_buf.Count >= 2 && _buf[1] == id && _buf.Count < len)
                    {
                        mightMatch = true; // need more bytes
                        break;
                    }
                }
                if (!mightMatch)
                {
                    // Garbage byte, skip it
                    _buf.RemoveAt(0);
                }
                else
                {
                    break; // wait for more data
                }
            }
        }
    }

    private void HandleFrame(byte id, byte[] frame)
    {
        switch (id)
        {
            case 0x02:
                // Health/ping response — just log occasionally
                break;

            case 0x03:
                // Knob: fe 03 [idx] [hi] [lo] ff
                {
                    int idx = frame[2];
                    int raw = (frame[3] << 8) | frame[4];
                    if (idx >= 0 && idx < 5)
                    {
                        // Deadzone on RAW value first — prevents snap discontinuity from defeating jitter filter
                        if (_lastFiredValues[idx] == -1 || Math.Abs(raw - _lastFiredValues[idx]) >= JitterDeadzone)
                        {
                            _lastFiredValues[idx] = raw;
                            // Snap top endpoint — pots may not reach full ADC range
                            int val = raw;
                            if (val > 1000) val = 1023;
                            OnKnob?.Invoke(new KnobEvent { Idx = idx, Value = val });
                        }
                    }
                }
                break;

            case 0x04:
                // Knob batch: fe 04 [5x hi+lo] ff — sent on connect
                // Always fire all values to restore initial state; update deadzone baseline
                for (int i = 0; i < 5; i++)
                {
                    int val = (frame[2 + i * 2] << 8) | frame[3 + i * 2];
                    if (val > 1000) val = 1023;
                    _lastFiredValues[i] = val;
                    OnKnob?.Invoke(new KnobEvent { Idx = i, Value = val, IsBatch = true });
                }
                break;

            case 0x06:
                // Button press (down): fe 06 [idx] ff
                {
                    int idx = frame[2];
                    if (idx >= 0 && idx < 5)
                        OnButton?.Invoke(new ButtonEvent { Idx = idx, IsDown = true });
                }
                break;

            case 0x07:
                // Button release (up): fe 07 [idx] ff
                {
                    int idx = frame[2];
                    if (idx >= 0 && idx < 5)
                        OnButton?.Invoke(new ButtonEvent { Idx = idx, IsDown = false });
                }
                break;

            case 0x08:
                // Device ID: fe 08 [4 bytes] ff
                Logger.Log($"Device ID: {frame[2]:X2}{frame[3]:X2}{frame[4]:X2}{frame[5]:X2}");
                break;
        }
    }

    public void Dispose()
    {
        _running = false;
        _cts.Cancel();
        _cts.Dispose();
        CloseCurrentPort();
    }
}
