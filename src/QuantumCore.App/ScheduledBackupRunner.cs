using CG.Web.MegaApiClient;
using System.IO.Compression;

namespace QuantumCore.App;

public static class ScheduledBackupRunner
{
    public static async Task RunAsync(string scheduleId)
    {
        string logPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "QuantumCore", "scheduled-backups.log");
        Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
        void Log(string message) => File.AppendAllText(logPath,
            $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}");

        BackupSchedule? schedule = null;
        DateTime started = DateTime.Now;
        var history = new ScheduledBackupHistoryEntry { ScheduleId = scheduleId, StartedAt = started };
        try
        {
            schedule = ScheduleStore.Load().FirstOrDefault(s => s.Id == scheduleId)
                ?? throw new InvalidOperationException("Scheduled backup definition was not found.");
            history.ScheduleName = schedule.Name;
            Log($"Starting full Business Backup '{schedule.Name}'.");

            long bytes = await BusinessBackupExecutionService.RunAsync(schedule, CancellationToken.None);
            history.Success = true;
            history.BytesWritten = bytes;
            history.Destination = "MEGA:/QuantumCore_Backup.zip";
            history.Message = $"Completed successfully. Business backup payload: {FormatBytes(bytes)}.";
            Log(history.Message);
        }
        catch (Exception ex)
        {
            history.Success = false;
            history.Message = ex.ToString();
            Log("FAILED: " + ex);
            throw;
        }
        finally
        {
            history.FinishedAt = DateTime.Now;
            try { ScheduledBackupHistoryStore.Add(history); }
            catch (Exception historyError) { Log("Could not write history: " + historyError); }
        }
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

public static class BusinessBackupExecutionService
{
    public static async Task<long> RunAsync(BackupSchedule schedule, CancellationToken token)
    {
        if (!schedule.BackupProfile.Equals("Business", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("This scheduled task is not configured for the full Business Backup profile.");
        if (!schedule.DestinationType.Equals("MEGA", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The full scheduled Business Backup currently requires MEGA Cloud as its destination.");
        if (string.IsNullOrWhiteSpace(schedule.SqlConnectionId) || string.IsNullOrWhiteSpace(schedule.DatabaseName))
            throw new InvalidOperationException("The schedule is missing its saved SQL connection or database selection.");

        var sqlConnection = SqlConnectionStore.Load().FirstOrDefault(c => c.Id == schedule.SqlConnectionId)
            ?? throw new InvalidOperationException("The saved SQL connection for this schedule was not found. Open Quantum Core and review Manage SQL.");
        if (!MegaAccountStore.TryLoad(out var account) || account is null)
            throw new InvalidOperationException("The saved MEGA session is missing or expired. Open Quantum Core, sign in to MEGA, enable Remember account, and update the schedule.");

        string stagingParent = await SqlBackupService.GetBackupStagingParentAsync(sqlConnection, token);
        string staging = Path.Combine(stagingParent, ".QuantumCore_Scheduled_" + Guid.NewGuid().ToString("N"));
        string archive = Path.Combine(stagingParent, "QuantumCore_Backup_" + Guid.NewGuid().ToString("N") + ".zip");
        try
        {
            Directory.CreateDirectory(staging);
            var result = await BusinessBackupService.BuildAsync(sqlConnection, schedule.DatabaseName, staging, token);
            token.ThrowIfCancellationRequested();

            await Task.Run(() => ZipFile.CreateFromDirectory(staging, archive, CompressionLevel.Optimal, false), token);
            if (!File.Exists(archive) || new FileInfo(archive).Length == 0)
                throw new IOException("The scheduled Business Backup ZIP is missing or empty.");

            var client = new MegaApiClient();
            await client.LoginAsync(account.ToToken()).WaitAsync(TimeSpan.FromSeconds(45), token);
            var root = client.GetNodes().Single(n => n.Type == NodeType.Root);

            const string finalName = "QuantumCore_Backup.zip";
            const string uploadingName = "QuantumCore_Backup.uploading.zip";
            var oldUploading = client.GetNodes(root).FirstOrDefault(n => n.Type == NodeType.File && n.Name == uploadingName);
            if (oldUploading is not null)
                await client.DeleteAsync(oldUploading, moveToTrash: false);
            var oldFinal = client.GetNodes(root).FirstOrDefault(n => n.Type == NodeType.File && n.Name == finalName);

            string uploadPath = Path.Combine(stagingParent, uploadingName);
            if (File.Exists(uploadPath)) File.Delete(uploadPath);
            File.Move(archive, uploadPath);
            archive = uploadPath;

            INode uploaded = await client.UploadFileAsync(uploadPath, root, null, token);
            token.ThrowIfCancellationRequested();
            INode promoted = await client.RenameAsync(uploaded, finalName);
            if (oldFinal is not null)
                await client.DeleteAsync(oldFinal, moveToTrash: false);

            return result.BytesWritten;
        }
        finally
        {
            try { if (File.Exists(archive)) File.Delete(archive); } catch { }
            try { if (Directory.Exists(staging)) Directory.Delete(staging, true); } catch { }
        }
    }
}
