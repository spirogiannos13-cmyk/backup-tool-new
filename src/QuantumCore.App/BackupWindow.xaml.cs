using CG.Web.MegaApiClient;
using Microsoft.Win32;
using System.IO;
using System.IO.Compression;
using System.Windows;
using System.Windows.Controls;

namespace QuantumCore.App;

public partial class BackupWindow : Window
{
    private readonly List<string> _sources = [];
    private CancellationTokenSource? _cancellation;
    public string? CreatedBackupPath { get; private set; }
    public long BytesCopied { get; private set; }

    public BackupWindow() => InitializeComponent();

    private void AddFiles_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Multiselect = true, Title = "Select files to back up" };
        if (dialog.ShowDialog(this) == true)
            foreach (var file in dialog.FileNames) AddSource(file);
    }

    private void AddFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Select a folder to back up", Multiselect = false };
        if (dialog.ShowDialog(this) == true) AddSource(dialog.FolderName);
    }

    private void AddSource(string path)
    {
        if (_sources.Contains(path, StringComparer.OrdinalIgnoreCase)) return;
        _sources.Add(path);
        SourcesList.Items.Add((Directory.Exists(path) ? "FOLDER  " : "FILE      ") + path);
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
    }

    private async void StartBackup_Click(object sender, RoutedEventArgs e)
    {
        if (_sources.Count == 0)
        {
            MessageBox.Show(this, "Add at least one file or folder before starting.", "Source required", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        bool useMega = DestinationModeCombo.SelectedIndex == 1;
        string? megaEmail = null;
        string? megaPassword = null;
        string? loginMfaCode = null;
        bool rememberMegaAccount = false;
        StoredMegaAccount? savedMegaAccount = null;

        if (useMega && MegaAccountStore.TryLoad(out var storedAccount) && storedAccount is not null)
        {
            savedMegaAccount = storedAccount;
        }
        else if (useMega)
        {
            var login = new MegaLoginWindow { Owner = this };
            if (login.ShowDialog() != true) return;
            megaEmail = login.Email;
            megaPassword = login.Password;
            loginMfaCode = login.MfaCode;
            rememberMegaAccount = login.RememberAccount;
        }
        else if (string.IsNullOrWhiteSpace(DestinationBox.Text) || !Directory.Exists(DestinationBox.Text))
        {
            MessageBox.Show(this, "Choose an existing destination folder.", "Destination required", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        StartButton.IsEnabled = false;
        CancelButton.Content = "Cancel operation";
        _cancellation = new CancellationTokenSource();
        string? temporaryFolder = null;

        try
        {
            string backupName = "QuantumCore_" + DateTime.Now.ToString("yyyyMMdd_HHmmss");
            string backupRoot;
            if (useMega)
            {
                temporaryFolder = Path.Combine(Path.GetTempPath(), "QuantumCore_" + Guid.NewGuid().ToString("N"));
                backupRoot = Path.Combine(temporaryFolder, backupName);
            }
            else
            {
                backupRoot = Path.Combine(DestinationBox.Text, backupName);
            }

            Directory.CreateDirectory(backupRoot);
            var files = new List<(string Source, string Root, string Relative)>();
            for (var i = 0; i < _sources.Count; i++)
            {
                var source = _sources[i];
                if (File.Exists(source))
                    files.Add((source, source, Path.GetFileName(source)));
                else if (Directory.Exists(source))
                {
                    var rootName = Path.GetFileName(Path.TrimEndingDirectorySeparator(source));
                    foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
                    {
                        _cancellation.Token.ThrowIfCancellationRequested();
                        var relative = Path.GetRelativePath(source, file);
                        files.Add((file, source, Path.Combine(rootName, relative)));
                    }
                }
            }
            if (files.Count == 0) throw new InvalidOperationException("The selected sources contain no files.");

            long totalBytes = files.Sum(f => new FileInfo(f.Source).Length);
            long copiedBytes = 0;
            int completed = 0;
            foreach (var item in files)
            {
                _cancellation.Token.ThrowIfCancellationRequested();
                var sourceIndex = _sources.FindIndex(s => string.Equals(s, item.Root, StringComparison.OrdinalIgnoreCase));
                var sourceName = Path.GetFileName(Path.TrimEndingDirectorySeparator(item.Root));
                var target = Path.Combine(backupRoot, $"{sourceIndex + 1}_{MakeSafeName(sourceName)}", item.Relative == Path.GetFileName(item.Root) ? Path.GetFileName(item.Source) : item.Relative);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                await CopyFileAsync(item.Source, target, _cancellation.Token);
                copiedBytes += new FileInfo(item.Source).Length;
                completed++;
                var percent = totalBytes == 0 ? (double)completed / files.Count * 100 : (double)copiedBytes / totalBytes * 100;
                BackupProgress.Value = percent;
                ProgressLabel.Text = $"{percent:0}%";
                StatusText.Text = $"Prepared {completed:N0} of {files.Count:N0} files";
            }

            BytesCopied = copiedBytes;
            if (useMega)
            {
                string archivePath = Path.Combine(temporaryFolder!, backupName + ".zip");
                StatusText.Text = "Packaging backup for MEGA…";
                await Task.Run(() => ZipFile.CreateFromDirectory(backupRoot, archivePath, CompressionLevel.Optimal, false), _cancellation.Token);
                _cancellation.Token.ThrowIfCancellationRequested();

                StatusText.Text = "Connecting to MEGA…";
                BackupProgress.IsIndeterminate = true;
                BackupProgress.Value = 0;
                ProgressLabel.Text = "CONNECTING";
                var client = new MegaApiClient();
                try
                {
                    if (savedMegaAccount is not null)
                    {
                        await client.LoginAsync(savedMegaAccount.ToToken())
                            .WaitAsync(TimeSpan.FromSeconds(45), _cancellation.Token);
                    }
                    else
                    {
                        var sessionToken = await client.LoginAsync(megaEmail!, megaPassword!, loginMfaCode)
                            .WaitAsync(TimeSpan.FromSeconds(45), _cancellation.Token);
                        if (rememberMegaAccount)
                            MegaAccountStore.Save(megaEmail!, sessionToken);
                    }
                    BackupProgress.IsIndeterminate = false;
                    BackupProgress.Value = 0;
                    ProgressLabel.Text = "0%";
                    StatusText.Text = "Connected to MEGA. Preparing upload…";
                    _cancellation.Token.ThrowIfCancellationRequested();
                    var root = client.GetNodes().Single(n => n.Type == NodeType.Root);
                    var remoteFolder = client.CreateFolder(backupName, root);
                    var progress = new Progress<double>(p =>
                    {
                        BackupProgress.Value = Math.Clamp(p, 0, 100);
                        ProgressLabel.Text = $"{p:0}%";
                        StatusText.Text = $"Uploading to MEGA… {p:0}%";
                    });
                    await client.UploadFileAsync(archivePath, remoteFolder, progress, _cancellation.Token);
                    CreatedBackupPath = "MEGA:/" + backupName + ".zip";
                    StatusText.Text = "MEGA UPLOAD COMPLETE";
                    ProgressLabel.Text = "100%";
                }
                finally
                {
                    BackupProgress.IsIndeterminate = false;
                    // Do not log out: MEGA logout invalidates the session token saved for future backups.
                }
            }
            else
            {
                CreatedBackupPath = backupRoot;
                StatusText.Text = $"Local backup completed · {FormatBytes(copiedBytes)}";
                ProgressLabel.Text = "100%";
            }

            MessageBox.Show(this, useMega
                ? $"Backup uploaded successfully to MEGA.\n\n{backupName}.zip"
                : $"Backup completed successfully.\n\nSaved to:\n{backupRoot}",
                "Quantum Core", MessageBoxButton.OK, MessageBoxImage.Information);
            DialogResult = true;
        }
        catch (TimeoutException)
        {
            BackupProgress.IsIndeterminate = false;
            StatusText.Text = "MEGA sign-in timed out after 45 seconds. Check your internet connection and try again.";
            StartButton.IsEnabled = true;
            CancelButton.Content = "Close";
            MessageBox.Show(this, "MEGA did not respond within 45 seconds. Check your internet connection, firewall or VPN, then try again.", "MEGA connection timed out", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (OperationCanceledException)
        {
            BackupProgress.IsIndeterminate = false;
            StatusText.Text = "Backup cancelled. Any partial local data remains in the temporary or destination folder.";
            StartButton.IsEnabled = true;
            CancelButton.Content = "Close";
        }
        catch (Exception ex)
        {
            StatusText.Text = "Backup failed. See the error details.";
            StartButton.IsEnabled = true;
            CancelButton.Content = "Close";
            MessageBox.Show(this, ex.Message, "Backup failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            BackupProgress.IsIndeterminate = false;
            if (temporaryFolder is not null && Directory.Exists(temporaryFolder))
            {
                try { Directory.Delete(temporaryFolder, true); } catch { }
            }
            if (megaPassword is not null) megaPassword = string.Empty;
            if (loginMfaCode is not null) loginMfaCode = string.Empty;
            _cancellation?.Dispose();
            _cancellation = null;
        }
    }

    private static async Task CopyFileAsync(string source, string target, CancellationToken token)
    {
        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
        await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true);
        await input.CopyToAsync(output, 81920, token);
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