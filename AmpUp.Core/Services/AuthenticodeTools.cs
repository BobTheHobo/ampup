using System.Runtime.InteropServices;

namespace AmpUp.Core.Services;

/// <summary>
/// Minimal Authenticode trust check via WinVerifyTrust — used to confirm the
/// Razer Chroma Broadcast DLL is genuinely signed before loading its entry
/// points. Ported from JaredWF/TurnUpCustomizer (Util/Razer/AuthenticodeTools.cs).
/// </summary>
internal static class AuthenticodeTools
{
    [DllImport("Wintrust.dll", PreserveSig = true, SetLastError = false)]
    private static extern uint WinVerifyTrust(IntPtr hWnd, IntPtr pgActionID, IntPtr pWinTrustData);

    public static bool IsTrusted(string fileName)
    {
        try { return WinVerifyTrust(fileName) == 0; }
        catch { return false; }
    }

    private static uint WinVerifyTrust(string fileName)
    {
        var wintrustActionGenericVerifyV2 = new Guid("{00AAC56B-CD44-11d0-8CC2-00C04FC295EE}");
        uint result;
        using var fileInfo = new WintrustFileInfo(fileName);
        using var guidPtr = new UnmanagedPointer(Marshal.AllocHGlobal(Marshal.SizeOf<Guid>()));
        using var dataPtr = new UnmanagedPointer(Marshal.AllocHGlobal(Marshal.SizeOf<WintrustData>()));

        var data = new WintrustData(fileInfo);
        Marshal.StructureToPtr(wintrustActionGenericVerifyV2, guidPtr.Ptr, true);
        Marshal.StructureToPtr(data, dataPtr.Ptr, true);
        result = WinVerifyTrust(IntPtr.Zero, guidPtr.Ptr, dataPtr.Ptr);
        return result;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WintrustFileInfo : IDisposable
    {
        public WintrustFileInfo(string fileName)
        {
            cbStruct = (uint)Marshal.SizeOf<WintrustFileInfo>();
            pcwszFilePath = fileName;
            pgKnownSubject = IntPtr.Zero;
            hFile = IntPtr.Zero;
        }

        public uint cbStruct;
        [MarshalAs(UnmanagedType.LPTStr)] public string pcwszFilePath;
        public IntPtr hFile;
        public IntPtr pgKnownSubject;

        public void Dispose()
        {
            if (pgKnownSubject != IntPtr.Zero)
                Marshal.FreeHGlobal(pgKnownSubject);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WintrustData : IDisposable
    {
        public WintrustData(WintrustFileInfo fileInfo)
        {
            cbStruct = (uint)Marshal.SizeOf<WintrustData>();
            pInfoStruct = Marshal.AllocHGlobal(Marshal.SizeOf<WintrustFileInfo>());
            Marshal.StructureToPtr(fileInfo, pInfoStruct, false);
            dwUnionChoice = 1;       // File
            pPolicyCallbackData = IntPtr.Zero;
            pSIPCallbackData = IntPtr.Zero;
            dwUIChoice = 2;          // NoUI
            fdwRevocationChecks = 0; // None
            dwStateAction = 0;       // Ignore
            hWVTStateData = IntPtr.Zero;
            pwszURLReference = IntPtr.Zero;
            dwProvFlags = 0x100;     // Safer
            dwUIContext = 0;         // Execute
        }

        public uint cbStruct;
        public IntPtr pPolicyCallbackData;
        public IntPtr pSIPCallbackData;
        public uint dwUIChoice;
        public uint fdwRevocationChecks;
        public uint dwUnionChoice;
        public IntPtr pInfoStruct;
        public uint dwStateAction;
        public IntPtr hWVTStateData;
        public IntPtr pwszURLReference;
        public uint dwProvFlags;
        public uint dwUIContext;

        public void Dispose()
        {
            if (pInfoStruct != IntPtr.Zero)
                Marshal.FreeHGlobal(pInfoStruct);
        }
    }

    private sealed class UnmanagedPointer : IDisposable
    {
        public IntPtr Ptr { get; private set; }
        public UnmanagedPointer(IntPtr ptr) => Ptr = ptr;
        public void Dispose()
        {
            if (Ptr != IntPtr.Zero) { Marshal.FreeHGlobal(Ptr); Ptr = IntPtr.Zero; }
            GC.SuppressFinalize(this);
        }
    }
}
