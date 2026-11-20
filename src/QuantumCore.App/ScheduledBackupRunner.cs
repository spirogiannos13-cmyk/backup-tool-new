using CG.Web.MegaApiClient;
using System.IO.Compression;

namespace QuantumCore.App;

public static class ScheduledBackupRunner
{
    public static async Task RunAsync(string scheduleId)
    {
        var logPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "QuantumCore", "scheduled-backups.log");
        Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
        void Log(string message) => File.AppendAllText(logPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}");

        try
        {
            var schedule = ScheduleStore.Load().FirstOrDefault(s => s.Id == scheduleId)
                ?? throw new InvalidOperationException("Scheduled backup definition was not found.");
            Log($"Starting '{schedule.Name}'.");
            await BackupExecutionService.RunAsync(schedule, CancellationToken.None);
            Log($"Completed '{schedule.Name}'.");
        }
        catch (Exception ex)
        {
            Log("FAILED: " + ex);
            throw;
        }
    }
}

public static class BackupExecutionService
{
    public static async Task RunAsync(BackupSchedule schedule, CancellationToken token)
    {
        if (schedule.Sources.Count == 0) throw new InvalidOperationException("The schedule has no source files or folders.");
        bool toMega = schedule.DestinationType.Equals("MEGA", StringComparison.OrdinalIgnoreCase);
        if (toMega && !MegaAccountStore.TryLoad(out _))
            throw new InvalidOperationException("No saved MEGA account is available. Open Quantum Core and connect your MEGA account again.");

        string backupName = "QuantumCore_" + DateTime.Now.ToString("yyyyMMdd_HHmmss");
        string? temporary = null;
        string backupRoot;
        if (toMega)
        {
            temporary = Path.Combine(Path.GetTempPath(), "QuantumCore_Scheduled_" + Guid.NewGuid().ToString("N"));
            backupRoot = Path.Combine(temporary, backupName);
        }
        else
        {
            if (!Directory.Exists(schedule.LocalDestination))
                throw new DirectoryNotFoundException("The scheduled local destination does not exist: " + schedule.LocalDestination);
            backupRoot = Path.Combine(schedule.LocalDestination, backupName);
        }

        try
        {
            Directory.CreateDirectory(backupRoot);
            for (int i = 0; i < schedule.Sources.Count; i++)
            {
                token.ThrowIfCancellationRequested();
                string source = schedule.Sources[i];
                if (File.Exists(source))
                {
                    string targetFolder = Path.Combine(backupRoot, $"{i + 1}_{MakeSafeName(Path.GetFileNameWithoutExtension(source))}");
                    Directory.CreateDirectory(targetFolder);
                    File.Copy(source, Path.Combine(targetFolder, Path.GetFileName(source)), overwrite: false);
                }
                else if (Directory.Exists(source))
                {
                    string rootName = Path.GetFileName(Path.TrimEndingDirectorySeparator(source));
                    foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
                    {
                        token.ThrowIfCancellationRequested();
                        string relative = Path.GetRelativePath(source, file);
                        string target = Path.Combine(backupRoot, $"{i + 1}_{MakeSafeName(rootName)}", relative);
                        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                        File.Copy(file, target, overwrite: false);
                    }
                }
                else
                    throw new FileNotFoundException("A scheduled source no longer exists.", source);
            }

            if (toMega)
            {
                string archive = Path.Combine(temporary!, backupName + ".zip");
                await Task.Run(() => ZipFile.CreateFromDirectory(backupRoot, archive, CompressionLevel.Optimal, false), token);
                if (!MegaAccountStore.TryLoad(out var account) || account is null)
                    throw new InvalidOperationException("Saved MEGA session is missing or could not be decrypted.");
                var client = new MegaApiClient();
                await client.LoginAsync(account.ToToken());
                var root = client.GetNodes().Single(n => n.Type == NodeType.Root);
                var folder = client.CreateFolder(backupName, root);
                await client.UploadFileAsync(archive, folder, null, token);
                // Keep the MEGA session active: logging out invalidates the saved session token.
            }
        }
        finally
        {
            if (temporary is not null && Directory.Exists(temporary))
            {
                try { Directory.Delete(temporary, true); } catch { }
            }
        }
    }

    private static string MakeSafeName(string value)
    {
        foreach (char invalid in Path.GetInvalidFileNameChars()) value = value.Replace(invalid, '_');
        return string.IsNullOrWhiteSpace(value) ? "Source" : value;
    }
}
