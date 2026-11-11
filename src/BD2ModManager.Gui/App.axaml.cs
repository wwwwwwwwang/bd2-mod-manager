using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using BD2ModManager.Core;

namespace BD2ModManager.Gui;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // 上一次自动更新留下的 BD2ModManager.exe.old 在这里清掉。
            AutoUpdater.CleanupOldFiles();

            var store = new StateStore();
            var modules = ModuleSettings.Load(store);

            // 免责声明按版本号重新确认：改过条款就重新弹一次。
            if (!modules.DisclaimerAccepted || modules.DisclaimerVersion < ModuleSettings.CurrentVersion)
            {
                desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                var gate = new DisclaimerWindow(modules);
                gate.Closed += (_, _) =>
                {
                    if (!gate.Accepted)
                    {
                        desktop.Shutdown(0);
                        return;
                    }

                    var window = CreateMainWindow();
                    desktop.MainWindow = window;
                    desktop.ShutdownMode = ShutdownMode.OnLastWindowClose;
                    window.Show();
                };
                desktop.MainWindow = gate;
                gate.Show();
            }
            else
            {
                desktop.MainWindow = CreateMainWindow();
            }
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static MainWindow CreateMainWindow()
    {
        var window = new MainWindow();
        var vm = new MainViewModel(window);
        window.DataContext = vm;
        window.Opened += (_, _) => _ = vm.InitializeAsync();
        return window;
    }
}
