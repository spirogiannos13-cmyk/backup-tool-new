using CG.Web.MegaApiClient;
using System.Diagnostics;
using System.Reflection;
using System.Windows;

namespace QuantumCore.App;

public partial class RestoreWindow : Window
{
    private MegaApiClient? _client;
    private string? _downloadedArchive;
    private QuantumCoreBackupManifest? _manifest;

    public RestoreWindow()
    {
        InitializeComponent();
        Loaded += async (_, _) => await RefreshPcFoldersAsync();
        Closed += async (_, _) =>
        {
            if (_client is not null) { try { await _client.LogoutAsync(); } catch { } }
            try { if (_downloadedArchive is not null && File.Exists(_downloadedArchive)) File.Delete(_downloadedArchive); } catch { }
        };
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshPcFoldersAsync();

    private async Task RefreshPcFoldersAsync()
    {
        RestoreButton.IsEnabled = false;
        StatusText.Text = "Connecting to MEGA…";
        try
        {
            if (!MegaAccountStore.TryLoad(out var account) || account is null)
                throw new InvalidOperationException("Connect to MEGA in Cloud & Storage and enable Remember Account first.");
            if (_client is not null) { try { await _client.LogoutAsync(); } catch { } }
            _client = new MegaApiClient();
            await _client.LoginAsync(account.ToToken()).WaitAsync(TimeSpan.FromSeconds(45));
            var folders = await MegaCloudService.ListRootFoldersAsync(_client);
            PcFoldersList.ItemsSource = folders;
            StatusText.Text = folders.Count == 0 ? "No PC backup folders were found in this MEGA account." : $"Found {folders.Count} computer backup folder(s).";
        }
        catch (Exception ex) { StatusText.Text = "Could not load MEGA backups: " + ex.Message; }
    }

    private async void PcFolders_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        RestoreButton.IsEnabled = false;
        _manifest = null;
        if (PcFoldersList.SelectedItem is not MegaCloudItem folder || _client is null) return;
        try
        {
            if (_downloadedArchive is not null) { try { File.Delete(_downloadedArchive); } catch { } }
            _downloadedArchive = null;
            var backup = _client.GetNodes(folder.Node).FirstOrDefault(n =>
                n.Type == NodeType.File && n.Name.Equals(MegaCloudService.BackupFileName, StringComparison.OrdinalIgnoreCase));
            if (backup is null) throw new FileNotFoundException("This PC folder does not contain QuantumCore_Backup.zip.");
            SelectedPcText.Text = folder.DisplayName;
            StatusText.Text = "Downloading backup metadata for validation…";
            _downloadedArchive = Path.Combine(Path.GetTempPath(), "QuantumCore-Recovery-" + Guid.NewGuid().ToString("N") + ".zip");
            await _client.DownloadFileAsync(backup, _downloadedArchive);
            _manifest = RestoreService.ReadManifest(_downloadedArchive);
            DatabaseTargetText.Text = @"C:\sunsoftsetups\" + MakeSafeFileName(_manifest.DatabaseName) + ".bak";
            BackupInfoText.Text = $"Backup created: {_manifest.CreatedAt.LocalDateTime:dd/MM/yyyy HH:mm}\nDatabase: {_manifest.DatabaseName}\nArchive size: {new FileInfo(_downloadedArchive).Length / 1024d / 1024d:0.##} MB";
            StatusText.Text = "Backup metadata is valid. Select RESTORE FULL SYSTEM when ready.";
            RestoreButton.IsEnabled = true;
        }
        catch (Exception ex)
        {
            BackupInfoText.Text = "The selected backup could not be validated.";
            StatusText.Text = ex.Message;
            RestoreButton.IsEnabled = false;
        }
    }

    private void Restore_Click(object sender, RoutedEventArgs e)
    {
        if (_downloadedArchive is null || _manifest is null || !File.Exists(_downloadedArchive)) return;
        var answer = MessageBox.Show(this,
            $"Restore the backup from {((MegaCloudItem)PcFoldersList.SelectedItem).DisplayName}?\n\nExisting configuration files, Registry settings, printers and C:\\form_path may be replaced. A safety copy of existing files will be saved under C:\\ProgramData\\QuantumCore\\PreRestore-...\n\nThe database will ONLY be copied to C:\\sunsoftsetups\\{MakeSafeFileName(_manifest.DatabaseName)}.bak. SQL Server will not be modified.\n\nWindows will ask for Administrator permission.",
            "Confirm full system recovery", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes) return;
        try
        {
            var start = new ProcessStartInfo
            {
                UseShellExecute = true,
                Verb = "runas"
            };
            string processPath = Environment.ProcessPath ?? throw new InvalidOperationException("Could not locate the Quantum Core executable.");
            if (Path.GetFileNameWithoutExtension(processPath).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
                start.FileName = processPath;
            else
                start.FileName = processPath;

            if (Path.GetFileNameWithoutExtension(processPath).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
                start.ArgumentList.Add(Assembly.GetEntryAssembly()?.Location ?? throw new InvalidOperationException("Could not locate Quantum Core assembly."));
            start.ArgumentList.Add("--restore-helper");
            start.ArgumentList.Add(_downloadedArchive);
            Process.Start(start);
            StatusText.Text = "Restore helper launched. Complete the Windows Administrator prompt to continue.";
            RestoreButton.IsEnabled = false;
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            StatusText.Text = "Administrator approval was cancelled. No files were changed.";
        }
        catch (Exception ex)
        {
            StatusText.Text = "Could not start elevated recovery: " + ex.Message;
        }
    }

    private static string MakeSafeFileName(string value)
    {
        foreach (char invalid in Path.GetInvalidFileNameChars()) value = value.Replace(invalid, '_');
        return string.IsNullOrWhiteSpace(value) ? "Database" : value;
    }
}
