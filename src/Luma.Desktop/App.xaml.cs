using System.Windows;
using Luma.Core;
namespace Luma.Desktop;
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += (_, a) => { DiagnosticLog.Write("Unhandled UI error: " + a.Exception); MessageBox.Show(a.Exception.Message + "\n\nSee Data/Logs for details. The operation may need to be repeated.", "Luma Toolkit", MessageBoxButton.OK, MessageBoxImage.Error); a.Handled = true; };
        TaskScheduler.UnobservedTaskException += (_, a) => { DiagnosticLog.Write("Unobserved task: " + a.Exception); a.SetObserved(); };
        base.OnStartup(e);
    }
}
