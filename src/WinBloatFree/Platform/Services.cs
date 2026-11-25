using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using WinBloatFree.Core;

namespace WinBloatFree.Platform;

/// <summary>Service Control Manager through advapi32, so no extra package is needed.</summary>
static class Services
{
    const uint SC_MANAGER_CONNECT = 0x0001;
    const uint SERVICE_QUERY_CONFIG = 0x0001, SERVICE_CHANGE_CONFIG = 0x0002, SERVICE_QUERY_STATUS = 0x0004, SERVICE_START = 0x0010, SERVICE_STOP = 0x0020;
    const uint SERVICE_NO_CHANGE = 0xFFFFFFFF;
    const uint SERVICE_CONFIG_DELAYED_AUTO_START_INFO = 3;
    const uint SERVICE_CONTROL_STOP = 1;
    const int ERROR_ACCESS_DENIED = 5, ERROR_SERVICE_DOES_NOT_EXIST = 1060, ERROR_SERVICE_ALREADY_RUNNING = 1056, ERROR_SERVICE_NOT_ACTIVE = 1062;
    const int SERVICE_STOPPED = 1, SERVICE_RUNNING = 4;

    [StructLayout(LayoutKind.Sequential)]
    struct SERVICE_STATUS { public uint dwServiceType, dwCurrentState, dwControlsAccepted, dwWin32ExitCode, dwServiceSpecificExitCode, dwCheckPoint, dwWaitHint; }

