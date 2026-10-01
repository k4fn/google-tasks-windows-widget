using System.Runtime.InteropServices;
using GoogleTasksDesktopWidget.Infrastructure;

namespace GoogleTasksDesktopWidget.Windows;

internal static class WorkerWLocator
{
    private const uint WmSpawnWorker = 0x052C;
    private const uint SmtoNormal = 0x0000;

    public static IntPtr FindWorkerW(bool requestWorker = true, bool logMissing = true)
    {
        if (requestWorker)
        {
            var progman = FindWindow("Progman", null);
            if (progman != IntPtr.Zero)
            {
                if (SendMessageTimeout(progman, WmSpawnWorker, IntPtr.Zero, IntPtr.Zero, SmtoNormal, 1000, out _) == IntPtr.Zero)
                {
                    AppLogger.WriteEvent("WorkerW.spawn-message", Marshal.GetLastWin32Error());
                }
            }
            else AppLogger.WriteEvent("WorkerW.progman-missing", Marshal.GetLastWin32Error());
        }

        var found = IntPtr.Zero;
        if (!EnumWindows((topLevel, _) =>
        {
            var desktopView = FindWindowEx(topLevel, IntPtr.Zero, "SHELLDLL_DefView", null);
            if (desktopView == IntPtr.Zero) return true;
            found = FindWindowEx(IntPtr.Zero, topLevel, "WorkerW", null);
            return found == IntPtr.Zero;
        }, IntPtr.Zero))
        {
            AppLogger.WriteEvent("WorkerW.enum-windows", Marshal.GetLastWin32Error());
        }

        if (found == IntPtr.Zero && logMissing) AppLogger.WriteEvent("WorkerW.not-found");

        return found;
    }

    private delegate bool EnumWindowsCallback(IntPtr window, IntPtr parameter);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr FindWindow(string? className, string? windowName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr afterChild, string? className, string? windowName);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsCallback callback, IntPtr parameter);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SendMessageTimeout(
        IntPtr window,
        uint message,
        IntPtr wParam,
        IntPtr lParam,
        uint flags,
        uint timeout,
        out IntPtr result);
}
