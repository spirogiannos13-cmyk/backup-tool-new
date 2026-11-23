using System.Diagnostics;
using System.IO.Compression;
using System.ComponentModel;

namespace QuantumCore.App;

public sealed record BusinessBackupResult(long BytesWritten, IReadOnlyList<string> CreatedFiles);

public static class BusinessBackupService
{
    private const string TaxProviderConfig = @"C:\Program Files (x86)\Sunsoft Ltd\ExternalTaxProvider\External.Tax.Provider\appsettings.production.json";
    private const string WebServiceConfig = @"C:\Program Files (x86)\Sunsoft Ltd\AmvrosiaWebService\Amvrosia.Web.Service\web.config";
    private const string FormPath = @"C:\form_path";
    private const string PrinterExportFileName = "printers.printerExport";

    private static readonly (string RegistryKey, string FileName)[] RegistryExports =
    [
        (@"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Sunsoft\System\Printers", "printers.reg"),
        (@"HKEY_CURRENT_USER\Software\VB and VBA Program Settings\spim_POSSystem", "vb.reg")
    ];

    public static async Task<BusinessBackupResult> BuildAsync(
        SavedSqlConnection sqlConnection,
        string databaseName,
        string stagingDirectory,
        CancellationToken token,
        Action<int, string>? progress = null)
    {
        if (!File.Exists(TaxProviderConfig))
            throw new FileNotFoundException("Required configuration file was not found.", TaxProviderConfig);
        if (!File.Exists(WebServiceConfig))
            throw new FileNotFoundException("Required web.config was not found.", WebServiceConfig);
        if (!Directory.Exists(FormPath))
            throw new DirectoryNotFoundException("Required form_path folder was not found: " + FormPath);
        if (sqlConnection is null || string.IsNullOrWhiteSpace(databaseName))
            throw new InvalidOperationException("Select a SQL Server connection and database before starting the business backup.");

        Directory.CreateDirectory(stagingDirectory);
        var created = new List<string>();
        long bytes = 0;

        void Report(int step, string label) => progress?.Invoke(step * 100 / 7, label);
        token.ThrowIfCancellationRequested();
        Report(0, "Exporting Windows printers");
        string printerTarget = Path.Combine(stagingDirectory, PrinterExportFileName);
        await RunStageAsync("Windows printer export (printers.printerExport)", () => ExportWindowsPrintersAsync(printerTarget, token));
        created.Add(printerTarget);
        Report(1, "Copying tax provider configuration");
        bytes += new FileInfo(printerTarget).Length;

        string taxTarget = Path.Combine(stagingDirectory, "appsettings.production.json");
        await RunStageAsync($"Copy configuration file '{TaxProviderConfig}'", () => CopyFileVerifiedAsync(TaxProviderConfig, taxTarget, token));
        created.Add(taxTarget);
        Report(2, "Copying web service configuration");
        bytes += new FileInfo(taxTarget).Length;

        string webTarget = Path.Combine(stagingDirectory, "web.config");
        await RunStageAsync($"Copy web.config '{WebServiceConfig}'", () => CopyFileVerifiedAsync(WebServiceConfig, webTarget, token));
        created.Add(webTarget);
        Report(3, "Exporting printer registry settings");
        bytes += new FileInfo(webTarget).Length;

        foreach (var (registryKey, fileName) in RegistryExports)
        {
            token.ThrowIfCancellationRequested();
            string target = Path.Combine(stagingDirectory, fileName);
            await RunStageAsync($"Export Registry key '{registryKey}'", () => ExportRegistryKeyAsync(registryKey, target, token));
            created.Add(target);
            Report(fileName == "printers.reg" ? 4 : 5, "Exporting Registry settings");
            bytes += new FileInfo(target).Length;
        }

        Report(5, "Compressing form_path");
        string formArchive = Path.Combine(stagingDirectory, "form_path.zip");
        await RunStageAsync($"Compress folder '{FormPath}'", () => Task.Run(() => ZipFile.CreateFromDirectory(FormPath, formArchive, CompressionLevel.Optimal, false), token));
        if (!File.Exists(formArchive) || new FileInfo(formArchive).Length == 0)
            throw new IOException("The form_path ZIP archive could not be created or is empty.");
        created.Add(formArchive);
        Report(6, "Creating compressed SQL database backup");
        bytes += new FileInfo(formArchive).Length;

        token.ThrowIfCancellationRequested();
        string sqlTarget = Path.Combine(stagingDirectory, "database.bak");
        string sqlServerBackup = await RunStageAsync($"Create SQL backup for database '{databaseName}'",
            () => SqlBackupService.CreateBackupAsync(sqlConnection, databaseName, stagingDirectory, token));
        if (!Path.GetFullPath(sqlServerBackup).Equals(Path.GetFullPath(sqlTarget), StringComparison.OrdinalIgnoreCase))
            await RunStageAsync($"Copy SQL backup from '{sqlServerBackup}'", () => SqlBackupService.CopyAndCleanupAsync(sqlServerBackup, sqlTarget, token));
        created.Add(sqlTarget);
        bytes += new FileInfo(sqlTarget).Length;

        Report(7, "Business backup components prepared");
        return new BusinessBackupResult(bytes, created);
    }

