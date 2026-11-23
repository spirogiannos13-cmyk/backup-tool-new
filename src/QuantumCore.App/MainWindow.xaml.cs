using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace QuantumCore.App;

public partial class MainWindow : Window
{
    private int _backupCount;
    private readonly DispatcherTimer _dashboardTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _systemTimer = new() { Interval = TimeSpan.FromSeconds(15) };
    private bool _refreshingSystem;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += MainWindow_Loaded;
        Closed += (_, _) => { _dashboardTimer.Stop(); _systemTimer.Stop(); };
        _dashboardTimer.Tick += (_, _) => RefreshBackupDashboard();
        _systemTimer.Tick += async (_, _) => await RefreshSystemMonitorAsync();
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        RefreshBackupDashboard();
        await RefreshSystemMonitorAsync();
        _dashboardTimer.Start();
        _systemTimer.Start();
    }

    private void RefreshBackupDashboard()
    {
        var status = ScheduledBackupStatusStore.Load();
        if (status is not null && status.UpdatedAt > DateTime.Now.AddHours(-24))
        {
            BackupProgressBar.Value = status.Percent;
            BackupPercentText.Text = $"{status.Percent}%";
            BackupStageText.Text = status.Stage;
            ActivityTitle.Text = status.ScheduleName;
            ActivityDescription.Text = $"{status.Stage} · Started {status.StartedAt:HH:mm:ss}";
            if (status.IsRunning)
            {
                CoreStateText.Text = "RUN";
                ActivityStatusText.Text = "Scheduled backup is running";
                ActivityStatusText.Foreground = System.Windows.Media.Brushes.LightGreen;
            }
            else
            {
                CoreStateText.Text = status.Percent >= 100 ? "DONE" : "IDLE";
                ActivityStatusText.Text = status.Percent >= 100 ? "Last scheduled backup completed" : "No scheduled backup is running";
                ActivityStatusText.Foreground = System.Windows.Media.Brushes.LightSkyBlue;
            }
        }
        else
        {
            BackupProgressBar.Value = 0;
            BackupPercentText.Text = "0%";
            BackupStageText.Text = "Waiting for the next scheduled operation";
            ActivityStatusText.Text = "No scheduled backup is running";
            CoreStateText.Text = "IDLE";
        }

        RefreshScheduleCountdown();
    }

    private void RefreshScheduleCountdown()
    {
        var schedules = ScheduleStore.Load();
        if (schedules.Count == 0)
        {
            ScheduleNameText.Text = "No schedule configured";
            ScheduleDetailText.Text = "No saved Quantum Core schedule was found.";
            ScheduleCountdownText.Text = "—";
            return;
        }

        var activeStatus = ScheduledBackupStatusStore.Load();
        bool currentBackupRunning = activeStatus?.IsRunning == true;
        var upcoming = schedules
            .Select(schedule =>
            {
                DateTime reference = DateTime.Now;
                if (activeStatus is not null && activeStatus.ScheduleId == schedule.Id &&
                    !activeStatus.IsRunning && activeStatus.Percent >= 100 &&
                    activeStatus.UpdatedAt > DateTime.Now.AddDays(-8))
                    reference = activeStatus.UpdatedAt;
                return new { Schedule = schedule, Next = GetNextRun(schedule, reference) };
            })
            .OrderBy(item => item.Next)
            .First();

        ScheduleNameText.Text = upcoming.Schedule.Name;
        ScheduleDetailText.Text = $"{upcoming.Schedule.Frequency} · {upcoming.Schedule.Time} · {upcoming.Schedule.DatabaseName} · {upcoming.Schedule.DestinationType}";
        if (currentBackupRunning && activeStatus?.ScheduleId == upcoming.Schedule.Id)
        {
            ScheduleCountdownText.Text = "BACKUP IS RUNNING";
            ScheduleDetailText.Text = $"{upcoming.Schedule.Frequency} · {upcoming.Schedule.Time} · {upcoming.Schedule.DatabaseName} · {upcoming.Schedule.DestinationType} · Countdown starts after completion";
            return;
        }

        var remaining = upcoming.Next - DateTime.Now;
        if (remaining < TimeSpan.Zero) remaining = TimeSpan.Zero;
        ScheduleCountdownText.Text = remaining.TotalDays >= 1
            ? $"{(int)remaining.TotalDays}d {remaining.Hours:00}:{remaining.Minutes:00}:{remaining.Seconds:00}"
            : $"{(int)remaining.TotalHours:00}:{remaining.Minutes:00}:{remaining.Seconds:00}";
    }

    private static DateTime GetNextRun(BackupSchedule schedule, DateTime now)
    {
        if (!TimeSpan.TryParse(schedule.Time, out var time)) time = new TimeSpan(3, 0, 0);
        var next = now.Date + time;
        if (schedule.Frequency.Equals("Weekly", StringComparison.OrdinalIgnoreCase))
        {
            if (!Enum.TryParse<DayOfWeek>(schedule.DayOfWeek, true, out var day)) day = DayOfWeek.Monday;
            int days = ((int)day - (int)now.DayOfWeek + 7) % 7;
            next = now.Date.AddDays(days) + time;
            if (next <= now) next = next.AddDays(7);
        }
        else if (next <= now)
        {
            next = next.AddDays(1);
        }
        return next;
    }

    private async Task RefreshSystemMonitorAsync()
    {
        if (_refreshingSystem) return;
        _refreshingSystem = true;
        try
        {
            var snapshot = await SystemMonitorService.ReadAsync();
            CpuInfoText.Text = "CPU · " + snapshot.Cpu;
            MemoryInfoText.Text = $"RAM · {snapshot.Memory}  ({snapshot.MemoryPercent:0}%)";
            MemoryUsageBar.Value = snapshot.MemoryPercent;
            DiskInfoText.Text = "DISKS · " + snapshot.Disk;
            DiskUsageBar.Value = snapshot.DiskPercent;
            TemperatureInfoText.Text = snapshot.Temperature;
            WindowsInfoText.Text = "OS · " + snapshot.Windows;
        }
        catch
        {
            TemperatureInfoText.Text = "TEMPERATURE · Sensor data unavailable";
        }
        finally { _refreshingSystem = false; }
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
        ShowPage("Overview", "Quantum Operations", "Live system health and scheduled backup monitoring.", OverviewPage, OverviewNav);
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
        RefreshBackupDashboard();
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
