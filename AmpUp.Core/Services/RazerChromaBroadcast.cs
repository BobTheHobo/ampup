using System.Runtime.InteropServices;
using System.Text;

namespace AmpUp.Core.Services;

/// <summary>
/// Razer Chroma Broadcast listener — the "old Turn Up" integration path.
/// This does NOT control Razer devices; it RECEIVES the current Chroma effect
/// (5 ChromaLink zone colors) broadcast by Razer's software and mirrors it onto
/// the Turn Up knobs / room lights. Ported from JaredWF/TurnUpCustomizer
/// (TurnUpService/Util/Razer/RazerIntegration.cs).
///
/// Requires the Razer Chroma runtime installed + running (Synapse 3 / the
/// standalone Razer Chroma App + Chroma Connect) — same model as Corsair needing
/// iCUE. The broadcast DLL (RzChromaBroadcastAPI64.dll) ships with that runtime.
///
/// IMPORTANT: AppId must be AmpUp's own Chroma Broadcast app GUID registered on
/// developer.razer.com. The placeholder below is a dev stand-in — Razer will not
/// broadcast to an unregistered AppId, so this must be replaced before release.
/// </summary>
public sealed class RazerChromaBroadcast : IDisposable
{
    // TODO: replace with AmpUp's registered Chroma Broadcast AppId (developer.razer.com).
    // Until then Razer will not deliver broadcast events — connection "works" but no colors arrive.
    private static readonly Guid PlaceholderAppId = new("00000000-0000-0000-0000-00000000a4d0");

    private readonly Guid _appId;

    /// <summary>
    /// Fires whenever a new Chroma broadcast frame arrives, with a 45-byte
    /// (15 LED × RGB) frame ready for RgbController.SetScreenSyncColors.
    /// Raised on the Razer broadcast thread — the consumer must be thread-safe.
    /// </summary>
    public event Action<byte[]>? OnFrame;

    public bool IsConnected { get; private set; }

    /// <summary>Last reason connection failed, for surfacing in the UI.</summary>
    public string? LastError { get; private set; }

    // ── Native broadcast API ────────────────────────────────────────
    private enum ChromaBroadcastType
    {
        Effect = 1,
        Status = 2,
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ChromaBroadcastEffect
    {
        public int CL1;
        public int CL2;
        public int CL3;
        public int CL4;
        public int CL5;
        public int Reserved;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate long InitDelegate(Guid id);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate long UnInitDelegate();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int RegisterEventDelegate(BroadcastEventDelegate fn);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int UnregisterDelegate();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int BroadcastEventDelegate(ChromaBroadcastType type, IntPtr data);

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi)]
    private static extern IntPtr LoadLibrary(string dllToLoad);
    [DllImport("kernel32.dll")]
    private static extern bool FreeLibrary(IntPtr hModule);
    [DllImport("kernel32.dll", CharSet = CharSet.Ansi)]
    private static extern IntPtr GetProcAddress(IntPtr hModule, string procedureName);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetModuleFileName(IntPtr hModule, StringBuilder lpFilename, int nSize);

    private IntPtr _dllHandle = IntPtr.Zero;
    private BroadcastEventDelegate? _broadcastHandler;
    private GCHandle _gcHandler;
    private readonly object _lock = new();
    private bool _disposed;

    public RazerChromaBroadcast(Guid? appId = null)
    {
        _appId = appId ?? PlaceholderAppId;
    }

    /// <summary>
    /// Loads the Razer broadcast DLL and registers for effect notifications.
    /// Returns true if the genuine, signed Razer DLL was found and Init succeeded.
    /// Safe to call when Razer software isn't installed — it just returns false.
    /// </summary>
    public bool TryStart()
    {
        lock (_lock)
        {
            if (_disposed) return false;
            if (IsConnected) return true;

            try
            {
                _dllHandle = LoadDll();
                if (_dllHandle == IntPtr.Zero)
                {
                    LastError = "Razer Chroma Broadcast DLL not found. Install/start the Razer Chroma App (or Synapse + Chroma Connect).";
                    return false;
                }

                if (!ValidateDllIsGenuineRazer())
                {
                    LastError = "Razer Chroma Broadcast DLL failed signature validation.";
                    FreeAndReset();
                    return false;
                }

                var initPtr = GetProcAddress(_dllHandle, "Init");
                var registerPtr = GetProcAddress(_dllHandle, "RegisterEventNotification");
                if (initPtr == IntPtr.Zero || registerPtr == IntPtr.Zero)
                {
                    LastError = "Razer Chroma Broadcast DLL is missing expected entry points.";
                    FreeAndReset();
                    return false;
                }

                var init = Marshal.GetDelegateForFunctionPointer<InitDelegate>(initPtr);
                init(_appId);

                _broadcastHandler = OnChromaBroadcast;
                _gcHandler = GCHandle.Alloc(_broadcastHandler);
                var register = Marshal.GetDelegateForFunctionPointer<RegisterEventDelegate>(registerPtr);
                register(_broadcastHandler);

                IsConnected = true;
                LastError = null;
                return true;
            }
            catch (Exception ex)
            {
                LastError = $"Razer Chroma Broadcast init failed: {ex.Message}";
                FreeAndReset();
                return false;
            }
        }
    }

