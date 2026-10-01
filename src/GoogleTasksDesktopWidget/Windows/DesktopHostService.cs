using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using GoogleTasksDesktopWidget.Infrastructure;

namespace GoogleTasksDesktopWidget.Windows;

public enum DesktopHostMode
{
    WorkerW,
    BottomMostWindow
}

public sealed class DesktopHostService(AppSettings settings, SettingsStore settingsStore) : IDisposable
{
    private const int WmDisplayChange = 0x007E;
    private const int WmExitSizeMove = 0x0232;
    private const int GwlStyle = -16;
    private const int GwlExStyle = -20;
    private const long WsChild = 0x40000000L;
    private const long WsPopup = 0x80000000L;
    private const long WsVisible = 0x10000000L;
    private const long WsExToolWindow = 0x00000080L;
    private const long WsExAppWindow = 0x00040000L;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpShowWindow = 0x0040;
    private const uint SwpFrameChanged = 0x0020;
    private static readonly IntPtr HwndBottom = new(1);
    private static readonly TimeSpan WorkerWProbeInterval = TimeSpan.FromSeconds(30);

    private Window? _window;
    private HwndSource? _source;
    private DispatcherTimer? _watchdog;
    private IntPtr _handle;
    private IntPtr _workerW;
    private uint _taskbarCreatedMessage;
    private bool _disposed;
    private bool _hasAppliedMode;
    private DateTime _nextWorkerWProbeUtc;
    private bool _updatingPosition;

    public DesktopHostMode Mode { get; private set; } = DesktopHostMode.BottomMostWindow;
    public double RestoreWidthDip => Math.Clamp(settings.WidthDip, 300, 700);
    public double RestoreHeightDip => Math.Clamp(settings.HeightDip, 300, 850);

    public void Attach(Window window)
    {
        _window = window;
        _handle = new WindowInteropHelper(window).Handle;
        _source = HwndSource.FromHwnd(_handle);
        _source?.AddHook(WindowMessageHook);
        _taskbarCreatedMessage = RegisterWindowMessage("TaskbarCreated");
        if (_taskbarCreatedMessage == 0) AppLogger.WriteEvent("DesktopHost.taskbar-message", Marshal.GetLastWin32Error());

        _watchdog = new DispatcherTimer(TimeSpan.FromSeconds(10), DispatcherPriority.Background, OnWatchdog, window.Dispatcher);
        _watchdog.Start();
        AttachToDesktop();
    }

    public void UpdatePosition()
    {
        if (_window is null || _handle == IntPtr.Zero || _updatingPosition) return;
        _updatingPosition = true;
        try
        {
            var monitor = SelectMonitor();
            var dpiScaleX = monitor.DpiX / 96d;
            var dpiScaleY = monitor.DpiY / 96d;
            var work = monitor.WorkArea;
            var requestedWidth = (int)Math.Round((_window.ActualWidth > 0 ? _window.ActualWidth : RestoreWidthDip) * dpiScaleX);
            var requestedHeight = (int)Math.Round((_window.ActualHeight > 0 ? _window.ActualHeight : RestoreHeightDip) * dpiScaleY);
            var width = Math.Clamp(requestedWidth, 1, Math.Max(1, work.Right - work.Left));
            var height = Math.Clamp(requestedHeight, 1, Math.Max(1, work.Bottom - work.Top));
            var requestedX = monitor.Bounds.Right - (int)Math.Round(settings.RightOffsetDip * dpiScaleX) - width;
            var requestedY = monitor.Bounds.Top + (int)Math.Round(settings.TopOffsetDip * dpiScaleY);
            var screenX = Math.Clamp(requestedX,
                work.Left, work.Right - width);
            var screenY = Math.Clamp(requestedY,
                work.Top, work.Bottom - height);

            var widthDip = width / dpiScaleX;
            var heightDip = height / dpiScaleY;
            if (Math.Abs(_window.Width - widthDip) > 0.5) _window.Width = widthDip;
            if (Math.Abs(_window.Height - heightDip) > 0.5) _window.Height = heightDip;

            if (_workerW != IntPtr.Zero && IsWindow(_workerW) && GetParent(_handle) == _workerW)
            {
                var parentOrigin = new NativePoint { X = 0, Y = 0 };
                _ = ClientToScreen(_workerW, ref parentOrigin);
                if (!SetWindowPos(_handle, IntPtr.Zero, screenX - parentOrigin.X, screenY - parentOrigin.Y,
                        width, height, SwpNoZOrder | SwpNoActivate | SwpShowWindow))
                    AppLogger.WriteEvent("DesktopHost.position-workerw", Marshal.GetLastWin32Error());
            }
            else if (!SetWindowPos(_handle, HwndBottom, screenX, screenY, width, height, SwpNoActivate | SwpShowWindow))
                AppLogger.WriteEvent("DesktopHost.position-bottom", Marshal.GetLastWin32Error());

            if (width != requestedWidth || height != requestedHeight || screenX != requestedX || screenY != requestedY)
            {
                settings.RightOffsetDip = Math.Max(0, (monitor.Bounds.Right - screenX - width) / dpiScaleX);
                settings.TopOffsetDip = Math.Max(0, (screenY - monitor.Bounds.Top) / dpiScaleY);
                settings.WidthDip = Math.Clamp(widthDip, 300, 700);
                settings.HeightDip = Math.Clamp(heightDip, 300, 850);
                try { settingsStore.Save(settings); }
                catch (SettingsStorageException exception) { AppLogger.WriteError("DesktopHost.position_save", exception); }
            }
        }
        finally { _updatingPosition = false; }
    }

