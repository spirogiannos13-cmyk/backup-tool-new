using CG.Web.MegaApiClient;
using System.IO.Compression;
using System.Diagnostics;

namespace QuantumCore.App;

public sealed class MegaUploadProgressReporter : IProgress<double>
{
    private readonly long _totalBytes;
    private readonly int _attempt;
    private readonly Action<long, long, double, double, DateTime, int> _onProgress;
    private readonly Action _resetIdleTimeout;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private long _lastBytes;
    private double _lastSeconds;
    private double _speedBytesPerSecond;

    public MegaUploadProgressReporter(
        long totalBytes,
        int attempt,
        Action<long, long, double, double, DateTime, int> onProgress,
        Action resetIdleTimeout)
    {
        _totalBytes = Math.Max(0, totalBytes);
        _attempt = attempt;
        _onProgress = onProgress;
        _resetIdleTimeout = resetIdleTimeout;
    }

    public void Report(double value)
    {
        double percent = Math.Clamp(value, 0, 100);
        long uploaded = (long)Math.Round(_totalBytes * percent / 100d);
        double seconds = _clock.Elapsed.TotalSeconds;
        double elapsed = seconds - _lastSeconds;
        if (elapsed > 0.05 && uploaded >= _lastBytes)
        {
            double sample = (uploaded - _lastBytes) / elapsed;
            _speedBytesPerSecond = _speedBytesPerSecond <= 0 ? sample : (_speedBytesPerSecond * 0.65) + (sample * 0.35);
        }
        else if (uploaded < _lastBytes)
        {
            _speedBytesPerSecond = 0;
        }

        _lastBytes = uploaded;
        _lastSeconds = seconds;
        _resetIdleTimeout();
        _onProgress(uploaded, _totalBytes, _speedBytesPerSecond, percent, DateTime.Now, _attempt);
    }
}

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
        ScheduledBackupStatusStore.Write(scheduleId, schedule?.Name ?? "Starting", true, 2, "Initializing scheduled backup", started);
        try
        {
            schedule = ScheduleStore.Load().FirstOrDefault(s => s.Id == scheduleId)
                ?? throw new InvalidOperationException("Scheduled backup definition was not found.");
            history.ScheduleName = schedule.Name;
            ScheduledBackupStatusStore.Write(scheduleId, schedule.Name, true, 5, "Preparing backup", started);
            Log($"Starting full Business Backup '{schedule.Name}'.");

            long bytes = await BusinessBackupExecutionService.RunAsync(schedule, started, (percent, stage) =>
                ScheduledBackupStatusStore.Write(scheduleId, schedule.Name, true, percent, stage, started), CancellationToken.None);
            ScheduledBackupStatusStore.Write(scheduleId, schedule.Name, false, 100, "Completed successfully", started);
            history.Success = true;
            history.BytesWritten = bytes;
            history.Destination = "MEGA:/QuantumCore_Backup.zip";
            history.Message = $"Completed successfully. Compressed ZIP uploaded to MEGA: {FormatBytes(bytes)}.";
            Log(history.Message);
        }
        catch (Exception ex)
        {
            ScheduledBackupStatusStore.Write(scheduleId, schedule?.Name ?? "Scheduled backup", false, 0, "Failed: " + ex.Message, started);
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
    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double size = bytes;
        int unit = 0;
        while (size >= 1024 && unit < units.Length - 1) { size /= 1024; unit++; }
        return $"{size:0.##} {units[unit]}";
    }

    public static async Task<long> RunAsync(BackupSchedule schedule, DateTime startedAt, Action<int, string> reportProgress, CancellationToken token)
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
            var result = await BusinessBackupService.BuildAsync(sqlConnection, schedule.DatabaseName, staging, token,
                (percent, stage) => reportProgress(10 + (int)(percent * 0.62), stage));
            token.ThrowIfCancellationRequested();

            reportProgress(75, "Compressing full backup package");
            await Task.Run(() => ZipFile.CreateFromDirectory(staging, archive, CompressionLevel.Optimal, false), token);
            reportProgress(82, "Backup package compressed");
            if (!File.Exists(archive) || new FileInfo(archive).Length == 0)
                throw new IOException("The scheduled Business Backup ZIP is missing or empty.");

            long uploadedArchiveBytes = new FileInfo(archive).Length;
            const string finalName = "QuantumCore_Backup.zip";
            const string uploadingName = "QuantumCore_Backup.uploading.zip";
            string uploadPath = Path.Combine(stagingParent, uploadingName);
            if (File.Exists(uploadPath)) File.Delete(uploadPath);
            File.Move(archive, uploadPath);
            archive = uploadPath;

            const int maxUploadAttempts = 3;
            const int idleTimeoutSeconds = 300;
            const int totalAttemptTimeoutMinutes = 120;
            INode? uploaded = null;
            Exception? lastUploadError = null;

            for (int attempt = 1; attempt <= maxUploadAttempts; attempt++)
            {
                token.ThrowIfCancellationRequested();

                // Use a fresh client for every attempt. Never start a retry until the
                // previous UploadFileAsync task has actually completed after cancellation.
                var client = new MegaApiClient();
                using var attemptCancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
                using var totalTimeout = new CancellationTokenSource(TimeSpan.FromMinutes(totalAttemptTimeoutMinutes));
                using var idleTimeout = new CancellationTokenSource();
                using var uploadCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                    token, totalTimeout.Token, idleTimeout.Token);

                try
                {
                    reportProgress(84, "Connecting to MEGA Cloud");
                    await client.LoginAsync(account.ToToken()).WaitAsync(TimeSpan.FromSeconds(45), token);
                    var root = client.GetNodes().Single(n => n.Type == NodeType.Root);

                    var oldUploading = client.GetNodes(root)
                        .FirstOrDefault(n => n.Type == NodeType.File && n.Name == uploadingName);
                    if (oldUploading is not null)
                        await client.DeleteAsync(oldUploading, moveToTrash: false);
                    var oldFinal = client.GetNodes(root)
                        .FirstOrDefault(n => n.Type == NodeType.File && n.Name == finalName);

                    ScheduledBackupStatusStore.Write(schedule.Id, schedule.Name, true, 88,
                        "Connecting upload stream to MEGA", startedAt, 0, uploadedArchiveBytes, 0, attempt, DateTime.Now);

                    var progressReporter = new MegaUploadProgressReporter(uploadedArchiveBytes, attempt,
                        (uploadedBytes, totalBytes, speed, uploadPercent, lastProgressAt, retry) =>
                        {
                            int overallPercent = 88 + (int)Math.Round(uploadPercent * 0.09);
                            ScheduledBackupStatusStore.Write(
                                schedule.Id, schedule.Name, true, overallPercent,
                                $"Uploading to MEGA · attempt {retry}/{maxUploadAttempts}",
                                startedAt, uploadedBytes, totalBytes, speed, retry, lastProgressAt);
                            reportProgress(overallPercent,
                                $"Uploading to MEGA · {FormatBytes(uploadedBytes)} / {FormatBytes(totalBytes)}");
                        },
                        () => idleTimeout.CancelAfter(TimeSpan.FromSeconds(idleTimeoutSeconds)));

                    reportProgress(88, attempt == 1
                        ? "Uploading backup to MEGA Cloud"
                        : $"Retrying MEGA upload · attempt {attempt}/{maxUploadAttempts}");

                    Task<INode> uploadTask = client.UploadFileAsync(
                        uploadPath, root, progressReporter, uploadCancellation.Token);

                    // The timeout cancels the library's own upload token, then we await
                    // the task itself. This prevents overlapping uploads and file cleanup
                    // while MegaApiClient is still reading the ZIP.
                    using var timeoutRegistration = totalTimeout.Token.Register(() => uploadCancellation.Cancel());
                    using var idleRegistration = idleTimeout.Token.Register(() => uploadCancellation.Cancel());

                    try
                    {
                        uploaded = await uploadTask;
                    }
                    catch (OperationCanceledException) when (!token.IsCancellationRequested)
                    {
                        if (totalTimeout.IsCancellationRequested)
                            throw new TimeoutException($"MEGA upload exceeded {totalAttemptTimeoutMinutes} minutes.");
                        if (idleTimeout.IsCancellationRequested)
                            throw new TimeoutException($"No MEGA upload progress for {idleTimeoutSeconds} seconds.");
                        throw;
                    }

                    if (uploaded is null)
                        throw new IOException("MEGA did not return the uploaded file node.");

                    reportProgress(97, "Finalizing MEGA upload");
                    token.ThrowIfCancellationRequested();
                    await client.RenameAsync(uploaded, finalName);
                    if (oldFinal is not null)
                        await client.DeleteAsync(oldFinal, moveToTrash: false);

                    break;
                }
                catch (Exception ex) when (attempt < maxUploadAttempts && !token.IsCancellationRequested)
                {
                    lastUploadError = ex;
                    ScheduledBackupStatusStore.Write(schedule.Id, schedule.Name, true, 88,
                        $"MEGA upload retry scheduled · attempt {attempt + 1}/{maxUploadAttempts}",
                        startedAt, 0, uploadedArchiveBytes, 0, attempt + 1, DateTime.Now);
                    reportProgress(88, $"MEGA upload attempt {attempt} failed: {ex.Message} · retrying {attempt + 1}/{maxUploadAttempts}");
                    await Task.Delay(TimeSpan.FromSeconds(5 * attempt), token);
                }
                catch
                {
                    throw;
                }
                finally
                {
                    try { await client.LogoutAsync(); } catch { }
                }
            }

            if (uploaded is null)
                throw new IOException("MEGA upload failed after all retry attempts.", lastUploadError);

            reportProgress(97, "Finalizing MEGA upload");
            token.ThrowIfCancellationRequested();
            await client.RenameAsync(uploaded, finalName);
            if (oldFinal is not null)
                await client.DeleteAsync(oldFinal, moveToTrash: false);

            reportProgress(100, "Upload completed");
            return uploadedArchiveBytes;
        }
        finally
        {
            try { if (File.Exists(archive)) File.Delete(archive); } catch { }
            try { if (Directory.Exists(staging)) Directory.Delete(staging, true); } catch { }
        }
    }
}
