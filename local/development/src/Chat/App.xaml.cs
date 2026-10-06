namespace Chat;
public partial class App : System.Windows.Application
{
    private System.Threading.Mutex? instance;
    protected override void OnStartup(System.Windows.StartupEventArgs e)
    {
        Theme.Apply();
        // A small transparent window gains nothing from the GPU path, which loads Direct3D and the graphics driver
        // (about 70 MB of committed memory). Software rendering keeps the app light without a visible difference.
        System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
        if (BuildMode.Local) Knowledge.Core.Storage.Root = System.IO.Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData), "WindowsChat.Local");
        // One writer for each user's local history and index.
        var sid = System.Security.Principal.WindowsIdentity.GetCurrent().User?.Value;
        var name = "Local\\WindowsChat-" + (BuildMode.Local ? "Prototype-" : "") + sid;
        instance = new System.Threading.Mutex(true, name, out bool created);
        if (!created)
        {
            // Launching again brings the running window forward instead of showing an error.
            try { using var show = System.Threading.EventWaitHandle.OpenExisting(name + "-show"); show.Set(); }
            catch { System.Windows.MessageBox.Show("Chaty כבר פועל. לחצו על הכפתור בפינת המסך כדי לפתוח אותו."); }
            Shutdown();
            return;
        }
        var signal = new System.Threading.EventWaitHandle(false, System.Threading.EventResetMode.AutoReset, name + "-show");
        new System.Threading.Thread(() => { while (signal.WaitOne()) Dispatcher.BeginInvoke(() => (MainWindow as MainWindow)?.RestoreFromTaskbar()); }) { IsBackground = true, Name = "Show requests" }.Start();
        base.OnStartup(e);
    }
    protected override void OnExit(System.Windows.ExitEventArgs e)
    {
        instance?.Dispose();
        base.OnExit(e);
    }
}
