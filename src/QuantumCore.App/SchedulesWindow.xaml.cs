using Microsoft.Data.SqlClient;
using System.Windows;
using System.Windows.Controls;

namespace QuantumCore.App;

public partial class SchedulesWindow : Window
{
    private List<BackupSchedule> _schedules = [];
    private List<SavedSqlConnection> _connections = [];
    private readonly HashSet<string> _orphanTaskIds = new(StringComparer.OrdinalIgnoreCase);
    private string? _editingScheduleId;

    public SchedulesWindow()
    {
        InitializeComponent();
        _connections = SqlConnectionStore.Load();
        SqlConnectionCombo.ItemsSource = _connections;
        if (_connections.Count > 0) SqlConnectionCombo.SelectedIndex = 0;
        _schedules = ScheduleStore.Load();
        RefreshSchedules();
        RefreshHistory();
        Loaded += async (_, _) => await RefreshSchedulesFromTaskSchedulerAsync();
    }

    private void SqlConnectionCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DatabaseCombo is not null)
        {
            DatabaseCombo.ItemsSource = null;
            StatusText.Text = "";
        }
    }

    private async void LoadDatabases_Click(object sender, RoutedEventArgs e)
    {
        if (SqlConnectionCombo.SelectedItem is not SavedSqlConnection connection)
        {
            MessageBox.Show(this, "Select a saved SQL connection first.", "SQL connection required",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            StatusText.Text = "Connecting to SQL Server…";
            DatabaseCombo.IsEnabled = false;
            await using var sql = new SqlConnection(connection.GetConnectionString("master"));
            await sql.OpenAsync();
            await using var command = new SqlCommand(
                "SELECT name FROM sys.databases WHERE database_id > 4 AND state_desc = N'ONLINE' ORDER BY name;", sql);
            await using var reader = await command.ExecuteReaderAsync();
            var databases = new List<string>();
            while (await reader.ReadAsync()) databases.Add(reader.GetString(0));
            DatabaseCombo.ItemsSource = databases;
            if (databases.Count > 0) DatabaseCombo.SelectedIndex = 0;
            StatusText.Text = databases.Count == 0
                ? "No online user databases were found."
                : $"Loaded {databases.Count} database(s).";
        }
        catch (Exception ex)
        {
            StatusText.Text = "Could not load SQL databases.";
            MessageBox.Show(this, ex.Message, "SQL connection failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally { DatabaseCombo.IsEnabled = true; }
    }

    private async void SaveSchedule_Click(object sender, RoutedEventArgs e)
    {
        if (SqlConnectionCombo.SelectedItem is not SavedSqlConnection connection)
        {
            MessageBox.Show(this, "Select a saved SQL connection.", "SQL connection required",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (DatabaseCombo.SelectedItem is not string databaseName)
        {
            MessageBox.Show(this, "Load and select the database to back up.", "Database required",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (!TimeSpan.TryParseExact(TimeBox.Text.Trim(), @"hh\:mm", null, out var time) || time.TotalHours >= 24)
        {
            MessageBox.Show(this, "Enter a valid 24-hour time, for example 03:00.", "Invalid time",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (!MegaAccountStore.TryLoad(out _))
        {
            MessageBox.Show(this,
                "No saved MEGA session was found. Open Create Backup, sign in to MEGA, and enable Remember account before scheduling.",
                "MEGA account required", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var existing = _editingScheduleId is not null
            ? _schedules.FirstOrDefault(s => s.Id == _editingScheduleId)
            : _schedules.FirstOrDefault(s =>
                s.BackupProfile.Equals("Business", StringComparison.OrdinalIgnoreCase) &&
                s.SqlConnectionId == connection.Id &&
                s.DatabaseName.Equals(databaseName, StringComparison.OrdinalIgnoreCase) &&
                s.DestinationType.Equals("MEGA", StringComparison.OrdinalIgnoreCase));
        var schedule = existing ?? new BackupSchedule();
        string oldName = schedule.Name;
        string oldTime = schedule.Time;
        string oldFrequency = schedule.Frequency;
        schedule.Name = $"Full Business Backup — {databaseName}";
        schedule.BackupProfile = "Business";
        schedule.SqlConnectionId = connection.Id;
        schedule.DatabaseName = databaseName;
        schedule.DestinationType = "MEGA";
        schedule.LocalDestination = "";
        schedule.Frequency = "Daily";
        schedule.Time = time.ToString(@"hh\:mm");

        try
        {
            if (existing is null) _schedules.Add(schedule);
            ScheduleStore.Save(_schedules);
            await WindowsTaskScheduler.RegisterAsync(schedule);
            _editingScheduleId = null;
            SaveScheduleButton.Content = "＋  SAVE DAILY SCHEDULE";
            await RefreshSchedulesFromTaskSchedulerAsync();
            StatusText.Text = $"Daily Business Backup scheduled for {schedule.Time} · MEGA Cloud.";
            MessageBox.Show(this,
                $"Full Business Backup is scheduled every day at {schedule.Time}.\n\nDatabase: {databaseName}\nSQL connection: {connection}\nDestination: MEGA Cloud\nRemote file: QuantumCore_Backup.zip (overwrite)",
                "Automation saved", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            if (existing is null) _schedules.RemoveAll(s => s.Id == schedule.Id);
            else
            {
                schedule.Name = oldName;
                schedule.Time = oldTime;
                schedule.Frequency = oldFrequency;
            }
            ScheduleStore.Save(_schedules);
            _editingScheduleId = null;
            SaveScheduleButton.Content = "＋  SAVE DAILY SCHEDULE";
            MessageBox.Show(this, ex.Message, "Could not save schedule", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void RemoveSchedule_Click(object sender, RoutedEventArgs e)
    {
        int index = SchedulesList.SelectedIndex;
        if (index < 0 || index >= _schedules.Count) return;
        var schedule = _schedules[index];
        var answer = MessageBox.Show(this, $"Remove the scheduled task '{schedule.Name}'?",
            "Remove automation", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes) return;
        try
        {
            await WindowsTaskScheduler.DeleteAsync(schedule.Id);
            _schedules.RemoveAll(s => s.Id == schedule.Id);
            _orphanTaskIds.Remove(schedule.Id);
            ScheduleStore.Save(_schedules);
            if (_editingScheduleId == schedule.Id) CancelEdit();
            await RefreshSchedulesFromTaskSchedulerAsync();
            StatusText.Text = "Scheduled task removed from Windows Task Scheduler.";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Could not remove schedule", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        _schedules = ScheduleStore.Load();
        await RefreshSchedulesFromTaskSchedulerAsync();
        RefreshHistory();
    }

    private void ModifySchedule_Click(object sender, RoutedEventArgs e)
    {
        int index = SchedulesList.SelectedIndex;
        if (index < 0 || index >= _schedules.Count) return;
        var schedule = _schedules[index];
        if (_orphanTaskIds.Contains(schedule.Id))
        {
            MessageBox.Show(this, "This Windows task no longer has its saved Quantum Core settings. You can remove it, but it cannot be edited. Create a new schedule after choosing its SQL connection and database.", "Settings unavailable", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var connection = _connections.FirstOrDefault(x => x.Id == schedule.SqlConnectionId);
        if (connection is null)
        {
            MessageBox.Show(this, "The saved SQL connection for this task was not found. Re-save the SQL connection before editing this schedule.", "SQL connection unavailable", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        SqlConnectionCombo.SelectedItem = connection;
        DatabaseCombo.ItemsSource = new[] { schedule.DatabaseName };
        DatabaseCombo.SelectedIndex = 0;
        TimeBox.Text = schedule.Time;
        _editingScheduleId = schedule.Id;
        SaveScheduleButton.Content = "✓  UPDATE SELECTED SCHEDULE";
        StatusText.Text = $"Editing: {schedule.Name}. Change the time or database, then update.";
    }

    private void CancelEdit()
    {
        _editingScheduleId = null;
        SaveScheduleButton.Content = "＋  SAVE DAILY SCHEDULE";
        StatusText.Text = "Edit cancelled.";
    }

    private async Task RefreshSchedulesFromTaskSchedulerAsync()
    {
        try
        {
            var taskIds = await WindowsTaskScheduler.ListScheduleIdsAsync();
            _orphanTaskIds.Clear();
            foreach (var id in taskIds)
            {
                if (_schedules.Any(s => s.Id.Equals(id, StringComparison.OrdinalIgnoreCase)))
                    continue;
                _orphanTaskIds.Add(id);
                _schedules.Add(new BackupSchedule
                {
                    Id = id,
                    Name = "Quantum Core task — saved settings not found",
                    Frequency = "Windows Task Scheduler",
                    Time = "—",
                    DatabaseName = "Configuration unavailable",
                    DestinationType = "Unknown"
                });
            }

            var actualIds = taskIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
            _schedules.RemoveAll(s => !_orphanTaskIds.Contains(s.Id) &&
                !actualIds.Contains(s.Id) &&
                !ScheduleStore.Load().Any(saved => saved.Id == s.Id));
            RefreshSchedules();
        }
        catch (Exception ex)
        {
            StatusText.Text = "Could not read Windows Task Scheduler: " + ex.Message;
            RefreshSchedules();
        }
    }

    private void RefreshSchedules()
    {
        SchedulesList.Items.Clear();
        foreach (var schedule in _schedules)
        {
            string detail = _orphanTaskIds.Contains(schedule.Id)
                ? "Windows task exists, but its saved Quantum Core settings are missing"
                : schedule.BackupProfile.Equals("Business", StringComparison.OrdinalIgnoreCase)
                ? $"Full Business Backup · {schedule.DatabaseName} · {schedule.DestinationType}"
                : $"{schedule.DestinationType} · legacy file/folder schedule";
            SchedulesList.Items.Add($"{schedule.Name}\n{schedule.Frequency} · {schedule.Time}\n{detail}");
        }
    }

    private void RefreshHistory()
    {
        HistoryList.Items.Clear();
        foreach (var entry in ScheduledBackupHistoryStore.Load().Take(20))
        {
            string status = entry.Success ? "SUCCESS" : "FAILED";
            string elapsed = entry.FinishedAt >= entry.StartedAt
                ? (entry.FinishedAt - entry.StartedAt).ToString(@"hh\:mm\:ss")
                : "—";
            HistoryList.Items.Add(
                $"{entry.StartedAt:yyyy-MM-dd HH:mm:ss} · {status} · {elapsed}\n{entry.ScheduleName} · {entry.Message}");
        }
        if (HistoryList.Items.Count == 0)
            HistoryList.Items.Add("No scheduled executions recorded yet.");
    }
}
