using Microsoft.Win32;
using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace QuantumCore.App;

public partial class SchedulesWindow : Window
{
    private readonly List<string> _sources = [];
    private List<BackupSchedule> _schedules = [];

    public SchedulesWindow()
    {
        InitializeComponent();
        _schedules = ScheduleStore.Load();
        RefreshSchedules();
    }

    private void AddFiles_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Multiselect = true, Title = "Select scheduled backup files" };
        if (dialog.ShowDialog(this) == true)
            foreach (var path in dialog.FileNames) AddSource(path);
    }

    private void AddFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Select scheduled backup folder" };
        if (dialog.ShowDialog(this) == true) AddSource(dialog.FolderName);
    }

    private void AddSource(string path)
    {
        if (_sources.Contains(path, StringComparer.OrdinalIgnoreCase)) return;
        _sources.Add(path);
        SourcesList.Items.Add(path);
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Choose scheduled backup destination" };
        if (dialog.ShowDialog(this) == true) LocalPathBox.Text = dialog.FolderName;
    }

    private void DestinationCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (LocalPathPanel is not null)
            LocalPathPanel.Visibility = DestinationCombo.SelectedIndex == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void FrequencyCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DayCombo is not null)
            DayCombo.Visibility = FrequencyCombo.SelectedIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void SaveSchedule_Click(object sender, RoutedEventArgs e)
    {
        if (_sources.Count == 0 || _sources.Any(p => !File.Exists(p) && !Directory.Exists(p)))
        {
            MessageBox.Show(this, "Add at least one existing source file or folder.", "Sources required", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (!TimeSpan.TryParseExact(TimeBox.Text.Trim(), @"hh\:mm", null, out var time) || time.TotalHours >= 24)
        {
            MessageBox.Show(this, "Enter a valid time in 24-hour format, for example 03:00.", "Invalid time", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        bool toMega = DestinationCombo.SelectedIndex == 1;
        if (toMega && !MegaAccountStore.TryLoad(out _))
        {
            MessageBox.Show(this, "Connect and save your MEGA account from New Backup before creating a MEGA schedule.", "MEGA account required", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (!toMega && (string.IsNullOrWhiteSpace(LocalPathBox.Text) || !Directory.Exists(LocalPathBox.Text)))
        {
            MessageBox.Show(this, "Choose an existing local destination folder.", "Destination required", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var schedule = new BackupSchedule
        {
            Name = string.IsNullOrWhiteSpace(NameBox.Text) ? "Scheduled backup" : NameBox.Text.Trim(),
            Sources = [.. _sources],
            DestinationType = toMega ? "MEGA" : "Local",
            LocalDestination = toMega ? "" : LocalPathBox.Text,
            Frequency = FrequencyCombo.SelectedIndex == 1 ? "Weekly" : "Daily",
            DayOfWeek = (DayCombo.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Monday",
            Time = time.ToString(@"hh\:mm")
        };

        try
        {
            _schedules.Add(schedule);
            ScheduleStore.Save(_schedules);
            await WindowsTaskScheduler.RegisterAsync(schedule);
            RefreshSchedules();
            _sources.Clear();
            SourcesList.Items.Clear();
            MessageBox.Show(this, $"Schedule '{schedule.Name}' was registered for {schedule.Frequency.ToLowerInvariant()} at {schedule.Time}.", "Schedule created", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            _schedules.RemoveAll(s => s.Id == schedule.Id);
            ScheduleStore.Save(_schedules);
            MessageBox.Show(this, ex.Message, "Could not create schedule", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void RemoveSchedule_Click(object sender, RoutedEventArgs e)
    {
        int index = SchedulesList.SelectedIndex;
        if (index < 0 || index >= _schedules.Count) return;
        var schedule = _schedules[index];
        try
        {
            await WindowsTaskScheduler.DeleteAsync(schedule.Id);
            _schedules.RemoveAt(index);
            ScheduleStore.Save(_schedules);
            RefreshSchedules();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Could not remove schedule", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void DisconnectMega_Click(object sender, RoutedEventArgs e)
    {
        if (!MegaAccountStore.TryLoad(out var account) || account is null)
        {
            MessageBox.Show(this, "No saved MEGA account was found.", "MEGA account", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var result = MessageBox.Show(this, $"Remove the saved MEGA session for {account.Email}? Existing MEGA schedules will remain but cannot run until you connect again.", "Disconnect MEGA", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (result == MessageBoxResult.Yes)
        {
            MegaAccountStore.Remove();
            MessageBox.Show(this, "The saved MEGA session was removed from this Windows user profile.", "MEGA disconnected", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private void RefreshSchedules()
    {
        SchedulesList.Items.Clear();
        foreach (var schedule in _schedules)
            SchedulesList.Items.Add($"{schedule.Name}\n{schedule.Frequency} · {schedule.Time}" +
                (schedule.Frequency == "Weekly" ? $" · {schedule.DayOfWeek}" : "") +
                $"\n{schedule.DestinationType} · {schedule.Sources.Count} source(s)");
    }
}
