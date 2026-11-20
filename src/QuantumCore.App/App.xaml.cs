using System.Windows;

namespace QuantumCore.App;

public partial class App : Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var args = e.Args;
        int runIndex = Array.FindIndex(args, a => a.Equals("--run-schedule", StringComparison.OrdinalIgnoreCase));
        if (runIndex >= 0 && runIndex + 1 < args.Length)
        {
            try
            {
                await ScheduledBackupRunner.RunAsync(args[runIndex + 1]);
                Shutdown(0);
            }
            catch
            {
                Shutdown(1);
            }
            return;
        }

        var main = new MainWindow();
        MainWindow = main;
        main.Show();
    }
}
