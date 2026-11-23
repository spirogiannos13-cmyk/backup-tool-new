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
    private readonly CancellationTokenSource _uiShutdown = new();

    public MainWindow()
    {
        InitializeComponent();
        Loaded += MainWindow_Loaded;
        Closing += (_, _) => BeginUiShutdown();
        _dashboardTimer.Tick += (_, _) => RefreshBackupDashboard();
        _systemTimer.Tick += async (_, _) => await RefreshSystemMonitorAsync();
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        if (_uiShutdown.IsCancellationRequested) return;
        RefreshBackupDashboard();
        await RefreshSystemMonitorAsync();
        if (_uiShutdown.IsCancellationRequested) return;
        _dashboardTimer.Start();
        _systemTimer.Start();
    }

    private void BeginUiShutdown()
    {
        if (_uiShutdown.IsCancellationRequested) return;

        // Stop UI-only polling. Scheduled backups run in their own Task Scheduler
        // process and are deliberately not cancelled when this window closes.
        _dashboardTimer.Stop();
        _systemTimer.Stop();
        _uiShutdown.Cancel();
    }

    private void RefreshBackupDashboard()
    {
        var status = ScheduledBackupStatusStore.Load();
        if (status is not null && status.UpdatedAt > DateTime.Now.AddHours(-24))
        {
            BackupProgressBar.Value = status.Percent;
            BackupPercentText.Text = $"{status.Percent}%";
            BackupStageText.Text = status.Stage;
            bool isMegaUpload = status.Stage.Contains("Uploading to MEGA", StringComparison.OrdinalIgnoreCase)
                || status.Stage.Contains("upload stream", StringComparison.OrdinalIgnoreCase)
                || status.Stage.Contains("uploading backup to MEGA", StringComparison.OrdinalIgnoreCase)
                || status.Stage.Contains("retrying MEGA upload", StringComparison.OrdinalIgnoreCase);
            if (isMegaUpload && status.TotalUploadBytes > 0)
            {
                DateTime lastProgress = status.LastUploadProgressAt ?? status.UpdatedAt;
                TimeSpan sinceProgress = DateTime.Now - lastProgress;
                if (sinceProgress.TotalSeconds >= 45)
                {
                    UploadDetailsText.Text = $"NO UPLOAD PROGRESS · Last activity {FormatDuration(sinceProgress)} ago · Attempt {status.RetryAttempt}/3";
                    UploadDetailsText.Foreground = System.Windows.Media.Brushes.Orange;
                }
                else
                {
                    string speed = status.UploadSpeedBytesPerSecond > 0
                        ? $"{FormatMegabytes(status.UploadSpeedBytesPerSecond)}/s"
                        : "Calculating…";
                    long remainingBytes = Math.Max(0, status.TotalUploadBytes - status.UploadedBytes);
                    string eta = status.UploadSpeedBytesPerSecond > 0
                        ? FormatDuration(TimeSpan.FromSeconds(remainingBytes / status.UploadSpeedBytesPerSecond))
                        : "Calculating…";
                    UploadDetailsText.Text = $"{FormatMegabytes(status.UploadedBytes)} MB / {FormatMegabytes(status.TotalUploadBytes)} MB · {speed} · ETA {eta} · Attempt {status.RetryAttempt}/3";
                    UploadDetailsText.Foreground = System.Windows.Media.Brushes.LightSkyBlue;
                }
            }
            else
            {
                UploadDetailsText.Text = "";
                UploadDetailsText.Foreground = System.Windows.Media.Brushes.LightSkyBlue;
            }
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
            UploadDetailsText.Text = "";
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

    private static double FormatMegabytes(long bytes) => bytes / 1024d / 1024d;

    private static string FormatDuration(TimeSpan duration)
    {
        if (duration < TimeSpan.Zero) duration = TimeSpan.Zero;
        if (duration.TotalDays >= 1)
            return $"{(int)duration.TotalDays}d {duration.Hours:00}:{duration.Minutes:00}:{duration.Seconds:00}";
        return $"{(int)duration.TotalHours:00}:{duration.Minutes:00}:{duration.Seconds:00}";
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
            var snapshot = await SystemMonitorService.ReadAsync(_uiShutdown.Token);
            CpuInfoText.Text = "CPU · " + snapshot.Cpu;
            MemoryInfoText.Text = $"RAM · {snapshot.Memory}  ({snapshot.MemoryPercent:0}%)";
            MemoryUsageBar.Value = snapshot.MemoryPercent;
            DiskInfoText.Text = "DISKS · " + snapshot.Disk;
            DiskUsageBar.Value = snapshot.DiskPercent;
            TemperatureInfoText.Text = snapshot.Temperature;
            WindowsInfoText.Text = "OS · " + snapshot.Windows;
        }
        catch (OperationCanceledException) when (_uiShutdown.IsCancellationRequested)
        {
            // The UI is closing; do not schedule or resume monitoring work.
        }
        catch
        {
            if (!_uiShutdown.IsCancellationRequested)
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
