using System.Windows;
using System.Windows.Controls;

namespace QuantumCore.App;

public partial class MainWindow : Window
{
    private int _backupCount;

    public MainWindow()
    {
        InitializeComponent();
    }

    private void ShowPage(string section, string title, string subtitle, ScrollViewer page, Button activeNav)
    {
        OverviewPage.Visibility = Visibility.Collapsed;
        BackupsPage.Visibility = Visibility.Collapsed;
        AutomationPage.Visibility = Visibility.Collapsed;
        HistoryPage.Visibility = Visibility.Collapsed;
        SqlPage.Visibility = Visibility.Collapsed;
        CloudPage.Visibility = Visibility.Collapsed;
        SettingsPage.Visibility = Visibility.Collapsed;

        page.Visibility = Visibility.Visible;
        BreadcrumbText.Text = $"COMMAND CENTER / {section.ToUpperInvariant()}";
        PageTitleText.Text = title;
        PageSubtitleText.Text = subtitle;

        Button[] navItems = [OverviewNav, BackupsNav, AutomationNav, HistoryNav, SqlNav, CloudNav, SettingsNav];
        foreach (var item in navItems)
        {
            item.Background = System.Windows.Media.Brushes.Transparent;
            item.Foreground = (System.Windows.Media.Brush)new System.Windows.Media.BrushConverter().ConvertFromString("#A9C0DA")!;
        }

        activeNav.Background = (System.Windows.Media.Brush)new System.Windows.Media.BrushConverter().ConvertFromString("#142B45")!;
        activeNav.Foreground = (System.Windows.Media.Brush)new System.Windows.Media.BrushConverter().ConvertFromString("#55E5FF")!;
    }

    private void Overview_Click(object sender, RoutedEventArgs e) =>
        ShowPage("Overview", "Quantum Operations", "Your backup environment, unified in one command space.", OverviewPage, OverviewNav);

    private void Backups_Click(object sender, RoutedEventArgs e) =>
        ShowPage("Backup Jobs", "Backup Workspace", "Create and manage your backup operations.", BackupsPage, BackupsNav);

    private void Automation_Click(object sender, RoutedEventArgs e) =>
        ShowPage("Automation", "Scheduled Operations", "Plan and monitor your automated protection routine.", AutomationPage, AutomationNav);

    private void History_Click(object sender, RoutedEventArgs e) =>
        ShowPage("History & Logs", "Execution History", "Review scheduled backup runs and diagnostic logs.", HistoryPage, HistoryNav);

    private void Cloud_Click(object sender, RoutedEventArgs e) =>
        ShowPage("Cloud & Storage", "Cloud Destinations", "Manage the cloud destination used by your backup workflows.", CloudPage, CloudNav);

    private void Settings_Click(object sender, RoutedEventArgs e) =>
        ShowPage("Settings", "Quantum Core Settings", "Application preferences and system configuration.", SettingsPage, SettingsNav);

    private void NewBackup_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new BackupWindow { Owner = this };
        if (dialog.ShowDialog() == true)
        {
            _backupCount++;
            TotalBackupsValue.Text = _backupCount.ToString();
            TotalBackupsCaption.Text = "Backup created this session";
            LastBackupValue.Text = FormatBytes(dialog.BytesCopied);
            LastBackupTimeValue.Text = DateTime.Now.ToString("HH:mm");
            LastBackupCaption.Text = "Latest operation completed";
            ActivityTitle.Text = "Backup completed";
            ActivityDescription.Text = $"Saved {FormatBytes(dialog.BytesCopied)} to {dialog.CreatedBackupPath}";
            CoreStateText.Text = "DONE";
            ActivityStatusText.Text = "Backup operation completed";
        }
    }

    private void SqlConnections_Click(object sender, RoutedEventArgs e)
    {
        ShowPage("SQL Manager", "SQL Connection Manager", "Manage SQL Server connections and protected databases.", SqlPage, SqlNav);
        OpenSqlManager_Click(sender, e);
    }

    private void OpenSqlManager_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SqlConnectionManagerWindow { Owner = this };
        dialog.ShowDialog();
    }

    private void Schedules_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SchedulesWindow { Owner = this };
        dialog.ShowDialog();
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
