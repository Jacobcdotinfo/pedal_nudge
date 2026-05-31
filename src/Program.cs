using System;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace PedalNudge.Windows;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);

        Application.ThreadException += (_, args) =>
        {
            AppLogger.LogException("UI thread exception", args.Exception);
            MessageBox.Show(
                $"Pedal Nudge hit an unexpected error. A log was written to:{Environment.NewLine}{AppLogger.LogPath}{Environment.NewLine}{Environment.NewLine}{args.Exception.Message}",
                "Pedal Nudge error",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        };

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception exception)
            {
                AppLogger.LogException("Unhandled exception", exception);
            }
            else
            {
                AppLogger.Log($"Unhandled exception object: {args.ExceptionObject}");
            }
        };

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            AppLogger.LogException("Unobserved task exception", args.Exception);
            args.SetObserved();
        };

        AppLogger.Log("Application starting.");
        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
        AppLogger.Log("Application exited.");
    }
}
