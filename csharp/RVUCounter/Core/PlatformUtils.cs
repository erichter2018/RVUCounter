using System.IO;
using System.Runtime.InteropServices;
using Serilog;

namespace RVUCounter.Core;

/// <summary>
/// Platform-specific utilities for Windows.
/// Provides multi-monitor detection and app path resolution.
/// </summary>
public static class PlatformUtils
{
    #region Win32 APIs

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip,
        MonitorEnumProc lpfnEnum, IntPtr dwData);

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MonitorInfoEx lpmi);

    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdcMonitor,
        ref Rect lprcMonitor, IntPtr dwData);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfoEx
    {
        public int Size;
        public Rect Monitor;
        public Rect WorkArea;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string DeviceName;
    }

    #endregion

    /// <summary>
    /// Get the application's base directory (where the exe or script lives).
    /// </summary>
    public static string GetAppRoot()
    {
        // For single-file publish, this returns the directory of the executable
        var exePath = Environment.ProcessPath;
        if (!string.IsNullOrEmpty(exePath))
        {
            return Path.GetDirectoryName(exePath) ?? Environment.CurrentDirectory;
        }
        return AppContext.BaseDirectory;
    }

    /// <summary>
    /// Get application paths (settings dir, data dir).
    /// </summary>
    public static (string SettingsDir, string DataDir) GetAppPaths()
    {
        var root = GetAppRoot();
        return (Config.GetSettingsPath(root), Config.GetDataPath(root));
    }

    /// <summary>
    /// Get all monitor work areas for multi-monitor support.
    /// </summary>
    public static List<System.Windows.Rect> GetMonitorWorkAreas()
    {
        var workAreas = new List<System.Windows.Rect>();

        bool MonitorEnumCallback(IntPtr hMonitor, IntPtr hdcMonitor, ref Rect lprcMonitor, IntPtr dwData)
        {
            var mi = new MonitorInfoEx { Size = Marshal.SizeOf<MonitorInfoEx>() };
            if (GetMonitorInfo(hMonitor, ref mi))
            {
                workAreas.Add(new System.Windows.Rect(
                    mi.WorkArea.Left,
                    mi.WorkArea.Top,
                    mi.WorkArea.Right - mi.WorkArea.Left,
                    mi.WorkArea.Bottom - mi.WorkArea.Top));
            }
            return true;
        }

        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, MonitorEnumCallback, IntPtr.Zero);

        return workAreas;
    }

    /// <summary>
    /// Check if a point is visible on any monitor.
    /// </summary>
    public static bool IsPointOnAnyMonitor(double x, double y)
    {
        var workAreas = GetMonitorWorkAreas();
        foreach (var area in workAreas)
        {
            if (x >= area.Left && x <= area.Right &&
                y >= area.Top && y <= area.Bottom)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Validate window position and reset to safe defaults if off-screen.
    /// </summary>
    public static (double X, double Y) ValidateWindowPosition(double x, double y, double width, double height)
    {
        // Check if the window's center is on any monitor
        double centerX = x + width / 2;
        double centerY = y + height / 2;

        if (IsPointOnAnyMonitor(centerX, centerY))
        {
            return (x, y);
        }

        // Reset to primary monitor center
        var primaryArea = GetMonitorWorkAreas().FirstOrDefault();
        if (primaryArea != default)
        {
            Log.Warning("Window position ({X}, {Y}) was off-screen, resetting to center", x, y);
            return (
                primaryArea.Left + (primaryArea.Width - width) / 2,
                primaryArea.Top + (primaryArea.Height - height) / 2
            );
        }

        return (100, 100); // Fallback
    }

    /// <summary>
    /// Ensure all required directories exist.
    /// </summary>
    public static void EnsureDirectoriesExist()
    {
        var root = GetAppRoot();
        Directory.CreateDirectory(Config.GetSettingsPath(root));
        Directory.CreateDirectory(Config.GetDataPath(root));
        Directory.CreateDirectory(Config.GetLogsPath(root));
        Directory.CreateDirectory(Path.Combine(root, Config.HelpersFolder));
    }
}
