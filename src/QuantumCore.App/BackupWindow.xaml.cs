using Microsoft.Win32;
using System.IO;
using System.Windows;

namespace QuantumCore.App;

public partial class BackupWindow : Window
{
    private readonly List<string> _sources = [];
    private CancellationTokenSource? _cancellation;
    public string? CreatedBackupPath { get; private set; }
    public long BytesCopied { get; private set; }

    public BackupWindow()
    {
        InitializeComponent();
    }

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

    private async void StartBackup_Click(object sender, RoutedEventArgs e)
    {
        if (_sources.Count == 0)
        {
            MessageBox.Show(this, "Add at least one file or folder before starting.", "Source required", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var destination = DestinationBox.Text;
        if (string.IsNullOrWhiteSpace(destination) || !Directory.Exists(destination))
        {
            MessageBox.Show(this, "Choose an existing destination folder.", "Destination required", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        StartButton.IsEnabled = false;
        CancelButton.Content = "Cancel operation";
        _cancellation = new CancellationTokenSource();
        try
        {
            var backupRoot = Path.Combine(destination, "QuantumCore_" + DateTime.Now.ToString("yyyyMMdd_HHmmss"));
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
                StatusText.Text = $"Copied {completed:N0} of {files.Count:N0} files";
            }

            CreatedBackupPath = backupRoot;
            BytesCopied = copiedBytes;
            StatusText.Text = $"Backup completed successfully · {FormatBytes(copiedBytes)}";
            ProgressLabel.Text = "100%";
            MessageBox.Show(this, $"Backup completed successfully.\n\nSaved to:\n{backupRoot}", "Quantum Core", MessageBoxButton.OK, MessageBoxImage.Information);
            DialogResult = true;
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Backup cancelled. The partial backup folder may be removed safely.";
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