    public void SavePosition()
    {
        if (_handle == IntPtr.Zero || !GetWindowRect(_handle, out var rect))
        {
            AppLogger.WriteEvent("DesktopHost.get-window-rect", Marshal.GetLastWin32Error());
            return;
        }
        var monitor = MonitorCatalog.FindForRect(rect);
        settings.MonitorDeviceName = monitor.DeviceName;
        settings.RightOffsetDip = Math.Clamp((monitor.Bounds.Right - rect.Right) * 96d / monitor.DpiX, 0, 10000);
        settings.TopOffsetDip = Math.Clamp((rect.Top - monitor.Bounds.Top) * 96d / monitor.DpiY, 0, 10000);
        settings.WidthDip = Math.Clamp((rect.Right - rect.Left) * 96d / monitor.DpiX, 300, 700);
        settings.HeightDip = Math.Clamp((rect.Bottom - rect.Top) * 96d / monitor.DpiY, 300, 850);
        try { settingsStore.Save(settings); }
        catch (SettingsStorageException exception) { AppLogger.WriteError("DesktopHost.position_save", exception); }
    }

    public void MoveByPhysicalDelta(int deltaX, int deltaY)
    {
        if (_window is null || _handle == IntPtr.Zero || !IsWindow(_handle) ||
            !GetWindowRect(_handle, out var rect)) return;

        var x = rect.Left + deltaX;
        var y = rect.Top + deltaY;
        if (_workerW != IntPtr.Zero && IsWindow(_workerW) && GetParent(_handle) == _workerW)
        {
            var origin = new NativePoint();
            _ = ClientToScreen(_workerW, ref origin);
            x -= origin.X;
            y -= origin.Y;
        }

        if (!SetWindowPos(_handle, IntPtr.Zero, x, y, rect.Right - rect.Left, rect.Bottom - rect.Top,
                SwpNoZOrder | SwpNoActivate | SwpShowWindow))
        {
            AppLogger.WriteEvent("DesktopHost.move", Marshal.GetLastWin32Error());
        }
    }

