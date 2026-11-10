using System.Runtime.InteropServices;

namespace Escena.Video;

public sealed record MonitorInfo(string DeviceName, int Left, int Top, int Width, int Height, bool IsPrimary, int Index)
{
    public string Label => IsPrimary
        ? $"Pantalla {Index} — principal ({Width}×{Height})"
        : $"Pantalla {Index} — ampliada ({Width}×{Height})";
}

/// <summary>Llista les pantalles connectades (coordenades de Windows, les mateixes que fa servir SetWindowPos).</summary>
public static class Monitors
{
    public static List<MonitorInfo> List()
    {
        var raw = new List<(string Name, RECT Rect, bool Primary)>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr h, IntPtr hdc, ref RECT r, IntPtr data) =>
        {
            var info = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
            if (GetMonitorInfo(h, ref info))
                raw.Add((info.szDevice, info.rcMonitor, (info.dwFlags & 1) != 0));
            return true;
        }, IntPtr.Zero);

        // Principal primer, després d'esquerra a dreta.
        return raw.OrderByDescending(m => m.Primary).ThenBy(m => m.Rect.Left).ThenBy(m => m.Rect.Top)
            .Select((m, i) => new MonitorInfo(m.Name, m.Rect.Left, m.Rect.Top,
                m.Rect.Right - m.Rect.Left, m.Rect.Bottom - m.Rect.Top, m.Primary, i + 1))
            .ToList();
    }

    /// <summary>La pantalla indicada; si no hi és, la primera ampliada; si no n'hi ha, la principal.</summary>
    public static MonitorInfo Resolve(string screenId, out bool found)
    {
        var all = List();
        var m = all.FirstOrDefault(x => x.DeviceName == screenId);
        found = m != null || string.IsNullOrEmpty(screenId);
        return m ?? all.FirstOrDefault(x => !x.IsPrimary) ?? all.First();
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MONITORINFOEX
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szDevice;
    }

    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, ref RECT rect, IntPtr data);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc proc, IntPtr data);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX info);
}
