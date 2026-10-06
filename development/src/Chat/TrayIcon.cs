using System;
using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace Chat;
/// <summary>The notification-area (tray) icon. Native Shell_NotifyIcon through a message-only window, so the app needs
/// no WinForms assembly (about 10 MB of extra memory). Left click toggles the panel; right click opens a small menu.</summary>
internal sealed class TrayIcon : IDisposable
{
    private const int WM_APP_TRAY = 0x8000 + 1, WM_LBUTTONUP = 0x0202, WM_RBUTTONUP = 0x0205, WM_CONTEXTMENU = 0x007B, NIN_SELECT = 0x0400, NIN_KEYSELECT = 0x0401;
    private const int NIM_ADD = 0, NIM_MODIFY = 1, NIM_DELETE = 2, NIM_SETVERSION = 4, NIF_MESSAGE = 1, NIF_ICON = 2, NIF_TIP = 4, NIF_SHOWTIP = 0x80;
    private const uint TPM_RETURNCMD = 0x100, TPM_RIGHTBUTTON = 2, TPM_LAYOUTRTL = 0x8000, MF_STRING = 0, MF_SEPARATOR = 0x800;
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public int Size; public IntPtr Window; public int Id; public int Flags; public int CallbackMessage; public IntPtr Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Tip;
        public int State, StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Info;
        public int Version;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string InfoTitle;
        public int InfoFlags; public Guid Item; public IntPtr BalloonIcon;
    }
    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern bool Shell_NotifyIcon(int message, ref NotifyIconData data);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern uint ExtractIconEx(string file, int index, IntPtr[]? large, IntPtr[] small, uint count);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr icon);
    [DllImport("user32.dll")] private static extern IntPtr LoadIcon(IntPtr instance, IntPtr name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int RegisterWindowMessage(string name);
    [DllImport("user32.dll")] private static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool AppendMenu(IntPtr menu, uint flags, UIntPtr id, string? text);
    [DllImport("user32.dll")] private static extern int TrackPopupMenuEx(IntPtr menu, uint flags, int x, int y, IntPtr window, IntPtr parameters);
    [DllImport("user32.dll")] private static extern bool DestroyMenu(IntPtr menu);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr window, int message, IntPtr w, IntPtr l);

    private readonly HwndSource window;
    private readonly IntPtr icon;
    private readonly bool ownsIcon;
    private readonly int taskbarCreated = RegisterWindowMessage("TaskbarCreated");
    private readonly Action toggle;
    private readonly (string Text, Action Run)[] menu;
    public TrayIcon(string tip, Action toggle, params (string Text, Action Run)[] menu)
    {
        this.toggle = toggle; this.menu = menu;
        // A message-only window (parent HWND_MESSAGE) receives the icon's mouse messages and is never shown.
        window = new HwndSource(new HwndSourceParameters("ChatyTray") { ParentWindow = new IntPtr(-3), Width = 0, Height = 0, WindowStyle = 0 });
        window.AddHook(Hook);
        var small = new IntPtr[1];
        if (Environment.ProcessPath != null && ExtractIconEx(Environment.ProcessPath, 0, null, small, 1) > 0 && small[0] != IntPtr.Zero) { icon = small[0]; ownsIcon = true; }
        else icon = LoadIcon(IntPtr.Zero, new IntPtr(32512));
        Tip = tip;
        Add();
    }
    public string Tip { get; set; }
    private NotifyIconData Data() => new() { Size = Marshal.SizeOf<NotifyIconData>(), Window = window.Handle, Id = 1, Flags = NIF_MESSAGE | NIF_ICON | NIF_TIP | NIF_SHOWTIP, CallbackMessage = WM_APP_TRAY, Icon = icon, Tip = Tip.Length > 127 ? Tip[..127] : Tip, Info = "", InfoTitle = "", Version = 4 };
    private void Add()
    {
        var data = Data();
        Shell_NotifyIcon(NIM_ADD, ref data);
        Shell_NotifyIcon(NIM_SETVERSION, ref data);
    }
    public void Update() { var data = Data(); Shell_NotifyIcon(NIM_MODIFY, ref data); }
    private IntPtr Hook(IntPtr hwnd, int message, IntPtr w, IntPtr l, ref bool handled)
    {
        // Explorer restarted: the notification area is new and the icon must be added again.
        if (message == taskbarCreated) { Add(); return IntPtr.Zero; }
        if (message != WM_APP_TRAY) return IntPtr.Zero;
        // With NOTIFYICON_VERSION_4 the event is in the low word of lParam.
        switch ((int)(l.ToInt64() & 0xFFFF))
        {
            case WM_LBUTTONUP or NIN_SELECT or NIN_KEYSELECT: toggle(); handled = true; break;
            case WM_RBUTTONUP or WM_CONTEXTMENU: ShowMenu(); handled = true; break;
        }
        return IntPtr.Zero;
    }
    private void ShowMenu()
    {
        var popup = CreatePopupMenu();
        try
        {
            for (int i = 0; i < menu.Length; i++)
                if (menu[i].Text == "-") AppendMenu(popup, MF_SEPARATOR, UIntPtr.Zero, null);
                else AppendMenu(popup, MF_STRING, (UIntPtr)(uint)(i + 1), menu[i].Text);
            GetCursorPos(out var point);
            // The menu closes correctly when the user clicks elsewhere only if its owner is the foreground window.
            SetForegroundWindow(window.Handle);
            var chosen = TrackPopupMenuEx(popup, TPM_RETURNCMD | TPM_RIGHTBUTTON | TPM_LAYOUTRTL, point.X, point.Y, window.Handle, IntPtr.Zero);
            PostMessage(window.Handle, 0, IntPtr.Zero, IntPtr.Zero);
            if (chosen > 0 && chosen <= menu.Length) menu[chosen - 1].Run();
        }
        finally { DestroyMenu(popup); }
    }
    public void Dispose()
    {
        var data = Data();
        Shell_NotifyIcon(NIM_DELETE, ref data);
        window.RemoveHook(Hook);
        window.Dispose();
        if (ownsIcon) DestroyIcon(icon);
    }
}
