using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace WinBloatFree.Platform;

/// <summary>Creates a System Restore point with SRSetRestorePointW from srclient.dll.</summary>
static class RestorePoint
{
    const int BEGIN_SYSTEM_CHANGE = 100, END_SYSTEM_CHANGE = 101, MODIFY_SETTINGS = 12;
    const int ERROR_SERVICE_DISABLED = 1058;

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

    const string SrKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\SystemRestore";
    const string FrequencyValue = "SystemRestorePointCreationFrequency";

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
                    ? "System Restore is turned off on this PC. Turn it on in System Properties > System Protection, or uncheck the restore point box to apply without one."
                    : $"Windows could not create a restore point (error {code}). Check that System Restore is turned on, or uncheck the restore point box to apply without one.";
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
}
