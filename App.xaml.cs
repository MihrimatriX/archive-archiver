using System.Runtime.InteropServices;
using System.Windows;

namespace ArchiveArchiver;

public partial class App : Application
{
    private static Mutex? instanceLock;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (e.Args.FirstOrDefault() == "--selftest")
        {
            AttachConsole(-1);   // WinExe has no console of its own; borrow the parent's
            try
            {
                await SelfTest.Run(e.Args.ElementAtOrDefault(1));
                Console.WriteLine("selftest ok");
                Shutdown(0);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("selftest FAILED: " + ex.Message);
                Shutdown(1);
            }
            return;
        }
        // One window only: two windows would share settings.json and could move the same files.
        instanceLock = new Mutex(true, "ArchiveArchiver.SingleInstance", out var first);
        if (!first)
        {
            System.Windows.MessageBox.Show("Arşivci zaten açık.", "Arşivci");
            Shutdown();
            return;
        }
        MetadataService.ClearCache();
        var sandbox = e.Args.FirstOrDefault() == "--deneme";
        if (sandbox) SelfTest.Sandbox();
        var w = new MainWindow();
        if (sandbox) w.AppTitleBar.Title += "  ·  DENEME (gerçek dosyalara dokunmaz)";
        w.Show();
    }

    [DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int processId);
}
