using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using System.Windows.Media.Animation;
using CG.Web.MegaApiClient;

namespace QuantumCore.App;

public partial class MainWindow : Window
{
    private readonly DispatcherTimer _dashboardTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _systemTimer = new() { Interval = TimeSpan.FromSeconds(15) };
    private bool _refreshingSystem;
    private bool? _coreActivityState;
    private ScheduledBackupStatus? _lastKnownScheduledStatus;
    private readonly CancellationTokenSource _uiShutdown = new();

    public MainWindow()
    {
        InitializeComponent();
        Loaded += MainWindow_Loaded;
        CurrentPcFolderText.Text = MegaCloudService.GetPcFolderName();
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
        RefreshBackupSummary();
        var loadedStatus = ScheduledBackupStatusStore.Load();
        if (loadedStatus is not null &&
            (_lastKnownScheduledStatus is null || loadedStatus.UpdatedAt >= _lastKnownScheduledStatus.UpdatedAt))
        {
            _lastKnownScheduledStatus = loadedStatus;
        }

        // Keep the last valid snapshot if a read lands during the status-file replacement.
        var status = _lastKnownScheduledStatus;
        SetCoreActivity(status?.IsRunning == true && status.UpdatedAt > DateTime.Now.AddHours(-24));
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
                CoreStateText.Text = "ACTIVE";
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

    private void SetCoreActivity(bool isRunning)
    {
        if (_coreActivityState == isRunning) return;
        _coreActivityState = isRunning;

        var idlePulse = (Storyboard)Resources["CorePulse"];
        var activePulse = (Storyboard)Resources["CoreActivePulse"];
        idlePulse.Stop(this);
        activePulse.Stop(this);

        if (isRunning)
            activePulse.Begin(this, true);
        else
            idlePulse.Begin(this, true);
    }

    private void RefreshHistory_Click(object sender, RoutedEventArgs e) => LoadHistoryEntries();

    private void HistoryFilter_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (HistoryEntriesGrid is not null) LoadHistoryEntries();
    }

    private void LoadHistoryEntries()
    {
        var entries = ScheduledBackupHistoryStore.Load()
            .OrderByDescending(entry => entry.FinishedAt == default ? entry.StartedAt : entry.FinishedAt)
            .ToList();

        int filter = HistoryFilterCombo?.SelectedIndex ?? 0;
        IEnumerable<ScheduledBackupHistoryEntry> filtered = entries;
        filtered = filter switch
        {
            1 => filtered.Where(entry => entry.Success),
            2 => filtered.Where(entry => !entry.Success && !entry.StatusDisplay.Equals("CANCELLED", StringComparison.OrdinalIgnoreCase)
                && !entry.Message.StartsWith("CANCELLED:", StringComparison.OrdinalIgnoreCase)),
            3 => filtered.Where(entry => entry.StatusDisplay.Equals("CANCELLED", StringComparison.OrdinalIgnoreCase)
                || entry.Message.StartsWith("CANCELLED:", StringComparison.OrdinalIgnoreCase)),
            4 => filtered.Where(entry => entry.ScheduleId.Equals("manual", StringComparison.OrdinalIgnoreCase)),
            5 => filtered.Where(entry => !entry.ScheduleId.Equals("manual", StringComparison.OrdinalIgnoreCase)),
            _ => filtered
        };

        var rows = filtered.ToList();
        HistoryEntriesGrid.ItemsSource = rows;
        HistorySummaryText.Text = $"SHOWING {rows.Count} OF {entries.Count} RECORDED OPERATIONS · HISTORY RETENTION: LAST 200";
    }