    private void AttachToDesktop(IntPtr discoveredWorkerW = default)
    {
        if (_window is null) return;
        if (_handle == IntPtr.Zero || !IsWindow(_handle))
        {
            var recreatedHandle = new WindowInteropHelper(_window).EnsureHandle();
            if (recreatedHandle == IntPtr.Zero || !IsWindow(recreatedHandle))
            {
                AppLogger.WriteEvent("DesktopHost.hwnd-unavailable", Marshal.GetLastWin32Error());
                return;
            }

            _source?.RemoveHook(WindowMessageHook);
            _handle = recreatedHandle;
            _source = HwndSource.FromHwnd(_handle);
            _source?.AddHook(WindowMessageHook);
            AppLogger.WriteEvent("DesktopHost.hwnd-restored");
        }

        _workerW = discoveredWorkerW != IntPtr.Zero && IsWindow(discoveredWorkerW)
            ? discoveredWorkerW : WorkerWLocator.FindWorkerW();
        _nextWorkerWProbeUtc = DateTime.UtcNow + WorkerWProbeInterval;
        var currentStyle = GetWindowLongPtr(_handle, GwlStyle).ToInt64();
        var extendedStyle = GetWindowLongPtr(_handle, GwlExStyle).ToInt64();
        extendedStyle = (extendedStyle | WsExToolWindow) & ~WsExAppWindow;
        _ = SetWindowLongPtr(_handle, GwlExStyle, new IntPtr(extendedStyle));

        if (_workerW != IntPtr.Zero && IsWindow(_workerW))
        {
            _ = SetParent(_handle, _workerW);
            if (GetParent(_handle) == _workerW)
            {
                currentStyle = (currentStyle | WsChild | WsVisible) & ~WsPopup;
                _ = SetWindowLongPtr(_handle, GwlStyle, new IntPtr(currentStyle));
                _ = SetWindowPos(_handle, IntPtr.Zero, 0, 0, 0, 0, SwpNoZOrder | SwpNoActivate | SwpShowWindow | SwpFrameChanged);
                SetMode(DesktopHostMode.WorkerW);
            }
            else
            {
                AppLogger.WriteEvent("DesktopHost.set-parent", Marshal.GetLastWin32Error());
                _workerW = IntPtr.Zero;
            }
        }

        if (_workerW == IntPtr.Zero)
        {
            _workerW = IntPtr.Zero;
            // GetParent also returns the owner for a top-level WS_POPUP window.
            // Only detach when this HWND is actually a child, such as after
            // losing a WorkerW host during an Explorer restart.
            if ((currentStyle & WsChild) != 0) _ = SetParent(_handle, IntPtr.Zero);
            currentStyle = (currentStyle | WsPopup | WsVisible) & ~WsChild;
            _ = SetWindowLongPtr(_handle, GwlStyle, new IntPtr(currentStyle));
            SetMode(DesktopHostMode.BottomMostWindow);
        }

        UpdatePosition();
    }

    private void SetMode(DesktopHostMode mode)
    {
        if (_hasAppliedMode && Mode == mode) return;
        Mode = mode;
        _hasAppliedMode = true;
        AppLogger.WriteEvent(mode == DesktopHostMode.WorkerW ? "DesktopHost.mode-workerw" : "DesktopHost.mode-bottom-most");
    }

    private IntPtr WindowMessageHook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (_taskbarCreatedMessage != 0 && message == _taskbarCreatedMessage)
        {
            _window?.Dispatcher.BeginInvoke(AttachToDesktop, DispatcherPriority.Background);
        }
        else if (message == WmDisplayChange)
        {
            _window?.Dispatcher.BeginInvoke(UpdatePosition, DispatcherPriority.Background);
        }
        else if (message == WmExitSizeMove)
        {
            _window?.Dispatcher.BeginInvoke(SavePosition, DispatcherPriority.Background);
        }

