using System.ComponentModel;
using System.Runtime.InteropServices;

namespace WinBloatFree.Platform;

/// <summary>Restarts Windows after the user confirms. Uses the native shutdown API.</summary>
static class SystemRestart
{
    const uint TokenAdjustPrivileges = 0x20, TokenQuery = 0x8, SePrivilegeEnabled = 0x2;
    const uint ShutdownReason = 0x80040001; // planned application maintenance
    const string SeShutdownName = "SeShutdownPrivilege";

    [StructLayout(LayoutKind.Sequential)]
    struct Luid { public uint LowPart; public int HighPart; }

    [StructLayout(LayoutKind.Sequential)]
    struct LuidAndAttributes { public Luid Luid; public uint Attributes; }

    [StructLayout(LayoutKind.Sequential)]
    struct TokenPrivileges { public uint PrivilegeCount; public LuidAndAttributes Privileges; }

    [DllImport("advapi32.dll", SetLastError = true)]
    static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool LookupPrivilegeValue(string? systemName, string name, out Luid luid);

    [DllImport("advapi32.dll", SetLastError = true)]
    static extern bool AdjustTokenPrivileges(IntPtr tokenHandle, bool disableAll, ref TokenPrivileges newState, uint bufferLength, IntPtr previousState, IntPtr returnLength);

    [DllImport("kernel32.dll")]
    static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool CloseHandle(IntPtr handle);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool InitiateSystemShutdownExW(string? machineName, string? message, uint timeout, bool forceAppsClosed, bool rebootAfterShutdown, uint reason);

    public static void Reboot()
    {
        EnableShutdownPrivilege();
        if (!InitiateSystemShutdownExW(null, null, 0, false, true, ShutdownReason))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows could not start the restart.");
    }

    static void EnableShutdownPrivilege()
    {
        if (!OpenProcessToken(GetCurrentProcess(), TokenAdjustPrivileges | TokenQuery, out var token))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not open the process token.");
        try
        {
            if (!LookupPrivilegeValue(null, SeShutdownName, out var luid))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not look up the shutdown privilege.");
            var tp = new TokenPrivileges
            {
                PrivilegeCount = 1,
                Privileges = new LuidAndAttributes { Luid = luid, Attributes = SePrivilegeEnabled },
            };
            if (!AdjustTokenPrivileges(token, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not enable the shutdown privilege.");
        }
        finally
        {
            CloseHandle(token);
        }
    }
}