    private void RefreshBackupSummary()
    {
        var entries = ScheduledBackupHistoryStore.Load()
            .OrderByDescending(entry => entry.FinishedAt)
            .ToList();

        var successful = entries.Where(entry => entry.Success).ToList();
        TotalBackupsValue.Text = successful.Count.ToString();
        TotalBackupsCaption.Text = "Successful backups · all sessions";

        var latestSuccessful = successful.FirstOrDefault();
        LastBackupValue.Text = latestSuccessful is null || latestSuccessful.BytesWritten <= 0
            ? "—"
            : FormatBytes(latestSuccessful.BytesWritten);
        if (latestSuccessful is not null)
            LastBackupValue.ToolTip = latestSuccessful.Destination;
        else
            LastBackupValue.ClearValue(FrameworkElement.ToolTipProperty);

        var latestOperation = entries.FirstOrDefault();
        if (latestOperation is null)
        {
            LastBackupTimeValue.Text = "—";
            LastBackupCaption.Text = "No operations recorded";
            return;
        }

        DateTime operationTime = latestOperation.FinishedAt == default
            ? latestOperation.StartedAt
            : latestOperation.FinishedAt;
        LastBackupTimeValue.Text = operationTime.ToString("dd/MM HH:mm");
        LastBackupCaption.Text = latestOperation.Success
            ? "Latest operation · completed successfully"
            : "Latest operation · failed";
        LastBackupCaption.Foreground = latestOperation.Success
            ? System.Windows.Media.Brushes.LightGreen
            : System.Windows.Media.Brushes.Orange;

        ActivityTitle.Text = latestOperation.ScheduleName;
        ActivityDescription.Text = $"{(latestOperation.Success ? "Completed successfully" : "Failed")} · {operationTime:dd/MM/yyyy HH:mm}";
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
    private static double FormatMegabytes(double bytesPerSecond) => bytesPerSecond / 1024d / 1024d;

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
        page.Opacity = 0;
        page.Visibility = Visibility.Visible;
        page.BeginAnimation(UIElement.OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(240))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            });
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
    private void History_Click(object sender, RoutedEventArgs e)
    {
        ShowPage("History & Logs", "Execution History", "Review manual and scheduled backup operations.", HistoryPage, HistoryNav);
        LoadHistoryEntries();
    }
    private void RefreshMegaAccount_Click(object sender, RoutedEventArgs e) => RefreshMegaAccountPanel();

    private void RefreshMegaAccountPanel()
    {
        bool connected = MegaAccountStore.TryLoad(out var account) && account is not null;
        MegaConnectionStateText.Text = connected ? "CONNECTED" : "NOT CONNECTED";
        MegaConnectionStateText.Foreground = connected
            ? System.Windows.Media.Brushes.LightGreen
            : System.Windows.Media.Brushes.Orange;
        MegaConnectionEmailText.Text = connected ? account!.Email : "No saved MEGA account";
        MegaConnectionHintText.Text = connected
            ? "An encrypted MEGA session is saved on this Windows account."
            : "Connect an account and enable Remember Account to use scheduled cloud backups.";
        DisconnectMegaAccountButtonState(connected);
    }

    private void DisconnectMegaAccountButtonState(bool connected)
    {
        foreach (var button in FindVisualChildren<Button>(CloudPage))
        {
            if (button.Content?.ToString() == "DISCONNECT MEGA")
                button.IsEnabled = connected;
        }
    }

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
            if (child is T typed) yield return typed;
            foreach (var descendant in FindVisualChildren<T>(child)) yield return descendant;
        }
    }

    private async void ConnectMegaAccount_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new MegaLoginWindow { Owner = this };
        if (dialog.ShowDialog() != true) return;

        MegaManagementMessageText.Text = "Connecting securely to MEGA…";
        try
        {
            var client = new MegaApiClient();
            var session = await client.LoginAsync(dialog.Email, dialog.Password, dialog.MfaCode)
                .WaitAsync(TimeSpan.FromSeconds(45));
            if (dialog.RememberAccount)
            {
                MegaAccountStore.Save(dialog.Email, session);
                MegaManagementMessageText.Text = "MEGA connected. The encrypted session has been saved for scheduled backups.";
            }
            else
            {
                MegaManagementMessageText.Text = "MEGA login verified, but Remember Account was not selected. The session was not saved; scheduled backups need a saved session.";
            }
            try { await client.LogoutAsync(); } catch { }
        }
        catch (Exception ex)
        {
            MegaManagementMessageText.Text = "Could not connect to MEGA.";
            MessageBox.Show(this, ex.Message, "MEGA connection failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        RefreshMegaAccountPanel();
    }

    private void DisconnectMegaAccount_Click(object sender, RoutedEventArgs e)
    {
        if (!MegaAccountStore.TryLoad(out var account) || account is null)
        {
            RefreshMegaAccountPanel();
            return;
        }

        var answer = MessageBox.Show(this,
            $"Disconnect {account.Email}?\\n\\nThe saved encrypted session will be removed. Manual MEGA backups will ask you to sign in again, and scheduled MEGA backups will not work until a new session is saved.",
            "Disconnect MEGA", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes) return;

        try
        {
            MegaAccountStore.Remove();
            MegaManagementMessageText.Text = "MEGA account disconnected. You can connect another account at any time.";
            RefreshMegaAccountPanel();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Could not disconnect MEGA", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void Cloud_Click(object sender, RoutedEventArgs e)
    {
        ShowPage("Cloud & Storage", "MEGA Account Management", "Connect, change or disconnect your cloud account.", CloudPage, CloudNav);
        RefreshMegaAccountPanel();
        _ = RefreshMegaCloudAsync();
    }
    private async Task<MegaApiClient> CreateSavedMegaClientAsync()
    {
        if (!MegaAccountStore.TryLoad(out var account) || account is null)
            throw new InvalidOperationException("Connect to MEGA and enable Remember Account before browsing cloud backups.");
        var client = new MegaApiClient();
        await client.LoginAsync(account.ToToken()).WaitAsync(TimeSpan.FromSeconds(45));
        return client;
    }

    private async void RefreshMegaCloud_Click(object sender, RoutedEventArgs e) =>
        await RefreshMegaCloudAsync();

    private async Task RefreshMegaCloudAsync()
    {
        try
        {
            MegaCloudStatusText.Text = "Connecting to MEGA and loading PC folders…";
            var client = await CreateSavedMegaClientAsync();
            var folders = await MegaCloudService.ListRootFoldersAsync(client);
            MegaPcFoldersList.ItemsSource = folders;
            MegaPcFilesList.ItemsSource = null;
            MegaPcFilesInfoText.Text = "Select a computer folder to view its backup.";
            MegaCloudStatusText.Text = folders.Count == 0
                ? "No Quantum Core PC folders found yet. The first backup from this PC will create its folder automatically."
                : $"Found {folders.Count} PC folder(s) in MEGA.";
            try { await client.LogoutAsync(); } catch { }
        }
        catch (Exception ex)
        {
            MegaCloudStatusText.Text = "Could not load MEGA folders: " + ex.Message;
        }
    }

    private async void MegaPcFolders_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (MegaPcFoldersList?.SelectedItem is not MegaCloudItem selected || !selected.IsFolder) return;
        try
        {
            var client = await CreateSavedMegaClientAsync();
            var files = await MegaCloudService.ListFolderContentsAsync(client, selected.Node);
            MegaPcFilesList.ItemsSource = files;
            MegaPcFilesInfoText.Text = files.Count == 0
                ? "This PC folder is empty."
                : $"{files.Count} file(s) · select a backup to delete it.";
            try { await client.LogoutAsync(); } catch { }
        }
        catch (Exception ex)
        {
            MegaPcFilesInfoText.Text = "Could not read this folder: " + ex.Message;
        }
    }

    private async void DeleteMegaCloudBackup_Click(object sender, RoutedEventArgs e)
    {
        if (MegaPcFilesList.SelectedItem is not MegaCloudItem selected || selected.IsFolder) return;
        var answer = MessageBox.Show(this,
            $"Move '{selected.DisplayName}' to the MEGA rubbish bin? This cannot be undone from Quantum Core.",
            "Delete cloud backup", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes) return;
        try
        {
            var client = await CreateSavedMegaClientAsync();
            await MegaCloudService.DeleteFileAsync(client, selected.Node);
            MegaCloudStatusText.Text = $"Deleted {selected.DisplayName} from MEGA.";
            await RefreshSelectedMegaPcFolderAsync();
            try { await client.LogoutAsync(); } catch { }
        }
        catch (Exception ex)
        {
            MegaCloudStatusText.Text = "Could not delete the selected backup: " + ex.Message;
        }
    }

    private async Task RefreshSelectedMegaPcFolderAsync()
    {
        if (MegaPcFoldersList.SelectedItem is not MegaCloudItem folder) return;
        var client = await CreateSavedMegaClientAsync();
        var files = await MegaCloudService.ListFolderContentsAsync(client, folder.Node);
        MegaPcFilesList.ItemsSource = files;
        MegaPcFilesInfoText.Text = files.Count == 0
            ? "This PC folder is empty."
            : $"{files.Count} file(s) · select a backup to delete it.";
        try { await client.LogoutAsync(); } catch { }
    }

    private void Settings_Click(object sender, RoutedEventArgs e) =>
        ShowPage("Settings", "Quantum Core Settings", "Application preferences and system configuration.", SettingsPage, SettingsNav);

    private void DisasterRecovery_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new RestoreWindow { Owner = this };
        dialog.ShowDialog();
        RefreshBackupDashboard();
    }

    private void NewBackup_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new BackupWindow { Owner = this };
        if (dialog.ShowDialog() == true)
            RefreshBackupSummary();
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
