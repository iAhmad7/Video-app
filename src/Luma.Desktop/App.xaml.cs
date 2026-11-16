using System.Windows;
using System.Windows.Threading;
using Luma.Core;
namespace Luma.Desktop;
public partial class App : Application
{
    readonly UiErrorDialogGuard uiErrors = new();

    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        TaskScheduler.UnobservedTaskException += (_, a) => { DiagnosticLog.Write("Unobserved task: " + a.Exception); a.SetObserved(); };
        base.OnStartup(e);
    }

    void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        e.Handled = true;
        DiagnosticLog.Write("Unhandled UI error: " + e.Exception);
        if (!uiErrors.TryBegin(e.Exception)) return;
        try
        {
            MessageBox.Show(e.Exception.Message + "\n\nSee Data/Logs for details. The operation may need to be repeated.\nRepeated identical notifications are suppressed for this session.",
                "Luma Toolkit", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally { uiErrors.End(); }
    }
}
