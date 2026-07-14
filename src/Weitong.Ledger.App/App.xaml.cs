using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace Weitong.Ledger.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += OnUiException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) => Log(args.ExceptionObject as Exception);
        base.OnStartup(e);
    }

    private void OnUiException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log(e.Exception);
        MessageBox.Show("发生错误：\n" + e.Exception.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }

    private static void Log(Exception? ex)
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "startup-error.log");
            File.WriteAllText(path, DateTime.Now + "\n" + (ex?.ToString() ?? "unknown"));
        }
        catch { }
    }
}