    [StructLayout(LayoutKind.Sequential)]
    struct SERVICE_DELAYED_AUTO_START_INFO { public int fDelayedAutostart; }

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)] static extern IntPtr OpenSCManagerW(string? machine, string? database, uint access);
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)] static extern IntPtr OpenServiceW(IntPtr scm, string name, uint access);
    [DllImport("advapi32.dll", SetLastError = true)] static extern bool CloseServiceHandle(IntPtr h);
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)] static extern bool QueryServiceConfigW(IntPtr svc, IntPtr buffer, uint size, out uint needed);
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)] static extern bool QueryServiceConfig2W(IntPtr svc, uint level, IntPtr buffer, uint size, out uint needed);
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool ChangeServiceConfigW(IntPtr svc, uint type, uint start, uint errorControl, string? binary, string? group, IntPtr tag, string? deps, string? account, string? password, string? display);
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)] static extern bool ChangeServiceConfig2W(IntPtr svc, uint level, ref SERVICE_DELAYED_AUTO_START_INFO info);
    [DllImport("advapi32.dll", SetLastError = true)] static extern bool QueryServiceStatus(IntPtr svc, out SERVICE_STATUS status);
    [DllImport("advapi32.dll", SetLastError = true)] static extern bool ControlService(IntPtr svc, uint control, out SERVICE_STATUS status);
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)] static extern bool StartServiceW(IntPtr svc, uint argc, IntPtr argv);

    sealed class Handle : IDisposable
    {
        public IntPtr Scm, Svc;
        public void Dispose()
        {
            if (Svc != IntPtr.Zero) CloseServiceHandle(Svc);
            if (Scm != IntPtr.Zero) CloseServiceHandle(Scm);
        }
    }

    static Handle? Open(string name, uint access)
    {
        var h = new Handle { Scm = OpenSCManagerW(null, null, SC_MANAGER_CONNECT) };
        if (h.Scm == IntPtr.Zero) { h.Dispose(); throw new Win32Exception(); }
        h.Svc = OpenServiceW(h.Scm, name, access);
        if (h.Svc == IntPtr.Zero)
        {
            int err = Marshal.GetLastWin32Error();
            h.Dispose();
            if (err == ERROR_SERVICE_DOES_NOT_EXIST) return null;
            throw new Win32Exception(err);
        }
        return h;
    }

    public static ServiceSnapshot Get(string name)
    {
        using var h = Open(name, SERVICE_QUERY_CONFIG | SERVICE_QUERY_STATUS);
        if (h is null) return new ServiceSnapshot(false, null, false);
        QueryServiceConfigW(h.Svc, IntPtr.Zero, 0, out var needed);
        var buf = Marshal.AllocHGlobal((int)Math.Max(needed, 64));
        try
        {
            if (!QueryServiceConfigW(h.Svc, buf, Math.Max(needed, 64), out _)) throw new Win32Exception();
            uint start = (uint)Marshal.ReadInt32(buf, 4); // QUERY_SERVICE_CONFIGW.dwStartType
            bool delayed = false;
            if (start == 2)
            {
                var b2 = Marshal.AllocHGlobal(16);
                try { if (QueryServiceConfig2W(h.Svc, SERVICE_CONFIG_DELAYED_AUTO_START_INFO, b2, 16, out _)) delayed = Marshal.ReadInt32(b2) != 0; }
                finally { Marshal.FreeHGlobal(b2); }
            }
            QueryServiceStatus(h.Svc, out var st);
            var s = start switch
            {
                0 => ServiceStart.Boot,
                1 => ServiceStart.System,
                2 => delayed ? ServiceStart.AutomaticDelayed : ServiceStart.Automatic,
                3 => ServiceStart.Manual,
                _ => ServiceStart.Disabled,
            };
            return new ServiceSnapshot(true, s, st.dwCurrentState != SERVICE_STOPPED);
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    static uint Native(ServiceStart s) => s switch
    {
        ServiceStart.Boot => 0,
        ServiceStart.System => 1,
        ServiceStart.Automatic or ServiceStart.AutomaticDelayed => 2,
        ServiceStart.Manual => 3,
        _ => 4,
    };

    public static void SetStart(string name, ServiceStart start)
    {
        try
        {
            using var h = Open(name, SERVICE_CHANGE_CONFIG | SERVICE_QUERY_CONFIG) ?? throw new InvalidOperationException($"Service {name} is not on this PC.");
            if (!ChangeServiceConfigW(h.Svc, SERVICE_NO_CHANGE, Native(start), SERVICE_NO_CHANGE, null, null, IntPtr.Zero, null, null, null, null))
                throw new Win32Exception();
            if (start is ServiceStart.Automatic or ServiceStart.AutomaticDelayed)
            {
                var info = new SERVICE_DELAYED_AUTO_START_INFO { fDelayedAutostart = start == ServiceStart.AutomaticDelayed ? 1 : 0 };
                ChangeServiceConfig2W(h.Svc, SERVICE_CONFIG_DELAYED_AUTO_START_INFO, ref info);
            }
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ERROR_ACCESS_DENIED)
        {
            // Some services (for example WaaSMedicSvc) refuse the SCM call; the registry value is the documented fallback.
            try
            {
                using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
                using var k = root.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{name}", writable: true) ?? throw new InvalidOperationException($"Service {name} is not on this PC.");
                k.SetValue("Start", (int)Native(start), RegistryValueKind.DWord);
                if (start is ServiceStart.Automatic or ServiceStart.AutomaticDelayed)
                    k.SetValue("DelayedAutostart", start == ServiceStart.AutomaticDelayed ? 1 : 0, RegistryValueKind.DWord);
            }
            catch (Exception inner) when (inner is UnauthorizedAccessException or System.Security.SecurityException)
            {
                throw new UnauthorizedAccessException($"Windows protects the {name} service and does not allow changing it, even as administrator.");
            }
        }
    }

    public static void Stop(string name)
    {
        using var h = Open(name, SERVICE_STOP | SERVICE_QUERY_STATUS);
        if (h is null) return;
        if (!ControlService(h.Svc, SERVICE_CONTROL_STOP, out _))
        {
            int err = Marshal.GetLastWin32Error();
            if (err == ERROR_SERVICE_NOT_ACTIVE) return;
            throw new Win32Exception(err);
        }
        for (int i = 0; i < 40; i++)
        {
            if (QueryServiceStatus(h.Svc, out var st) && st.dwCurrentState == SERVICE_STOPPED) return;
            Thread.Sleep(250);
        }
    }

    public static void Start(string name)
    {
        using var h = Open(name, SERVICE_START | SERVICE_QUERY_STATUS);
        if (h is null) return;
        if (!StartServiceW(h.Svc, 0, IntPtr.Zero))
        {
            int err = Marshal.GetLastWin32Error();
            if (err == ERROR_SERVICE_ALREADY_RUNNING) return;
            throw new Win32Exception(err);
        }
        for (int i = 0; i < 40; i++)
        {
            if (QueryServiceStatus(h.Svc, out var st) && st.dwCurrentState == SERVICE_RUNNING) return;
            Thread.Sleep(250);
        }
    }
}
