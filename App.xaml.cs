using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace GhostyNav;

public partial class App : Application
{
    private Mutex? _singleInstance;

    protected override void OnStartup(StartupEventArgs e)
    {
        _singleInstance = new Mutex(true, @"Local\GhostyNav.SingleInstance", out bool isFirst);
        if (!isFirst)
        {
            Shutdown();
            return;
        }

        // A transparent (layered) window is copied back from the GPU every frame anyway, so for a
        // window this small software rendering is as fast and avoids loading the Direct3D driver stack.
        RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;

        Services.Store.MoveFromOldName(); // before anything reads settings
        Services.Theme.Apply(Services.AppSettings.Load().Accent);
        base.OnStartup(e);
        MainWindow = new MainWindow();
        MainWindow.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}
