using System.Runtime.InteropServices;

namespace HyperVNetworkSwitcher.Helpers;

/// <summary>Thin wrappers around Win32 APIs used for DPI-aware popup positioning.</summary>
internal static class NativeMethods
{
    private const uint SPI_GETWORKAREA          = 0x0030;
    private const uint MONITOR_DEFAULTTONEAREST  = 0x0002;
    private const int  MDT_EFFECTIVE_DPI         = 0;

    [StructLayout(LayoutKind.Sequential)]
    internal struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int  cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SystemParametersInfo(uint action, uint param, out RECT output, uint winIni);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT point);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(POINT point, uint flags);

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);

    [DllImport("Shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr monitor, int dpiType, out uint dpiX, out uint dpiY);

    /// <summary>
    /// Work area (taskbar-excluded desktop bounds, physical pixels) and DPI scale of the monitor
    /// under the cursor — the screen whose tray was just clicked.  Falls back to the primary
    /// monitor at 100 %.
    /// </summary>
    internal static (RECT WorkArea, double Scale) GetCursorMonitorMetrics()
    {
        if (GetCursorPos(out var cursor))
        {
            var monitor = MonitorFromPoint(cursor, MONITOR_DEFAULTTONEAREST);
            var info    = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };

            if (GetMonitorInfo(monitor, ref info))
            {
                double scale = 1.0;
                if (GetDpiForMonitor(monitor, MDT_EFFECTIVE_DPI, out uint dpiX, out _) == 0 && dpiX != 0)
                    scale = dpiX / 96.0;

                return (info.rcWork, scale);
            }
        }

        return (GetPrimaryWorkArea(), 1.0);
    }

    private static RECT GetPrimaryWorkArea()
    {
        if (SystemParametersInfo(SPI_GETWORKAREA, 0, out var rect, 0))
            return rect;

        return new RECT { Left = 0, Top = 0, Right = 1920, Bottom = 1040 };
    }

    // ── Native Win32 dark-mode support ─────────────────────────────────────────
    // uxtheme.dll exposes these only by ordinal (no named exports).
    // SetPreferredAppMode  = ordinal 135  (Win10 1903 / build 18362+)
    // RefreshImmersiveColorPolicyState = ordinal 104
    //
    // Calling SetPreferredAppMode(AllowDark=1) at process startup makes Windows
    // render native Win32 elements (menus, scrollbars) in dark mode whenever the
    // system theme is dark — the same mechanism Electron uses internally.
    // WinUI 3 controls are unaffected (they use their own XAML theming pipeline).
    //
    // Wrapped in try/catch: ordinal layout may differ on future Windows builds.

    [DllImport("uxtheme.dll", EntryPoint = "#135", SetLastError = false)]
    private static extern int SetPreferredAppMode(int mode);   // 0=Default 1=AllowDark 2=ForceDark 3=ForceLight

    [DllImport("uxtheme.dll", EntryPoint = "#104", SetLastError = false)]
    private static extern void RefreshImmersiveColorPolicyState();

    /// <summary>
    /// Opts the process into Windows dark-mode rendering for native Win32 UI elements
    /// (context menus, etc.).  Call once, before any UI is shown.  No-ops safely on
    /// older Windows builds where the ordinals do not exist.
    /// </summary>
    internal static void EnableDarkModeForNativeUi()
    {
        try
        {
            SetPreferredAppMode(1); // AllowDark — follows the system preference
            RefreshImmersiveColorPolicyState();
        }
        catch { /* ordinal absent on old builds — non-fatal */ }
    }

    // ── Message boxes (WinUI has no built-in MessageBox) ────────────────────────
    private const uint MB_ICONERROR = 0x10, MB_ICONQUESTION = 0x20, MB_ICONWARNING = 0x30,
                       MB_ICONINFORMATION = 0x40, MB_YESNO = 0x4, MB_TOPMOST = 0x40000;
    private const int IDYES = 6;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);

    internal static void Info(string text, string caption)
        => MessageBoxW(IntPtr.Zero, text, caption, MB_ICONINFORMATION | MB_TOPMOST);

    internal static void Warn(string text, string caption)
        => MessageBoxW(IntPtr.Zero, text, caption, MB_ICONWARNING | MB_TOPMOST);

    internal static void Error(string text, string caption)
        => MessageBoxW(IntPtr.Zero, text, caption, MB_ICONERROR | MB_TOPMOST);

    /// <summary>Shows a Yes/No prompt; returns true if the user chose Yes.</summary>
    internal static bool Confirm(string text, string caption)
        => MessageBoxW(IntPtr.Zero, text, caption, MB_YESNO | MB_ICONQUESTION | MB_TOPMOST) == IDYES;
}
