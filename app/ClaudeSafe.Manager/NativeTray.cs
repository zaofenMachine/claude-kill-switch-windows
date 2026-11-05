using System.Runtime.InteropServices;

namespace ClaudeSafe.Manager;

/// <summary>Windows notification-area interop; the application UI remains entirely WinUI.</summary>
internal sealed class NativeTray : IDisposable
{
    private readonly IntPtr hwnd;
    private readonly Microsoft.UI.WindowId windowId;
    private readonly SubclassProc callback;
    private readonly Action<string> navigate;
    private readonly Func<Task> exit;
    private readonly Action shutdown;
    private readonly uint taskbarCreated = RegisterWindowMessage("TaskbarCreated");
    private const uint CallbackMessage = 0x8000 + 73;
    private NotifyIconData data;
    private readonly IntPtr customIcon;
    private bool disposed;
    public bool SystemShutdown { get; private set; }

    public NativeTray(MainWindow window, Action<string> navigate, Func<Task> exit, Action shutdown)
    {
        hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
        windowId = window.AppWindow.Id;
        this.navigate = navigate; this.exit = exit; this.shutdown = shutdown;
        callback = WndProc;
        if (!SetWindowSubclass(hwnd, callback, 73, 0)) throw new InvalidOperationException("Notification window unavailable.");
        var iconSize = (int)Math.Round(16 * DpiScale(hwnd));
        customIcon = LoadImage(IntPtr.Zero, Path.Combine(AppContext.BaseDirectory, "Assets", "ClaudeSafe.ico"), 1, iconSize, iconSize, 0x10);
        data = new NotifyIconData { Size = (uint)Marshal.SizeOf<NotifyIconData>(), Window = hwnd, Id = 1,
            Flags = 1 | 2 | 4, CallbackMessage = CallbackMessage, Icon = customIcon != IntPtr.Zero ? customIcon : LoadIcon(IntPtr.Zero, new IntPtr(32518)),
            Tip = "ClaudeSafe · 已停止", Info = "", InfoTitle = "" };
        if (!ShellNotifyIcon(0, ref data)) { RemoveWindowSubclass(hwnd, callback, 73); if (customIcon != IntPtr.Zero) DestroyIcon(customIcon); throw new InvalidOperationException("Notification icon unavailable."); }
    }
    public void Update(bool running)
    {
        var tip = "ClaudeSafe · " + (running ? "运行中" : "已停止");
        if (disposed || data.Tip == tip) return;
        data.Tip = tip; data.Flags = 4; ShellNotifyIcon(1, ref data);
    }
    public void ShowTip()
    {
        if (disposed) return;
        data.Flags = 0x10; data.InfoTitle = "ClaudeSafe 仍在运行";
        data.Info = "点击托盘图标打开。要停止内核并退出，请使用托盘菜单。"; data.InfoFlags = 1;
        ShellNotifyIcon(1, ref data);
    }
    private IntPtr WndProc(IntPtr window, uint message, UIntPtr wParam, IntPtr lParam, UIntPtr id, UIntPtr reference)
    {
        if (message == 0x24) // WM_GETMINMAXINFO: keep all controls reachable at the current DPI.
        {
            var result = DefSubclassProc(window, message, wParam, lParam);
            var bounds = Marshal.PtrToStructure<MinMaxInfo>(lParam);
            var scale = DpiScale(window);
            var area = Microsoft.UI.Windowing.DisplayArea.GetFromWindowId(windowId, Microsoft.UI.Windowing.DisplayAreaFallback.Primary).WorkArea;
            bounds.MinTrackSize.X = Math.Min((int)(960 * scale), area.Width);
            bounds.MinTrackSize.Y = Math.Min((int)(700 * scale), area.Height);
            Marshal.StructureToPtr(bounds, lParam, false);
            return result;
        }
        if (message == taskbarCreated && !disposed) { data.Flags = 1 | 2 | 4; ShellNotifyIcon(0, ref data); }
        if (message == 0x11) { SystemShutdown = true; return new IntPtr(1); }
        if (message == 0x16)
        {
            if (wParam != UIntPtr.Zero) shutdown(); else SystemShutdown = false;
        }
        if (message == CallbackMessage)
        {
            var action = unchecked((uint)lParam.ToInt64());
            if (action is 0x202 or 0x203) navigate("channel");
            if (action == 0x205) ShowMenu();
            return IntPtr.Zero;
        }
        return DefSubclassProc(window, message, wParam, lParam);
    }
    private void ShowMenu()
    {
        var menu = CreatePopupMenu();
        try
        {
            AppendMenu(menu, 0, 101, "打开 ClaudeSafe"); AppendMenu(menu, 0, 102, "订阅管理 / 更新");
            AppendMenu(menu, 0, 103, "Desktop 测速 / 定时"); AppendMenu(menu, 0x800, 0, "");
            AppendMenu(menu, 0, 104, "停止内核并退出");
            GetCursorPos(out var point); SetForegroundWindow(hwnd);
            var command = TrackPopupMenuEx(menu, 0x100 | 2, point.X, point.Y, hwnd, IntPtr.Zero);
            PostMessage(hwnd, 0, UIntPtr.Zero, IntPtr.Zero);
            if (command is >= 101 and <= 103) navigate(command == 102 ? "subscriptions" : command == 103 ? "benchmarks" : "channel");
            if (command == 104) _ = exit();
        }
        finally { DestroyMenu(menu); }
    }
    public void Dispose()
    {
        if (disposed) return; disposed = true;
        ShellNotifyIcon(2, ref data); RemoveWindowSubclass(hwnd, callback, 73);
        if (customIcon != IntPtr.Zero) DestroyIcon(customIcon);
    }
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate IntPtr SubclassProc(IntPtr window, uint message, UIntPtr wParam, IntPtr lParam, UIntPtr id, UIntPtr reference);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct NotifyIconData
    {
        public uint Size; public IntPtr Window; public uint Id, Flags, CallbackMessage; public IntPtr Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Tip;
        public uint State, StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Info;
        public uint TimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string InfoTitle;
        public uint InfoFlags; public Guid Guid; public IntPtr BalloonIcon;
    }
    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct MinMaxInfo { public Point Reserved, MaxSize, MaxPosition, MinTrackSize, MaxTrackSize; }
    internal static double DpiScale(IntPtr window) => Math.Max(96, GetDpiForWindow(window)) / 96d;
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr window);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "Shell_NotifyIconW")] private static extern bool ShellNotifyIcon(uint message, ref NotifyIconData data);
    [DllImport("comctl32.dll")] private static extern bool SetWindowSubclass(IntPtr hwnd, SubclassProc proc, nuint id, nuint reference);
    [DllImport("comctl32.dll")] private static extern bool RemoveWindowSubclass(IntPtr hwnd, SubclassProc proc, nuint id);
    [DllImport("comctl32.dll")] private static extern IntPtr DefSubclassProc(IntPtr hwnd, uint message, UIntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessage(string name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr LoadIcon(IntPtr instance, IntPtr name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "LoadImageW")] private static extern IntPtr LoadImage(IntPtr instance, string name, uint type, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr icon);
    [DllImport("user32.dll")] private static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool AppendMenu(IntPtr menu, uint flags, nuint id, string text);
    [DllImport("user32.dll")] private static extern bool DestroyMenu(IntPtr menu);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern uint TrackPopupMenuEx(IntPtr menu, uint flags, int x, int y, IntPtr window, IntPtr parameters);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr window, uint message, UIntPtr wParam, IntPtr lParam);
}