    private static async Task RunStageAsync(string stage, Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new IOException($"Backup step failed: {stage}. Details: {ex.Message}", ex);
        }
    }

    private static async Task<T> RunStageAsync<T>(string stage, Func<Task<T>> action)
    {
        try
        {
            return await action();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new IOException($"Backup step failed: {stage}. Details: {ex.Message}", ex);
        }
    }

    private static async Task ExportWindowsPrintersAsync(string targetPath, CancellationToken token)
    {
        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        string printBrmPath = Path.Combine(windows, "System32", "spool", "tools", "PrintBrm.exe");
        if (!File.Exists(printBrmPath) && Environment.Is64BitOperatingSystem && !Environment.Is64BitProcess)
            printBrmPath = Path.Combine(windows, "Sysnative", "spool", "tools", "PrintBrm.exe");

        if (!File.Exists(printBrmPath))
            throw new FileNotFoundException(
                "Windows PrintBRM was not found. Enable the Print and Document Services / Print Management feature, then try again.",
                printBrmPath);

        // PrintBRM is run as the signed-in user, exactly like the PowerShell command
        // verified on this PC. Export to the user's writable temp directory first:
        // the MEGA staging folder may be under Program Files\...\MSSQL\Backup,
        // where the interactive user may not have permission to create files.
        string tempExport = Path.Combine(Path.GetTempPath(), "printers_" + Guid.NewGuid().ToString("N") + ".printerExport");
        try
        {
            var start = new ProcessStartInfo(printBrmPath)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = Path.GetTempPath()
            };
            start.ArgumentList.Add("-b");
            start.ArgumentList.Add("-f");
            start.ArgumentList.Add(tempExport);

            using var process = new Process { StartInfo = start };
            if (!process.Start())
                throw new InvalidOperationException("Windows PrintBRM could not be started.");

            Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync(token);
            Task<string> stderrTask = process.StandardError.ReadToEndAsync(token);
            await process.WaitForExitAsync(token);
            string processOutput = (await stdoutTask) + Environment.NewLine + (await stderrTask);

            if (process.ExitCode != 0 || !File.Exists(tempExport) || new FileInfo(tempExport).Length == 0)
            {
                throw new InvalidOperationException(
                    $"Windows PrintBRM failed (exit code {process.ExitCode}) or did not create a valid export in the user's temp folder. " +
                    $"Computer: {Environment.MachineName}. Output: {processOutput.Trim()}");
            }

            token.ThrowIfCancellationRequested();
            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
            File.Copy(tempExport, targetPath, overwrite: true);
            if (!File.Exists(targetPath) || new FileInfo(targetPath).Length == 0)
                throw new IOException($"PrintBRM export could not be copied to the backup staging folder: {targetPath}");
        }
        finally
        {
            try { if (File.Exists(tempExport)) File.Delete(tempExport); } catch { }
        }

        token.ThrowIfCancellationRequested();
    }

    private static async Task ExportRegistryKeyAsync(string registryKey, string targetPath, CancellationToken token)
    {
        var start = new ProcessStartInfo("reg.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add("export");
        start.ArgumentList.Add(registryKey);
        start.ArgumentList.Add(targetPath);
        start.ArgumentList.Add("/y");

        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start Windows Registry export.");
        var stdout = process.StandardOutput.ReadToEndAsync(token);
        var stderr = process.StandardError.ReadToEndAsync(token);
        await process.WaitForExitAsync(token);
        string output = (await stdout) + Environment.NewLine + (await stderr);
        if (process.ExitCode != 0 || !File.Exists(targetPath) || new FileInfo(targetPath).Length == 0)
            throw new InvalidOperationException($"Could not export Registry key '{registryKey}'. {output.Trim()}");
    }

    private static async Task CopyFileVerifiedAsync(string source, string target, CancellationToken token)
    {
        await using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, true))
        await using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024, true))
        {
            await input.CopyToAsync(output, 1024 * 1024, token);
            await output.FlushAsync(token);
        }

        await using var sourceStream = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, true);
        await using var targetStream = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, true);
        byte[] sourceHash = await System.Security.Cryptography.SHA256.HashDataAsync(sourceStream, token);
        byte[] targetHash = await System.Security.Cryptography.SHA256.HashDataAsync(targetStream, token);
        if (!System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(sourceHash, targetHash))
        {
            File.Delete(target);
            throw new IOException($"File integrity check failed for '{Path.GetFileName(source)}'.");
        }
    }
}
