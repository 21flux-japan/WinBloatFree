using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using WinBloatFree.Core;

namespace WinBloatFree.Platform;

/// <summary>
/// Registry writes that ask only for key-write rights (not permission-change rights),
/// and that can temporarily take ownership when an ACL blocks administrators.
/// </summary>
static class RegistryIO
{
    const int KeyRead = 0x20019, KeyWrite = 0x20006, KeyWow64_64 = 0x0100, Delete = 0x10000;
    const int WriteRights = KeyRead | KeyWrite | KeyWow64_64;
    const int WriteOrDelete = WriteRights | Delete;
    const int ErrorAccessDenied = 5, ErrorFileNotFound = 2;
    const int SeRegistryKey = 4;
    const int OwnerInfo = 0x1, DaclInfo = 0x4;
    static readonly IntPtr Hklm = new(unchecked((int)0x80000002));
    static readonly IntPtr Hkcu = new(unchecked((int)0x80000001));
    static readonly SecurityIdentifier Admins = new("S-1-5-32-544");

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern int RegCreateKeyEx(IntPtr hKey, string subKey, int reserved, IntPtr keyClass, int options, int sam, IntPtr security, out IntPtr result, out int disposition);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern int RegOpenKeyEx(IntPtr hKey, string subKey, int options, int sam, out IntPtr result);

    [DllImport("advapi32.dll", SetLastError = true)]
    static extern int RegCloseKey(IntPtr hKey);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern int GetNamedSecurityInfo(string name, int type, int info, out IntPtr owner, out IntPtr group, out IntPtr dacl, out IntPtr sacl, out IntPtr sd);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern int SetNamedSecurityInfo(string name, int type, int info, IntPtr owner, IntPtr group, IntPtr dacl, IntPtr sacl);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool ConvertSecurityDescriptorToStringSecurityDescriptor(IntPtr sd, int revision, int info, out IntPtr str, out uint len);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool ConvertStringSecurityDescriptorToSecurityDescriptor(string str, int revision, out IntPtr sd, IntPtr size);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr LocalFree(IntPtr h);

    [DllImport("advapi32.dll", SetLastError = true)]
    static extern bool ConvertStringSidToSid(string sid, out IntPtr psid);

    public static void SetValue(RegHive hive, string key, string name, RegKind kind, string data)
    {
        try
        {
            TrySet(hive, key, name, kind, data);
        }
        catch (Exception ex) when (IsDenied(ex))
        {
            try
            {
                WithOwnership(hive, key, () => TrySet(hive, key, name, kind, data));
            }
            catch (Exception inner) when (IsDenied(inner) || IsDenied(ex))
            {
                throw ProtectError(hive, key, name, inner);
            }
        }
    }

    public static void DeleteValue(RegHive hive, string key, string name)
    {
        try
        {
            TryDeleteValue(hive, key, name);
        }
        catch (Exception ex) when (IsDenied(ex))
        {
            try
            {
                WithOwnership(hive, key, () => TryDeleteValue(hive, key, name));
            }
            catch (Exception inner) when (IsDenied(inner) || IsDenied(ex))
            {
                throw ProtectError(hive, key, name, inner);
            }
        }
    }

    public static void DeleteKeyIfEmpty(RegHive hive, string key)
    {
        using var root = RegistryKey.OpenBaseKey(hive == RegHive.HKLM ? RegistryHive.LocalMachine : RegistryHive.CurrentUser, RegistryView.Registry64);
        using (var k = root.OpenSubKey(key, writable: false))
        {
            if (k is null || k.ValueCount > 0 || k.SubKeyCount > 0) return;
        }
        try
        {
            root.DeleteSubKey(key, throwOnMissingSubKey: false);
        }
        catch (Exception ex) when (IsDenied(ex))
        {
            try
            {
                WithOwnership(hive, key, () => root.DeleteSubKey(key, throwOnMissingSubKey: false));
            }
            catch (Exception inner) when (IsDenied(inner) || IsDenied(ex))
            {
                throw ProtectError(hive, key, "", inner);
            }
        }
    }

    static void TrySet(RegHive hive, string key, string name, RegKind kind, string data)
    {
        using var k = OpenOrCreate(hive, key, WriteRights) ?? throw new IOException("Could not open the registry key.");
        switch (kind)
        {
            case RegKind.DWord:
                if (!RegValues.TryParseNumber(data, out var d)) throw new FormatException("Not a number: " + data);
                k.SetValue(name, unchecked((int)(uint)d), RegistryValueKind.DWord);
                break;
            case RegKind.QWord:
                if (!RegValues.TryParseNumber(data, out var q)) throw new FormatException("Not a number: " + data);
                k.SetValue(name, unchecked((long)q), RegistryValueKind.QWord);
                break;
            case RegKind.ExpandString: k.SetValue(name, data, RegistryValueKind.ExpandString); break;
            case RegKind.MultiString: k.SetValue(name, data.Length == 0 ? Array.Empty<string>() : data.Split('\n'), RegistryValueKind.MultiString); break;
            case RegKind.Binary: k.SetValue(name, Convert.FromHexString(data), RegistryValueKind.Binary); break;
            default: k.SetValue(name, data, RegistryValueKind.String); break;
        }
    }

    static void TryDeleteValue(RegHive hive, string key, string name)
    {
        using var k = OpenOrCreate(hive, key, WriteOrDelete, create: false);
        k?.DeleteValue(name, throwOnMissingValue: false);
    }

