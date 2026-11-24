using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

namespace QuantumCore.App;

public sealed class BackupSchedule
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Scheduled backup";
    public List<string> Sources { get; set; } = [];
    public string DestinationType { get; set; } = "Local";
    public string LocalDestination { get; set; } = "";
    public string Frequency { get; set; } = "Daily";
    public string DayOfWeek { get; set; } = "Monday";
    public string Time { get; set; } = "03:00";
    public string BackupProfile { get; set; } = "Business";
    public string SqlConnectionId { get; set; } = "";
    public string DatabaseName { get; set; } = "";
}

public static class ScheduleStore
{
    private static readonly byte[] Entropy = "QuantumCore.Schedules.v1"u8.ToArray();
    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "QuantumCore", "schedules.dat");

    public static List<BackupSchedule> Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return [];
            var json = ProtectedData.Unprotect(File.ReadAllBytes(FilePath), Entropy, DataProtectionScope.CurrentUser);
            return JsonSerializer.Deserialize<List<BackupSchedule>>(json) ?? [];
        }
        catch { return []; }
    }

    public static void Save(List<BackupSchedule> schedules)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(schedules, new JsonSerializerOptions { WriteIndented = true });
        var encrypted = ProtectedData.Protect(json, Entropy, DataProtectionScope.CurrentUser);
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllBytes(FilePath, encrypted);
    }
}

public static class WindowsTaskScheduler
{
    private const string Prefix = "QuantumCore_Backup_";

    public static async Task RegisterAsync(BackupSchedule schedule, CancellationToken cancellationToken = default)
    {
        // Re-create the Windows task instead of relying on schtasks /Create /F to
        // update an existing trigger. This ensures a changed start time is applied.
        await DeleteAsync(schedule.Id, cancellationToken);

        var taskName = Prefix + schedule.Id;
        var appHost = Environment.ProcessPath ?? throw new InvalidOperationException("Cannot determine application executable path.");
        var assemblyPath = System.Reflection.Assembly.GetEntryAssembly()?.Location
            ?? throw new InvalidOperationException("Cannot determine application assembly path.");
        var appHostName = Path.GetFileNameWithoutExtension(appHost);
        string taskCommand;
        if (appHostName.Equals("QuantumCore", StringComparison.OrdinalIgnoreCase))
            taskCommand = $"\"{appHost}\" --run-schedule \"{schedule.Id}\"";
        else
            taskCommand = $"\"dotnet\" \"{assemblyPath}\" --run-schedule \"{schedule.Id}\"";

        var args = new List<string> { "/Create", "/SC", schedule.Frequency.Equals("Weekly", StringComparison.OrdinalIgnoreCase) ? "WEEKLY" : "DAILY" };
        if (schedule.Frequency.Equals("Weekly", StringComparison.OrdinalIgnoreCase))
        {
            var day = Enum.TryParse<DayOfWeek>(schedule.DayOfWeek, true, out var parsed) ? parsed : DayOfWeek.Monday;
            args.AddRange(["/D", day.ToString()[..3].ToUpperInvariant()]);
        }
        args.AddRange(["/ST", schedule.Time, "/TN", taskName, "/TR", taskCommand, "/F", "/IT"]);
        await RunSchtasksAsync(args, cancellationToken);
    }

    public static async Task DeleteAsync(string id, CancellationToken cancellationToken = default)
    {
        await RunSchtasksAsync(["/Delete", "/TN", Prefix + id, "/F"], cancellationToken, allowMissing: true);
    }

