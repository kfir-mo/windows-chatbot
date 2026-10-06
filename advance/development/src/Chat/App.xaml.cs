namespace Chat;
public partial class App : System.Windows.Application
{
    private System.Threading.Mutex? instance;
    protected override void OnStartup(System.Windows.StartupEventArgs e)
    {
        Theme.Apply();
        if (BuildMode.Local) Knowledge.Core.Storage.Root = System.IO.Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData), "WindowsChat.Local");
        // One writer for each user's local history and index.
        var sid = System.Security.Principal.WindowsIdentity.GetCurrent().User?.Value;
        instance = new System.Threading.Mutex(true, "Local\\WindowsChat-" + (BuildMode.Local ? "Prototype-" : "") + sid, out bool created);
        if (!created)
        {
            System.Windows.MessageBox.Show("עוזר הידע כבר פועל. ניתן לשחזר אותו ממגש המערכת.");
            Shutdown();
            return;
        }
        base.OnStartup(e);
    }
    protected override void OnExit(System.Windows.ExitEventArgs e)
    {
        instance?.Dispose();
        base.OnExit(e);
    }
}