        return IntPtr.Zero;
    }

    private void OnWatchdog(object? sender, EventArgs args)
    {
        if (!IsWindow(_handle))
        {
            AttachToDesktop();
            return;
        }

        if (_workerW != IntPtr.Zero)
        {
            if (!IsWindow(_workerW) || GetParent(_handle) != _workerW) AttachToDesktop();
            return;
        }

        // A stable bottom-most WS_POPUP can have an owner, which GetParent
        // returns even though the window is not parented. Probe for a newly
        // available WorkerW without treating that owner as a reason to reattach.
        if (_hasAppliedMode && Mode == DesktopHostMode.BottomMostWindow &&
            DateTime.UtcNow < _nextWorkerWProbeUtc) return;

        _nextWorkerWProbeUtc = DateTime.UtcNow + WorkerWProbeInterval;
        var candidate = WorkerWLocator.FindWorkerW(requestWorker: false, logMissing: false);
        if (candidate != IntPtr.Zero) AttachToDesktop(candidate);
    }

    private MonitorInfo SelectMonitor()
    {
        if (!string.IsNullOrWhiteSpace(settings.MonitorDeviceName))
        {
            var match = MonitorCatalog.All.FirstOrDefault(item => string.Equals(item.DeviceName, settings.MonitorDeviceName, StringComparison.OrdinalIgnoreCase));
            if (match is not null) return match;
        }

        return MonitorCatalog.All.FirstOrDefault(item => item.IsPrimary) ?? MonitorCatalog.All[0];
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _watchdog?.Stop();
        _source?.RemoveHook(WindowMessageHook);
        _source = null;
        _window = null;
    }

    private static IntPtr GetWindowLongPtr(IntPtr window, int index) => IntPtr.Size == 8
        ? GetWindowLongPtr64(window, index)
        : new IntPtr(GetWindowLong32(window, index));

    private static IntPtr SetWindowLongPtr(IntPtr window, int index, IntPtr value) => IntPtr.Size == 8
        ? SetWindowLongPtr64(window, index, value)
        : new IntPtr(SetWindowLong32(window, index, value.ToInt32()));

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left; public int Top; public int Right; public int Bottom; }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindow(IntPtr window);
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetParent(IntPtr child, IntPtr parent);
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr GetParent(IntPtr window);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)] private static extern IntPtr GetWindowLongPtr64(IntPtr window, int index);
    [DllImport("user32.dll", EntryPoint = "GetWindowLong", SetLastError = true)] private static extern int GetWindowLong32(IntPtr window, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)] private static extern IntPtr SetWindowLongPtr64(IntPtr window, int index, IntPtr value);
    [DllImport("user32.dll", EntryPoint = "SetWindowLong", SetLastError = true)] private static extern int SetWindowLong32(IntPtr window, int index, int value);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetWindowRect(IntPtr window, out NativeRect rect);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ClientToScreen(IntPtr window, ref NativePoint point);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern uint RegisterWindowMessage(string message);

    private sealed record MonitorInfo(string DeviceName, NativeRect Bounds, NativeRect WorkArea, int DpiX, int DpiY, bool IsPrimary)
    {
        public IntPtr Handle { get; init; }
    }

    private static class MonitorCatalog
    {
        public static IReadOnlyList<MonitorInfo> All => Enumerate();

        public static MonitorInfo FindForRect(NativeRect rect)
        {
            var center = new NativePoint { X = (rect.Left + rect.Right) / 2, Y = (rect.Top + rect.Bottom) / 2 };
            var handle = MonitorFromPoint(center, 2);
            return Enumerate().FirstOrDefault(info => info.Handle == handle) ?? Enumerate()[0];
        }

        private static List<MonitorInfo> Enumerate()
        {
            var result = new List<MonitorInfo>();
            _ = EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (monitor, _, _, _) =>
            {
                var native = new MonitorInfoEx { Size = Marshal.SizeOf<MonitorInfoEx>(), DeviceName = string.Empty };
                if (!GetMonitorInfo(monitor, ref native)) return true;
                var dpiX = 96u;
                var dpiY = 96u;
                if (GetDpiForMonitor(monitor, 0, out var resolvedX, out var resolvedY) == 0)
                {
                    dpiX = resolvedX;
                    dpiY = resolvedY;
                }
                result.Add(new MonitorInfo(native.DeviceName.TrimEnd('\0'), native.Monitor, native.Work, checked((int)dpiX), checked((int)dpiY), (native.Flags & 1) != 0) { Handle = monitor });
                return true;
            }, IntPtr.Zero);

            if (result.Count == 0)
            {
                var bounds = new NativeRect { Left = 0, Top = 0, Right = 1920, Bottom = 1080 };
                result.Add(new MonitorInfo("", bounds, bounds, 96, 96, true));
            }
            return result;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct MonitorInfoEx
        {
            public int Size;
            public NativeRect Monitor;
            public NativeRect Work;
            public uint Flags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        }

        [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool EnumDisplayMonitors(IntPtr dc, IntPtr clip, MonitorEnum callback, IntPtr data);
        private delegate bool MonitorEnum(IntPtr monitor, IntPtr dc, IntPtr rect, IntPtr data);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "GetMonitorInfoW")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfoEx info);
        [DllImport("shcore.dll")] private static extern int GetDpiForMonitor(IntPtr monitor, int dpiType, out uint dpiX, out uint dpiY);
        [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr MonitorFromPoint(NativePoint point, uint flags);
    }
}