    public void Stop()
    {
        lock (_lock)
        {
            if (_dllHandle != IntPtr.Zero)
            {
                try
                {
                    var unregPtr = GetProcAddress(_dllHandle, "UnRegisterEventNotification");
                    if (unregPtr != IntPtr.Zero)
                        Marshal.GetDelegateForFunctionPointer<UnregisterDelegate>(unregPtr)();

                    var uninitPtr = GetProcAddress(_dllHandle, "UnInit");
                    if (uninitPtr != IntPtr.Zero)
                        Marshal.GetDelegateForFunctionPointer<UnInitDelegate>(uninitPtr)();
                }
                catch { /* best effort teardown */ }
            }
            FreeAndReset();
        }
    }

    private void FreeAndReset()
    {
        if (_gcHandler.IsAllocated) _gcHandler.Free();
        _broadcastHandler = null;
        if (_dllHandle != IntPtr.Zero)
        {
            FreeLibrary(_dllHandle);
            _dllHandle = IntPtr.Zero;
        }
        IsConnected = false;
    }

    private static IntPtr LoadDll()
    {
        string dllName = GetDllName();
        // Standard search first (Razer registers the DLL on a searchable path).
        var handle = LoadLibrary(dllName);
        if (handle != IntPtr.Zero) return handle;

        // Fallback: the explicit Razer Chroma Broadcast install location.
        foreach (var root in new[]
        {
            Environment.ExpandEnvironmentVariables("%ProgramW6432%"),
            Environment.ExpandEnvironmentVariables("%ProgramFiles(x86)%"),
        })
        {
            if (string.IsNullOrEmpty(root)) continue;
            string full = System.IO.Path.Combine(root, "Razer", "ChromaBroadcast", "bin", dllName);
            if (System.IO.File.Exists(full))
            {
                handle = LoadLibrary(full);
                if (handle != IntPtr.Zero) return handle;
            }
        }
        return IntPtr.Zero;
    }

    private static string GetDllName() =>
        Environment.Is64BitProcess ? "RzChromaBroadcastAPI64.dll" : "RzChromaBroadcastAPI.dll";

    /// <summary>
    /// Verifies the loaded DLL lives in a Razer/System path and carries a valid
    /// Authenticode signature — guards against a spoofed DLL on the search path.
    /// (Turn Up dropped the "Razer USA Ltd." subject check after Razer's signing
    /// cert expired 2026-02-26; we match that and only require a trusted signature.)
    /// </summary>
    private bool ValidateDllIsGenuineRazer()
    {
        var builder = new StringBuilder(260);
        GetModuleFileName(_dllHandle, builder, builder.Capacity);
        string path = builder.ToString();
        if (string.IsNullOrEmpty(path)) return false;

        if (!IsExpectedDllPath(path)) return false;
        return AuthenticodeTools.IsTrusted(path);
    }

    private static bool IsExpectedDllPath(string actualPath)
    {
        string name = GetDllName();
        string lower = actualPath.ToLowerInvariant();

        string sysRoot = System.IO.Path.GetPathRoot(Environment.SystemDirectory) ?? "";
        var expected = new List<string>();
        if (sysRoot.Length > 0)
        {
            expected.Add(System.IO.Path.Combine(sysRoot, "Windows", "System32", name));
            expected.Add(System.IO.Path.Combine(sysRoot, "Windows", "SysWOW64", name));
        }
        foreach (var root in new[]
        {
            Environment.ExpandEnvironmentVariables("%ProgramW6432%"),
            Environment.ExpandEnvironmentVariables("%ProgramFiles(x86)%"),
        })
        {
            if (!string.IsNullOrEmpty(root))
                expected.Add(System.IO.Path.Combine(root, "Razer", "ChromaBroadcast", "bin", name));
        }

        foreach (var e in expected)
            if (e.ToLowerInvariant() == lower) return true;
        return false;
    }

    // Razer packs each ChromaLink zone as 0x00BBGGRR.
    private static (byte R, byte G, byte B) FromChroma(int v) =>
        ((byte)(v & 0xFF), (byte)((v >> 8) & 0xFF), (byte)((v >> 16) & 0xFF));

    private int OnChromaBroadcast(ChromaBroadcastType type, IntPtr data)
    {
        if (type != ChromaBroadcastType.Effect || data == IntPtr.Zero)
            return 0;

        try
        {
            var fx = Marshal.PtrToStructure<ChromaBroadcastEffect>(data);
            Span<(byte R, byte G, byte B)> zones = stackalloc (byte, byte, byte)[5]
            {
                FromChroma(fx.CL1), FromChroma(fx.CL2), FromChroma(fx.CL3),
                FromChroma(fx.CL4), FromChroma(fx.CL5),
            };

            // 5 ChromaLink zones → 5 knobs × 3 LEDs (1:1, each knob solid in its zone color).
            var frame = new byte[45];
            for (int k = 0; k < 5; k++)
            {
                var (r, g, b) = zones[k];
                for (int led = 0; led < 3; led++)
                {
                    int o = (k * 3 + led) * 3;
                    frame[o] = r;
                    frame[o + 1] = g;
                    frame[o + 2] = b;
                }
            }
            OnFrame?.Invoke(frame);
        }
        catch { /* never let a native callback throw back across the boundary */ }
        return 0;
    }

    public void Dispose()
    {
        Stop();
        _disposed = true;
    }
}
