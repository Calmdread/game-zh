using System.Threading;
using System.Windows;

namespace GameZh;

public partial class App : System.Windows.Application
{
    Mutex? instanceMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        instanceMutex = new Mutex(true, "Local\\GameZh_4AC7A8BE_OnlyInstance", out bool created);
        if (!created)
        {
            System.Windows.MessageBox.Show("GameZh 已经在运行。请查看任务栏通知区域。", "GameZh", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }
        DispatcherUnhandledException += (_, args) =>
        {
            AppLog.Write("ui_exception", args.Exception.GetType().Name);
            System.Windows.MessageBox.Show("程序遇到错误：" + args.Exception.Message + "\n请查看设置或日志。", "GameZh", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var (settings, warning) = ConfigStore.Load();
        var window = new MainWindow(settings, warning);
        MainWindow = window;
        window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { instanceMutex?.ReleaseMutex(); } catch { }
        instanceMutex?.Dispose();
        base.OnExit(e);
    }
}
