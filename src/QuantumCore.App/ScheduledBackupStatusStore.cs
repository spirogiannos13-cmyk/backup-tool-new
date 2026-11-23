using System.Text.Json;

namespace QuantumCore.App;

public sealed class ScheduledBackupStatus
{
    public string ScheduleId { get; set; } = "";
    public string ScheduleName { get; set; } = "";
    public bool IsRunning { get; set; }
    public int Percent { get; set; }
    public string Stage { get; set; } = "";
    public DateTime StartedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public long UploadedBytes { get; set; }
    public long TotalUploadBytes { get; set; }
    public double UploadSpeedBytesPerSecond { get; set; }
    public int RetryAttempt { get; set; }
    public DateTime? LastUploadProgressAt { get; set; }
}

public static class ScheduledBackupStatusStore
{
    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "QuantumCore", "scheduled-backup-status.json");

    public static ScheduledBackupStatus? Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return null;
            return JsonSerializer.Deserialize<ScheduledBackupStatus>(File.ReadAllText(FilePath));
        }
        catch { return null; }
    }

    public static void Write(
        string scheduleId,
        string scheduleName,
        bool running,
        int percent,
        string stage,
        DateTime startedAt,
        long uploadedBytes = 0,
        long totalUploadBytes = 0,
        double uploadSpeedBytesPerSecond = 0,
        int retryAttempt = 0,
        DateTime? lastUploadProgressAt = null)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var status = new ScheduledBackupStatus
            {
                ScheduleId = scheduleId,
                ScheduleName = scheduleName,
                IsRunning = running,
                Percent = Math.Clamp(percent, 0, 100),
                Stage = stage,
                StartedAt = startedAt,
                UpdatedAt = DateTime.Now,
                UploadedBytes = uploadedBytes,
                TotalUploadBytes = totalUploadBytes,
                UploadSpeedBytesPerSecond = Math.Max(0, uploadSpeedBytesPerSecond),
                RetryAttempt = retryAttempt,
                LastUploadProgressAt = lastUploadProgressAt
            };
            string temp = FilePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(status));
            File.Move(temp, FilePath, overwrite: true);
        }
        catch { /* Dashboard telemetry must never interrupt a backup. */ }
    }
}