    static RegistryKey? OpenOrCreate(RegHive hive, string key, int sam, bool create = true)
    {
        var hRoot = hive == RegHive.HKLM ? Hklm : Hkcu;
        IntPtr current = hRoot;
        var parts = key.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < parts.Length; i++)
        {
            int rc = create
                ? RegCreateKeyEx(current, parts[i], 0, IntPtr.Zero, 0, sam, IntPtr.Zero, out var next, out _)
                : RegOpenKeyEx(current, parts[i], 0, sam, out next);
            if (current != hRoot) RegCloseKey(current);
            if (rc != 0)
            {
                if (!create && rc == ErrorFileNotFound) return null;
                throw new System.ComponentModel.Win32Exception(rc);
            }
            current = next;
        }
        var safe = new SafeRegistryHandle(current, ownsHandle: true);
        return RegistryKey.FromHandle(safe, RegistryView.Registry64);
    }

    static void WithOwnership(RegHive hive, string key, Action write)
    {
        string path = (hive == RegHive.HKLM ? "MACHINE\\" : "USER\\") + key;
        string? original = ReadSddl(path);
        bool changed = false;
        try
        {
            TokenPrivilege.Enable("SeTakeOwnershipPrivilege", "SeRestorePrivilege");
            // Take ownership first so we can change the DACL, then grant Administrators write access.
            if (!SetOwner(path, Admins)) throw new UnauthorizedAccessException("Could not take ownership of the registry key.");
            changed = true;
            GrantAdminsFull(path, original);
            write();
        }
        finally
        {
            if (changed && original is not null)
            {
                try { RestoreSddl(path, original); }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine("Could not restore registry ACL: " + ex.Message); }
            }
        }
    }

    static string? ReadSddl(string path)
    {
        int rc = GetNamedSecurityInfo(path, SeRegistryKey, OwnerInfo | DaclInfo, out _, out _, out _, out _, out var sd);
        if (rc != 0 || sd == IntPtr.Zero) return null;
        try
        {
            if (!ConvertSecurityDescriptorToStringSecurityDescriptor(sd, 1, OwnerInfo | DaclInfo, out var str, out _))
                return null;
            try { return Marshal.PtrToStringUni(str); }
            finally { LocalFree(str); }
        }
        finally { LocalFree(sd); }
    }

    static bool SetOwner(string path, SecurityIdentifier owner)
    {
        if (!ConvertStringSidToSid(owner.Value, out var sid) || sid == IntPtr.Zero) return false;
        try
        {
            int rc = SetNamedSecurityInfo(path, SeRegistryKey, OwnerInfo, sid, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            return rc == 0;
        }
        finally { LocalFree(sid); }
    }

    static void GrantAdminsFull(string path, string? originalSddl)
    {
        // Start from the current DACL if we have it, and add an Administrators allow ACE.
        var sddl = originalSddl ?? ReadSddl(path) ?? "O:BAG:BAD:P(A;CI;KA;;;BA)";
        // Insert a full-control ACE for Administrators if it is not already present.
        if (!sddl.Contains("(A;", StringComparison.Ordinal) || !sddl.Contains(";;BA)", StringComparison.Ordinal))
        {
            int d = sddl.IndexOf("D:", StringComparison.Ordinal);
            if (d >= 0) sddl = sddl.Insert(d + 2, "(A;CI;KA;;;BA)");
            else sddl += "D:(A;CI;KA;;;BA)";
        }
        RestoreSddl(path, sddl, ownerAndDacl: true);
    }

    static void RestoreSddl(string path, string sddl, bool ownerAndDacl = true)
    {
        if (!ConvertStringSecurityDescriptorToSecurityDescriptor(sddl, 1, out var sd, IntPtr.Zero))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            GetOwnerAndDacl(sd, out var owner, out var dacl);
            int info = ownerAndDacl ? OwnerInfo | DaclInfo : DaclInfo;
            int rc = SetNamedSecurityInfo(path, SeRegistryKey, info, owner, IntPtr.Zero, dacl, IntPtr.Zero);
            if (rc != 0) throw new System.ComponentModel.Win32Exception(rc);
        }
        finally { LocalFree(sd); }
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    static extern bool GetSecurityDescriptorOwner(IntPtr sd, out IntPtr owner, out bool defaulted);

    [DllImport("advapi32.dll", SetLastError = true)]
    static extern bool GetSecurityDescriptorDacl(IntPtr sd, out bool present, out IntPtr dacl, out bool defaulted);

    static void GetOwnerAndDacl(IntPtr sd, out IntPtr owner, out IntPtr dacl)
    {
        GetSecurityDescriptorOwner(sd, out owner, out _);
        GetSecurityDescriptorDacl(sd, out _, out dacl, out _);
    }

    static bool IsDenied(Exception ex) =>
        ex is UnauthorizedAccessException
        || ex is System.Security.SecurityException
        || (ex is System.ComponentModel.Win32Exception w && w.NativeErrorCode is ErrorAccessDenied or 0x5)
        || (ex.InnerException is not null && IsDenied(ex.InnerException))
        || (ex is IOException && ex.Message.Contains("denied", StringComparison.OrdinalIgnoreCase));

    static DetailedException ProtectError(RegHive hive, string key, string name, Exception inner)
    {
        var target = hive + "\\" + key + (string.IsNullOrEmpty(name) ? "" : "\\" + name);
        return new DetailedException(
            "Windows protects this setting and would not allow the change.",
            target + ": " + inner.Message);
    }
}
