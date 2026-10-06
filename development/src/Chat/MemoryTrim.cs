using System;
using System.Diagnostics;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace Chat;
/// <summary>Keeps the resident memory of an idle app low: when the panel closes (or work finishes and the app is idle),
/// compact the managed heap and hand unused pages back to Windows. Pages come back on demand if they are needed again.</summary>
internal static class MemoryTrim
{
    [DllImport("kernel32.dll")] private static extern bool SetProcessWorkingSetSize(IntPtr process, IntPtr minimum, IntPtr maximum);
    private static DispatcherTimer? pending;
    /// <summary>Trims after a short quiet period; a new request restarts the wait, so an active user is never interrupted.</summary>
    public static void Schedule(Dispatcher dispatcher, double seconds = 4)
    {
        pending?.Stop();
        pending = new DispatcherTimer(TimeSpan.FromSeconds(seconds), DispatcherPriority.ApplicationIdle, (_, _) => { pending?.Stop(); pending = null; Now(); }, dispatcher);
        pending.Start();
    }
    public static void Cancel() { pending?.Stop(); pending = null; }
    public static void Now()
    {
        try
        {
            GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, true, true);
            GC.WaitForPendingFinalizers();
            using var self = Process.GetCurrentProcess();
            SetProcessWorkingSetSize(self.Handle, -1, -1);
        }
        catch { }
    }
}
