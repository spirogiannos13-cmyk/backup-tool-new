using CG.Web.MegaApiClient;
using Microsoft.Win32;
using System.IO;
using System.IO.Compression;
using System.Windows;
using System.Windows.Controls;

namespace QuantumCore.App;

public partial class BackupWindow : Window
{
    private CancellationTokenSource? _cancellation;
    private List<SavedSqlConnection> _sqlConnections = [];
    public string? CreatedBackupPath { get; private set; }
    public long BytesCopied { get; private set; }


    public BackupWindow()
    {
        InitializeComponent();
        _sqlConnections = SqlConnectionStore.Load();
        SqlConnectionCombo.ItemsSource = _sqlConnections;
        if (_sqlConnections.Count > 0) SqlConnectionCombo.SelectedIndex = 0;
        RefreshMegaAccountStatus();
    }

    private void SqlConnectionCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        SqlDatabaseCombo.ItemsSource = null;
    }

    private async void LoadSqlDatabases_Click(object sender, RoutedEventArgs e)
    {
        if (SqlConnectionCombo.SelectedItem is not SavedSqlConnection connection)
        {
            MessageBox.Show(this, "Add or select a saved SQL connection first.", "SQL connection required",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        try
        {
            StatusText.Text = "Connecting to SQL Server and loading databases…";
            StartButton.IsEnabled = false;
            await using var sql = new Microsoft.Data.SqlClient.SqlConnection(connection.GetConnectionString("master"));
            await sql.OpenAsync();
            await using var command = new Microsoft.Data.SqlClient.SqlCommand(
                "SELECT name FROM sys.databases WHERE database_id > 4 AND state_desc = N'ONLINE' ORDER BY name;", sql);
            await using var reader = await command.ExecuteReaderAsync();
            var databases = new List<string>();
            while (await reader.ReadAsync()) databases.Add(reader.GetString(0));
            SqlDatabaseCombo.ItemsSource = databases;
            if (databases.Count > 0) SqlDatabaseCombo.SelectedIndex = 0;
            StatusText.Text = databases.Count == 0 ? "No online user databases found." : $"Loaded {databases.Count} SQL database(s).";
        }
        catch (Exception ex)
        {
            StatusText.Text = "Could not load SQL databases.";
            MessageBox.Show(this, ex.Message, "SQL connection failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally { StartButton.IsEnabled = true; }
    }

    private void ManageSql_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SqlConnectionManagerWindow { Owner = this };
        dialog.ShowDialog();
        _sqlConnections = SqlConnectionStore.Load();
        SqlConnectionCombo.ItemsSource = null;
        SqlConnectionCombo.ItemsSource = _sqlConnections;
        if (_sqlConnections.Count > 0) SqlConnectionCombo.SelectedIndex = 0;
    }


    private void BrowseDestination_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Choose backup destination", Multiselect = false };
        if (dialog.ShowDialog(this) == true) DestinationBox.Text = dialog.FolderName;
    }

    private void DestinationModeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (LocalDestinationPanel is null) return;
        LocalDestinationPanel.Visibility = DestinationModeCombo.SelectedIndex == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (StatusText is not null)
            StatusText.Text = DestinationModeCombo.SelectedIndex == 1
                ? "MEGA cloud selected. You will sign in securely when you start."
                : "Choose a local folder. Source files are copied; originals are never modified.";
        if (MegaAccountPanel is not null) RefreshMegaAccountStatus();
    }

    private void RefreshMegaAccountStatus()
    {
        bool connected = MegaAccountStore.TryLoad(out var account) && account is not null;
        MegaAccountPanel.Visibility = DestinationModeCombo.SelectedIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
        MegaAccountText.Text = connected
            ? $"Remembered MEGA account: {account!.Email}"
            : "No saved MEGA account. Sign in during the next MEGA backup.";
        MegaAccountText.Foreground = connected
            ? System.Windows.Media.Brushes.LightGreen
            : System.Windows.Media.Brushes.Orange;
    }

    private void DisconnectMega_Click(object sender, RoutedEventArgs e)
    {
        if (!MegaAccountStore.TryLoad(out var account) || account is null)
        {
            RefreshMegaAccountStatus();
            return;
        }

        var answer = MessageBox.Show(this,
            $"Remove the saved MEGA session for {account.Email}?\n\nThe next MEGA backup will ask you to sign in again. Scheduled backups will also need a newly saved MEGA session.",
            "Disconnect MEGA", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes) return;

        try
        {
            MegaAccountStore.Remove();
            RefreshMegaAccountStatus();
            StatusText.Text = "Saved MEGA session removed. Sign in again on the next MEGA backup.";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Could not disconnect MEGA", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void StartBusinessBackup_Click(object sender, RoutedEventArgs e)
    {
        if (SqlConnectionCombo.SelectedItem is not SavedSqlConnection sqlConnection
            || SqlDatabaseCombo.SelectedItem is not string databaseName)
        {
            MessageBox.Show(this, "Select a saved SQL connection and load/select the database to back up.",
                "SQL database required", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        bool useMega = DestinationModeCombo.SelectedIndex == 1;
        if (!useMega && (string.IsNullOrWhiteSpace(DestinationBox.Text) || !Directory.Exists(DestinationBox.Text)))
        {
            MessageBox.Show(this, "Choose an existing local backup destination.", "Destination required",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        StoredMegaAccount? savedAccount = null;
        string? email = null, password = null, mfa = null;
        bool remember = false;
        if (useMega && MegaAccountStore.TryLoad(out var stored) && stored is not null)
            savedAccount = stored;
        else if (useMega)
        {
            var login = new MegaLoginWindow { Owner = this };
            if (login.ShowDialog() != true) return;
            email = login.Email;
            password = login.Password;
            mfa = login.MfaCode;
            remember = login.RememberAccount;
        }

        DateTime operationStartedAt = DateTime.Now;
        long backupSizeBytes = 0;
        StartButton.IsEnabled = false;
        CancelButton.Content = "Cancel operation";
        _cancellation = new CancellationTokenSource();
        string? staging = null;
        try
        {
            string backupName = "QuantumCore_Backup";
            // MEGA staging lives beside SQL Server's backup directory, whose existing
            // ACLs permit the SQL service to create database.bak. No fixed C:\database
            // dependency and no broad permission changes are needed.
            string stagingParent = useMega
                ? await SqlBackupService.GetBackupStagingParentAsync(sqlConnection, _cancellation.Token)
                : DestinationBox.Text;
            staging = Path.Combine(stagingParent, ".QuantumCore_Staging_" + Guid.NewGuid().ToString("N"));
            StatusText.Text = "Exporting Windows printers and checking required backup sources…";
            BackupProgress.IsIndeterminate = true;
            var result = await BusinessBackupService.BuildAsync(
                sqlConnection, databaseName, staging, _cancellation.Token);

            _cancellation.Token.ThrowIfCancellationRequested();
            if (useMega)
            {
                string archive = Path.Combine(stagingParent, backupName + "_" + Guid.NewGuid().ToString("N") + ".zip");
                try
                {
                    StatusText.Text = "Packaging business backup…";
                    await Task.Run(() => ZipFile.CreateFromDirectory(staging, archive, CompressionLevel.Optimal, false), _cancellation.Token);
                    backupSizeBytes = new FileInfo(archive).Length;
                    _cancellation.Token.ThrowIfCancellationRequested();

                    StatusText.Text = "Connecting to MEGA…";
                    var client = new MegaApiClient();
                    if (savedAccount is not null)
                        await client.LoginAsync(savedAccount.ToToken()).WaitAsync(TimeSpan.FromSeconds(45), _cancellation.Token);
                    else
                    {
                        var session = await client.LoginAsync(email!, password!, mfa).WaitAsync(TimeSpan.FromSeconds(45), _cancellation.Token);
                        if (remember) MegaAccountStore.Save(email!, session);
                    }

                    string pcFolderName = MegaCloudService.GetPcFolderName();
                    var progress = new Progress<double>(p =>
                    {
                        BackupProgress.IsIndeterminate = false;
                        BackupProgress.Value = Math.Clamp(p, 0, 100);
                        ProgressLabel.Text = $"{p:0}%";
                        StatusText.Text = $"Uploading business backup to MEGA / {pcFolderName}… {p:0}%";
                    });
                    await MegaCloudService.ReplacePcBackupAsync(client, archive, progress, _cancellation.Token);
                    CreatedBackupPath = $"MEGA:/{pcFolderName}/{MegaCloudService.BackupFileName}";
                }
                finally
                {
                    try { if (File.Exists(archive)) File.Delete(archive); } catch { }
                }
            }
            else
            {
                string finalDirectory = Path.Combine(DestinationBox.Text, backupName);
                PromoteCompletedBackup(staging, finalDirectory);
                staging = null;
                CreatedBackupPath = finalDirectory;
                backupSizeBytes = GetDirectorySize(finalDirectory);
            }

            BytesCopied = backupSizeBytes > 0 ? backupSizeBytes : result.BytesWritten;
            try
            {
                ScheduledBackupHistoryStore.Add(new ScheduledBackupHistoryEntry
                {
                    ScheduleId = "manual",
                    ScheduleName = "Manual Business Backup",
                    StartedAt = operationStartedAt,
                    FinishedAt = DateTime.Now,
                    Success = true,
                    Status = "SUCCESS",
                    Destination = CreatedBackupPath ?? (useMega ? "MEGA:/QuantumCore_Backup.zip" : DestinationBox.Text),
                    BytesWritten = BytesCopied,
                    Message = $"Completed successfully. {FormatBytes(BytesCopied)}."
                });
            }
            catch (Exception historyError)
            {
                StatusText.Text = $"Backup completed, but history could not be saved: {historyError.Message}";
            }

            BackupProgress.IsIndeterminate = false;
            BackupProgress.Value = 100;
            ProgressLabel.Text = "100%";
            StatusText.Text = $"Business backup completed · {FormatBytes(result.BytesWritten)}";
            MessageBox.Show(this, useMega
                ? $"Business backup uploaded successfully.\n\n{CreatedBackupPath}"
                : $"Business backup completed successfully.\n\nSaved to:\n{CreatedBackupPath}",
                "Quantum Core", MessageBoxButton.OK, MessageBoxImage.Information);
            DialogResult = true;
        }
        catch (OperationCanceledException)
        {
            TryRecordManualHistory(operationStartedAt, DateTime.Now, false, "CANCELLED",
                "CANCELLED: Backup cancelled by the user.", useMega ? "MEGA:/QuantumCore_Backup.zip" : DestinationBox.Text, 0);
            BackupProgress.IsIndeterminate = false;
            StatusText.Text = "Backup cancelled. The previous completed backup was not replaced.";
            StartButton.IsEnabled = true;
            CancelButton.Content = "Close";
        }
        catch (Exception ex)
        {
            TryRecordManualHistory(operationStartedAt, DateTime.Now, false, "FAILED",
                "FAILED: " + ex, useMega ? "MEGA:/QuantumCore_Backup.zip" : DestinationBox.Text, 0);
            BackupProgress.IsIndeterminate = false;
            StatusText.Text = "Business backup failed. The previous completed backup was not replaced.";
            StartButton.IsEnabled = true;
            CancelButton.Content = "Close";
            MessageBox.Show(this, ex.Message, "Business backup failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            if (staging is not null && Directory.Exists(staging))
            {
                try { Directory.Delete(staging, true); } catch { }
            }
            if (password is not null) password = string.Empty;
            if (mfa is not null) mfa = string.Empty;
            _cancellation?.Dispose();
            _cancellation = null;
            BackupProgress.IsIndeterminate = false;
        }
    }

    private static void TryRecordManualHistory(DateTime startedAt, DateTime finishedAt, bool success,
        string status, string message, string destination, long bytesWritten)
    {
        try
        {
            ScheduledBackupHistoryStore.Add(new ScheduledBackupHistoryEntry
            {
                ScheduleId = "manual",
                ScheduleName = "Manual Business Backup",
                StartedAt = startedAt,
                FinishedAt = finishedAt,
                Success = success,
                Status = status,
                Destination = destination,
                BytesWritten = bytesWritten,
                Message = message
            });
        }
        catch
        {
            // Preserve the original backup outcome if history storage itself is unavailable.
        }
    }

    private static long GetDirectorySize(string directory)
    {
        return Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
            .Sum(file => new FileInfo(file).Length);
    }

    private static void PromoteCompletedBackup(string stagingDirectory, string finalDirectory)
    {
        string parent = Path.GetDirectoryName(finalDirectory)!;
        Directory.CreateDirectory(parent);
        string previousDirectory = finalDirectory + ".previous";
        if (Directory.Exists(previousDirectory)) throw new IOException($"A previous backup cleanup folder already exists: {previousDirectory}. Inspect it before retrying.");

        bool movedPrevious = false;
        try
        {
            if (Directory.Exists(finalDirectory))
            {
                Directory.Move(finalDirectory, previousDirectory);
                movedPrevious = true;
            }
            Directory.Move(stagingDirectory, finalDirectory);
            if (movedPrevious)
            {
                Directory.Delete(previousDirectory, true);
            }
        }
        catch
        {
            if (!Directory.Exists(finalDirectory) && movedPrevious && Directory.Exists(previousDirectory))
                Directory.Move(previousDirectory, finalDirectory);
            throw;
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        if (_cancellation is not null) _cancellation.Cancel();
        else Close();
    }

    private static string MakeSafeName(string value)
    {
        foreach (var invalid in Path.GetInvalidFileNameChars()) value = value.Replace(invalid, '_');
        return string.IsNullOrWhiteSpace(value) ? "Source" : value;
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