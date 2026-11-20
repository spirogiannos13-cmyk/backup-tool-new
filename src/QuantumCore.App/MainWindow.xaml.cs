using System.Windows;

namespace QuantumCore.App;

public partial class MainWindow : Window
{
    private int _backupCount;

    public MainWindow()
    {
        InitializeComponent();
    }

    private void NewBackup_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new BackupWindow { Owner = this };
        if (dialog.ShowDialog() == true)
        {
            _backupCount++;
            TotalBackupsValue.Text = _backupCount.ToString();
            TotalBackupsCaption.Text = "Backup created this session";
            LastBackupValue.Text = DateTime.Now.ToString("HH:mm");
            LastBackupCaption.Text = "Latest operation completed";
            ActivityTitle.Text = "Backup completed";
            ActivityDescription.Text = $"Saved {FormatBytes(dialog.BytesCopied)} to {dialog.CreatedBackupPath}";
            CoreStateText.Text = "DONE";
            ActivityStatusText.Text = "Backup operation completed";
        }
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double size = bytes;
        int unit = 0;
        while (size >= 1024 && unit < units.Length - 1) { size /= 1024; unit++; }
        return $"{size:0.##} {units[unit]}";
    }
}