    public static async Task<List<string>> ListScheduleIdsAsync(CancellationToken cancellationToken = default)
    {
        var start = new ProcessStartInfo("schtasks.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add("/Query");
        start.ArgumentList.Add("/FO");
        start.ArgumentList.Add("CSV");
        start.ArgumentList.Add("/NH");

        using var process = Process.Start(start)
            ?? throw new InvalidOperationException("Could not read Windows Task Scheduler.");
        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        var output = await outputTask;
        var error = await errorTask;
        if (process.ExitCode != 0)
            throw new InvalidOperationException("Windows Task Scheduler query failed. " + error.Trim());

        var ids = new List<string>();
        foreach (var line in output.Split(new[] { '\\r', '\\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var firstColumn = line.Split(',')[0].Trim().Trim('"');
            var taskName = firstColumn[(firstColumn.LastIndexOf('\\\\') + 1)..];
            if (taskName.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
                ids.Add(taskName[Prefix.Length..]);
        }
        return ids.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static async Task RunSchtasksAsync(IEnumerable<string> arguments, CancellationToken token, bool allowMissing = false)
    {
        var start = new ProcessStartInfo("schtasks.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start Windows Task Scheduler.");
        var stdout = process.StandardOutput.ReadToEndAsync(token);
        var stderr = process.StandardError.ReadToEndAsync(token);
        await process.WaitForExitAsync(token);
        var output = (await stdout) + Environment.NewLine + (await stderr);
        if (process.ExitCode != 0 && !allowMissing)
            throw new InvalidOperationException("Windows Task Scheduler could not register the backup. " + output.Trim());
    }
}


public sealed class ScheduledBackupHistoryEntry
{
    public string ScheduleId { get; set; } = "";
    public string ScheduleName { get; set; } = "";
    public DateTime StartedAt { get; set; }
    public DateTime FinishedAt { get; set; }
    public bool Success { get; set; }
    public string Status { get; set; } = "";
    public string Destination { get; set; } = "MEGA:/QuantumCore_Backup.zip";
    public long BytesWritten { get; set; }
    public string Message { get; set; } = "";

    public string StatusDisplay => !string.IsNullOrWhiteSpace(Status)
        ? Status
        : (Message.StartsWith("CANCELLED:", StringComparison.OrdinalIgnoreCase) ? "CANCELLED"
            : Success ? "SUCCESS" : "FAILED");
    public string OperationDisplay => ScheduleId.Equals("manual", StringComparison.OrdinalIgnoreCase)
        ? "Manual Business Backup" : (string.IsNullOrWhiteSpace(ScheduleName) ? "Scheduled Backup" : ScheduleName);
    public string StartedDisplay => StartedAt == default ? "—" : StartedAt.ToString("dd/MM/yy HH:mm:ss");
    public string DurationDisplay
    {
        get
        {
            if (StartedAt == default || FinishedAt == default) return "—";
            var duration = FinishedAt - StartedAt;
            return duration.TotalHours >= 1 ? $"{(int)duration.TotalHours}h {duration.Minutes}m"
                : duration.TotalMinutes >= 1 ? $"{(int)duration.TotalMinutes}m {duration.Seconds}s"
                : $"{Math.Max(0, duration.TotalSeconds):0}s";
        }
    }
    public string SizeDisplay
    {
        get
        {
            if (BytesWritten <= 0) return "—";
            string[] units = ["B", "KB", "MB", "GB", "TB"];
            double size = BytesWritten;
            int unit = 0;
            while (size >= 1024 && unit < units.Length - 1) { size /= 1024; unit++; }
            return $"{size:0.##} {units[unit]}";
        }
    }
}

public static class ScheduledBackupHistoryStore
{
    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "QuantumCore", "scheduled-backup-history.json");

    public static List<ScheduledBackupHistoryEntry> Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return [];
            return System.Text.Json.JsonSerializer.Deserialize<List<ScheduledBackupHistoryEntry>>(File.ReadAllText(FilePath)) ?? [];
        }
        catch { return []; }
    }

    public static void Add(ScheduledBackupHistoryEntry entry)
    {
        var entries = Load();
        entries.Insert(0, entry);
        if (entries.Count > 200) entries = entries.Take(200).ToList();
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var temp = FilePath + ".tmp";
        File.WriteAllText(temp, System.Text.Json.JsonSerializer.Serialize(entries, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        File.Move(temp, FilePath, overwrite: true);
    }
}
