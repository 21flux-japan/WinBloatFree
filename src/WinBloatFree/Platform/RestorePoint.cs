using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace WinBloatFree.Platform;

/// <summary>
/// Detects, enables and creates System Restore points through srclient.dll.
/// No scripts and no extra packages are used.
/// </summary>
static class RestorePoint
{
    const int BEGIN_SYSTEM_CHANGE = 100, END_SYSTEM_CHANGE = 101, MODIFY_SETTINGS = 12;
    const int ERROR_SERVICE_DISABLED = 1058;
    const string SppClientsKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\SPP\Clients";
    const string SppClientsValue = "{09F7EDC5-294E-4180-AF6A-FB0E6A0E9513}";
    const string SrKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\SystemRestore";
    const string SrPolicyKey = @"SOFTWARE\Policies\Microsoft\Windows NT\SystemRestore";
    const string FrequencyValue = "SystemRestorePointCreationFrequency";

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode, Pack = 1)]
    struct RESTOREPOINTINFOW
    {
        public int dwEventType;
        public int dwRestorePtType;
        public long llSequenceNumber;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szDescription;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    struct STATEMGRSTATUS { public int nStatus; public long llSequenceNumber; }

    [DllImport("srclient.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool SRSetRestorePointW(ref RESTOREPOINTINFOW info, out STATEMGRSTATUS status);

    [DllImport("srclient.dll", CharSet = CharSet.Unicode)]
    static extern int EnableSR([MarshalAs(UnmanagedType.LPWStr)] string drive);

    [DllImport("srclient.dll", CharSet = CharSet.Unicode)]
    static extern int EnableSREx([MarshalAs(UnmanagedType.LPWStr)] string drive, [MarshalAs(UnmanagedType.Bool)] bool wait);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool GetVolumeNameForVolumeMountPoint(string mountPoint, StringBuilder name, int length);

    /// <summary>The system drive in "C:\" form, or "C:\" if it cannot be read.</summary>
    public static string SystemDrive
    {
        get
        {
            var root = Path.GetPathRoot(Environment.SystemDirectory);
            if (string.IsNullOrEmpty(root)) return @"C:\";
            return root.EndsWith('\\') ? root : root + "\\";
        }
    }

    public static bool IsAvailable()
    {
        try
        {
            return File.Exists(Path.Combine(Environment.SystemDirectory, "srclient.dll"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>True when System Restore is on for the system drive.</summary>
    public static bool IsEnabled()
    {
        try
        {
            using var hive = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var policy = hive.OpenSubKey(SrPolicyKey);
            if (Dword(policy, "DisableSR") == 1) return false;

            using var key = hive.OpenSubKey(SrKey);
            if (Dword(key, "DisableSR") == 1) return false;

            using var spp = hive.OpenSubKey(SppClientsKey);
            if (spp?.GetValue(SppClientsValue) is string[] entries && entries.Length > 0)
            {
                var guid = SystemVolumeGuid();
                if (guid is not null && entries.Any(e => e.Contains(guid, StringComparison.OrdinalIgnoreCase)))
                    return true;
            }

            // Older signal: a positive session interval means protection is on.
            return Dword(key, "RPSessionInterval") > 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return false;
        }
    }

    /// <summary>Turns on System Restore for the system drive. Returns null on success.</summary>
    public static string? Enable()
    {
        if (!IsAvailable()) return "System Restore is not available on this edition of Windows.";
        var drive = SystemDrive;
        try
        {
            int rc;
            try { rc = EnableSREx(drive, true); }
            catch (EntryPointNotFoundException) { rc = EnableSR(drive); }
            if (rc != 0 && drive.EndsWith('\\'))
            {
                var alt = drive.TrimEnd('\\');
                try { rc = EnableSREx(alt, true); }
                catch (EntryPointNotFoundException) { rc = EnableSR(alt); }
            }
            return rc == 0 ? null : $"Windows could not turn on System Restore (error {rc}).";
        }
        catch (DllNotFoundException)
        {
            return "System Restore is not available on this edition of Windows.";
        }
        catch (Exception ex)
        {
            return "Windows could not turn on System Restore: " + ex.Message;
        }
    }

    /// <summary>Returns null on success, otherwise a plain explanation.</summary>
    public static string? Create(string description)
    {
        // Windows normally allows one restore point per 24 hours. Lift that for this one call, then put it back.
        using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var key = root.CreateSubKey(SrKey, writable: true);
        object? oldFrequency = key?.GetValue(FrequencyValue);
        try
        {
            key?.SetValue(FrequencyValue, 0, RegistryValueKind.DWord);
            var info = new RESTOREPOINTINFOW { dwEventType = BEGIN_SYSTEM_CHANGE, dwRestorePtType = MODIFY_SETTINGS, szDescription = description };
            if (!SRSetRestorePointW(ref info, out var status))
            {
                int code = status.nStatus != 0 ? status.nStatus : Marshal.GetLastWin32Error();
                return code == ERROR_SERVICE_DISABLED
                    ? "System Restore is turned off on this PC."
                    : $"Windows could not create a restore point (error {code}).";
            }
            var end = new RESTOREPOINTINFOW { dwEventType = END_SYSTEM_CHANGE, dwRestorePtType = MODIFY_SETTINGS, llSequenceNumber = status.llSequenceNumber, szDescription = description };
            SRSetRestorePointW(ref end, out _);
            return null;
        }
        catch (DllNotFoundException)
        {
            return "System Restore is not available on this edition of Windows.";
        }
        finally
        {
            if (oldFrequency is null) key?.DeleteValue(FrequencyValue, false);
            else key?.SetValue(FrequencyValue, oldFrequency, RegistryValueKind.DWord);
        }
    }

    static int Dword(RegistryKey? key, string name)
    {
        if (key?.GetValue(name) is int i) return i;
        if (key?.GetValue(name) is uint u) return unchecked((int)u);
        return 0;
    }

    static string? SystemVolumeGuid()
    {
        var sb = new StringBuilder(260);
        if (!GetVolumeNameForVolumeMountPoint(SystemDrive, sb, sb.Capacity)) return null;
        var id = sb.ToString();
        int start = id.IndexOf('{');
        int end = start < 0 ? -1 : id.IndexOf('}', start);
        return end > start ? id[start..(end + 1)] : null;
    }
}
