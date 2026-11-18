using System.Runtime.InteropServices;

namespace PhoneMirror;

/// <summary>
/// While a phone is being mirrored, asks Windows to keep this laptop's Wi-Fi steady:
/// background network scans off (they pause the link for a moment about once a minute, which shows up
/// as lag spikes) and "media streaming mode" on. Everything goes back to normal when mirroring stops,
/// or by itself if Phone Mirror closes, because Windows ties these settings to our open handle.
/// </summary>
static class WifiTuning
{
    const int OpBackgroundScan = 2, OpMediaStreaming = 3;

    static IntPtr handle = IntPtr.Zero;
    static readonly List<Guid> tuned = new();
    public static Action<string>? Log;

    static WifiTuning()
    {
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Set(false);
    }

    public static bool Active => handle != IntPtr.Zero;

    public static void Set(bool on)
    {
        if (on == Active) return;
        try
        {
            if (on) Start(); else Stop();
        }
        catch (Exception e)
        {
            Log?.Invoke("Wi-Fi tuning unavailable: " + e.Message);
            try { if (handle != IntPtr.Zero) WlanCloseHandle(handle, IntPtr.Zero); } catch { }
            handle = IntPtr.Zero;
            tuned.Clear();
        }
    }

    static void Start()
    {
        if (WlanOpenHandle(2, IntPtr.Zero, out _, out var h) != 0) return;
        handle = h;
        if (WlanEnumInterfaces(h, IntPtr.Zero, out var list) != 0) return;
        try
        {
            int n = Marshal.ReadInt32(list);
            for (int i = 0; i < n; i++)
            {
                // WLAN_INTERFACE_INFO: GUID (16) + WCHAR[256] (512) + state (4) = 532 bytes, after an 8-byte header.
                var item = list + 8 + i * 532;
                var g = Marshal.PtrToStructure<Guid>(item);
                int state = Marshal.ReadInt32(item + 16 + 512);
                if (state != 1) continue;   // 1 = connected
                bool a = SetBool(g, OpBackgroundScan, false);
                bool b = SetBool(g, OpMediaStreaming, true);
                if (a || b) tuned.Add(g);
            }
        }
        finally { WlanFreeMemory(list); }
        if (tuned.Count > 0) Log?.Invoke("Wi-Fi tuned for mirroring (background scans paused, streaming mode on).");
    }

    static void Stop()
    {
        foreach (var g in tuned)
        {
            SetBool(g, OpMediaStreaming, false);
            SetBool(g, OpBackgroundScan, true);
        }
        tuned.Clear();
        WlanCloseHandle(handle, IntPtr.Zero);
        handle = IntPtr.Zero;
    }

    static bool SetBool(Guid g, int op, bool value)
    {
        var p = Marshal.AllocHGlobal(4);
        try
        {
            Marshal.WriteInt32(p, value ? 1 : 0);
            return WlanSetInterface(handle, ref g, op, 4, p, IntPtr.Zero) == 0;
        }
        finally { Marshal.FreeHGlobal(p); }
    }

    [DllImport("wlanapi.dll")] static extern int WlanOpenHandle(uint clientVersion, IntPtr reserved, out uint negotiated, out IntPtr handle);
    [DllImport("wlanapi.dll")] static extern int WlanCloseHandle(IntPtr handle, IntPtr reserved);
    [DllImport("wlanapi.dll")] static extern int WlanEnumInterfaces(IntPtr handle, IntPtr reserved, out IntPtr list);
    [DllImport("wlanapi.dll")] static extern void WlanFreeMemory(IntPtr p);
    [DllImport("wlanapi.dll")] static extern int WlanSetInterface(IntPtr handle, ref Guid iface, int opcode, uint size, IntPtr data, IntPtr reserved);
}
