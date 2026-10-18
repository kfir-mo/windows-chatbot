using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Chat;
/// <summary>Ctrl+Alt+Space opens and closes Chaty from anywhere, also while another program has the focus: the key is
/// registered with Windows (RegisterHotKey), which sends it to Chaty. If another program already took it, Chaty works as before.</summary>
public partial class MainWindow
{
    private const string HotKeyName = "Ctrl+Alt+Space";
    private const int HotKeyId = 0x4348, WmHotKey = 0x0312;
    private const uint ModAlt = 0x1, ModControl = 0x2, ModNoRepeat = 0x4000, VkSpace = 0x20;
    [DllImport("user32.dll", SetLastError = true)] private static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr window, int id);
    private HwndSource? hotKeySource;
    private void RegisterOpenKey()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (hotKeySource != null || !RegisterHotKey(handle, HotKeyId, ModControl | ModAlt | ModNoRepeat, VkSpace)) return;
        hotKeySource = HwndSource.FromHwnd(handle);
        hotKeySource.AddHook(HotKeyHook);
        Closed += (_, _) => { UnregisterHotKey(handle, HotKeyId); hotKeySource?.RemoveHook(HotKeyHook); hotKeySource = null; };
    }
    private IntPtr HotKeyHook(IntPtr window, int message, IntPtr id, IntPtr data, ref bool handled)
    {
        if (message != WmHotKey || id.ToInt32() != HotKeyId) return IntPtr.Zero;
        handled = true;
        // Open but behind another program: it comes to the front instead of closing.
        if (IsVisible && expanded && WindowState != WindowState.Minimized && IsActive) CollapsePanel(this, new());
        else RestoreFromTaskbar();
        return IntPtr.Zero;
    }
}
