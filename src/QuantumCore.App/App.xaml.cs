using System.Windows;

namespace QuantumCore.App;

public partial class App : Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var args = e.Args;
        int restoreIndex = Array.FindIndex(args, a => a.Equals("--restore-helper", StringComparison.OrdinalIgnoreCase));
        if (restoreIndex >= 0 && restoreIndex + 1 < args.Length)
        {
            try
            {
                RestoreService.RunElevatedRestore(args[restoreIndex + 1]);
                MessageBox.Show("Full system recovery completed successfully. The SQL database file was copied to C:\\sunsoftsetups and was not restored into SQL Server.", "Quantum Core recovery complete", MessageBoxButton.OK, MessageBoxImage.Information);
                Shutdown(0);
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.ToString(), "Quantum Core restore failed", MessageBoxButton.OK, MessageBoxImage.Error);
                Shutdown(1);
            }
            return;
        }

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
