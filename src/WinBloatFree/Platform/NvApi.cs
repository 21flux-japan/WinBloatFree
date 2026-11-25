using System.Runtime.InteropServices;
using WinBloatFree.Core;

namespace WinBloatFree.Platform;

/// <summary>
/// Minimal NVAPI driver settings (DRS) access through nvapi64.dll, which ships with the
/// NVIDIA driver. Only the global base profile is read or changed. Function ids and the
/// NVDRS_SETTING layout come from NVIDIA's public NVAPI SDK headers.
/// </summary>
static class NvApi
{
    const uint ID_Initialize = 0x0150E828, ID_CreateSession = 0x0694D52E, ID_DestroySession = 0xDAD9CFF8, ID_LoadSettings = 0x375DBD6B,
               ID_SaveSettings = 0xFCBC7E14, ID_GetBaseProfile = 0xDA8466A0, ID_GetSetting = 0x73BF8338, ID_SetSetting = 0x577DD202,
               ID_DeleteProfileSetting = 0xE4A26362, ID_RestoreProfileDefaultSetting = 0x53F0381E;
    const int NVAPI_OK = 0, NVAPI_SETTING_NOT_FOUND = -160;

    // NVDRS_SETTING_V1: version, NvAPI_UnicodeString name (4096 bytes), settingId, settingType, settingLocation,
    // isCurrentPredefined, isPredefinedValid, predefined value union (4100 bytes), current value union (4100 bytes).
    const int SettingSize = 4 + 4096 + 5 * 4 + 4100 + 4100; // 12320
    const uint SettingVersion = SettingSize | (1u << 16);
    const int OffId = 4100, OffType = 4104, OffIsCurrentPredefined = 4112, OffCurrentValue = 4120 + 4100;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate IntPtr QueryInterfaceFn(uint id);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int InitializeFn();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int CreateSessionFn(out IntPtr session);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int SessionFn(IntPtr session);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int GetBaseProfileFn(IntPtr session, out IntPtr profile);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int GetSettingFn(IntPtr session, IntPtr profile, uint id, IntPtr setting);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int SetSettingFn(IntPtr session, IntPtr profile, IntPtr setting);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int ProfileIdFn(IntPtr session, IntPtr profile, uint id);

    static readonly object Lock = new();
    static bool _tried;
    static QueryInterfaceFn? _qi;

    static T? Fn<T>(uint id) where T : Delegate
    {
        var p = _qi!(id);
        return p == IntPtr.Zero ? null : Marshal.GetDelegateForFunctionPointer<T>(p);
    }

    static bool Load()
    {
        lock (Lock)
        {
            if (_tried) return _qi is not null;
            _tried = true;
            if (!NativeLibrary.TryLoad("nvapi64.dll", out var lib)) return false;
            if (!NativeLibrary.TryGetExport(lib, "nvapi_QueryInterface", out var qi)) return false;
            _qi = Marshal.GetDelegateForFunctionPointer<QueryInterfaceFn>(qi);
            var init = Fn<InitializeFn>(ID_Initialize);
            if (init is null || init() != NVAPI_OK) { _qi = null; return false; }
            return true;
        }
    }

    static T WithBaseProfile<T>(Func<IntPtr, IntPtr, T> body)
    {
        var create = Fn<CreateSessionFn>(ID_CreateSession) ?? throw new InvalidOperationException("NVAPI has no DRS support.");
        var destroy = Fn<SessionFn>(ID_DestroySession)!;
        Check(create(out var session), "create session");
        try
        {
            Check(Fn<SessionFn>(ID_LoadSettings)!(session), "load settings");
            Check(Fn<GetBaseProfileFn>(ID_GetBaseProfile)!(session, out var profile), "get base profile");
            return body(session, profile);
        }
        finally { destroy(session); }
    }

    static void Check(int rc, string what)
    {
        if (rc != NVAPI_OK) throw new InvalidOperationException($"NVIDIA driver call failed ({what}, code {rc}).");
    }

    static IntPtr NewSetting()
    {
        var p = Marshal.AllocHGlobal(SettingSize);
        Marshal.Copy(new byte[SettingSize], 0, p, SettingSize);
        Marshal.WriteInt32(p, 0, unchecked((int)SettingVersion));
        return p;
    }

    public static NvSnapshot Get(uint id)
    {
        if (!Load()) return new NvSnapshot(false, false, null);
        if (id == 0) return new NvSnapshot(true, false, null); // availability check only
        lock (Lock)
        {
            return WithBaseProfile((s, prof) =>
            {
                var buf = NewSetting();
                try
                {
                    int rc = Fn<GetSettingFn>(ID_GetSetting)!(s, prof, id, buf);
                    if (rc == NVAPI_SETTING_NOT_FOUND) return new NvSnapshot(true, false, null);
                    Check(rc, "get setting");
                    bool predefined = Marshal.ReadInt32(buf, OffIsCurrentPredefined) != 0;
                    uint value = unchecked((uint)Marshal.ReadInt32(buf, OffCurrentValue));
                    return new NvSnapshot(true, !predefined, value);
                }
                finally { Marshal.FreeHGlobal(buf); }
            });
        }
    }

    public static void Set(uint id, uint value)
    {
        if (!Load()) throw new InvalidOperationException("No NVIDIA driver found.");
        lock (Lock)
        {
            WithBaseProfile((s, prof) =>
            {
                var buf = NewSetting();
                try
                {
                    Marshal.WriteInt32(buf, OffId, unchecked((int)id));
                    Marshal.WriteInt32(buf, OffType, 0); // NVDRS_DWORD_TYPE
                    Marshal.WriteInt32(buf, OffCurrentValue, unchecked((int)value));
                    Check(Fn<SetSettingFn>(ID_SetSetting)!(s, prof, buf), "set setting");
                    Check(Fn<SessionFn>(ID_SaveSettings)!(s), "save settings");
                    return 0;
                }
                finally { Marshal.FreeHGlobal(buf); }
            });
        }
    }

    public static void Reset(uint id)
    {
        if (!Load()) throw new InvalidOperationException("No NVIDIA driver found.");
        lock (Lock)
        {
            WithBaseProfile((s, prof) =>
            {
                var restore = Fn<ProfileIdFn>(ID_RestoreProfileDefaultSetting);
                int rc = restore is null ? NVAPI_SETTING_NOT_FOUND : restore(s, prof, id);
                if (rc != NVAPI_OK) rc = Fn<ProfileIdFn>(ID_DeleteProfileSetting)!(s, prof, id);
                if (rc != NVAPI_OK && rc != NVAPI_SETTING_NOT_FOUND) Check(rc, "reset setting");
                Check(Fn<SessionFn>(ID_SaveSettings)!(s), "save settings");
                return 0;
            });
        }
    }
}
