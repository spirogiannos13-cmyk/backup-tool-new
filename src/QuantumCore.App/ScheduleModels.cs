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
