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
        CancellationToken token)
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

        token.ThrowIfCancellationRequested();
        string printerTarget = Path.Combine(stagingDirectory, PrinterExportFileName);
        await RunStageAsync("Windows printer export (printers.printerExport)", () => ExportWindowsPrintersAsync(printerTarget, token));
        created.Add(printerTarget);
        bytes += new FileInfo(printerTarget).Length;

        string taxTarget = Path.Combine(stagingDirectory, "appsettings.production.json");
        await RunStageAsync($"Copy configuration file '{TaxProviderConfig}'", () => CopyFileVerifiedAsync(TaxProviderConfig, taxTarget, token));
        created.Add(taxTarget);
        bytes += new FileInfo(taxTarget).Length;

        string webTarget = Path.Combine(stagingDirectory, "web.config");
        await RunStageAsync($"Copy web.config '{WebServiceConfig}'", () => CopyFileVerifiedAsync(WebServiceConfig, webTarget, token));
        created.Add(webTarget);
        bytes += new FileInfo(webTarget).Length;

        foreach (var (registryKey, fileName) in RegistryExports)
        {
            token.ThrowIfCancellationRequested();
            string target = Path.Combine(stagingDirectory, fileName);
            await RunStageAsync($"Export Registry key '{registryKey}'", () => ExportRegistryKeyAsync(registryKey, target, token));
            created.Add(target);
            bytes += new FileInfo(target).Length;
        }

        string formArchive = Path.Combine(stagingDirectory, "form_path.zip");
        await RunStageAsync($"Compress folder '{FormPath}'", () => Task.Run(() => ZipFile.CreateFromDirectory(FormPath, formArchive, CompressionLevel.Optimal, false), token));
        if (!File.Exists(formArchive) || new FileInfo(formArchive).Length == 0)
            throw new IOException("The form_path ZIP archive could not be created or is empty.");
        created.Add(formArchive);
        bytes += new FileInfo(formArchive).Length;

        token.ThrowIfCancellationRequested();
        string sqlTarget = Path.Combine(stagingDirectory, "database.bak");
        string sqlServerBackup = await RunStageAsync($"Create SQL backup for database '{databaseName}'",
            () => SqlBackupService.CreateBackupAsync(sqlConnection, databaseName, stagingDirectory, token));
        if (!Path.GetFullPath(sqlServerBackup).Equals(Path.GetFullPath(sqlTarget), StringComparison.OrdinalIgnoreCase))
            await RunStageAsync($"Copy SQL backup from '{sqlServerBackup}'", () => SqlBackupService.CopyAndCleanupAsync(sqlServerBackup, sqlTarget, token));
        created.Add(sqlTarget);
        bytes += new FileInfo(sqlTarget).Length;

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

        var start = new ProcessStartInfo(printBrmPath)
        {
            UseShellExecute = true,
            Verb = "runas",
            WorkingDirectory = Path.GetDirectoryName(targetPath)!
        };
        // Explicitly identify this machine as the source print server.
        // PrintBRM's documented backup syntax includes -s \\ServerName.
        start.ArgumentList.Add("-s");
        start.ArgumentList.Add(@"\\" + Environment.MachineName);
        start.ArgumentList.Add("-b");
        start.ArgumentList.Add("-f");
        start.ArgumentList.Add(targetPath);

        using var process = new Process { StartInfo = start };
        try
        {
            if (!process.Start())
                throw new InvalidOperationException("Windows PrintBRM could not be started.");
            await process.WaitForExitAsync(token);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            throw new OperationCanceledException(
                "Printer export requires Windows administrator approval. The UAC permission request was declined.", ex, token);
        }
        catch
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
            throw;
        }

        if (process.ExitCode != 0 || !File.Exists(targetPath) || new FileInfo(targetPath).Length == 0)
        {
            string spoolerState = "unknown";
            try
            {
                using var service = new Process
                {
                    StartInfo = new ProcessStartInfo("sc.exe", "query Spooler")
                    {
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true
                    }
                };
                service.Start();
                string serviceOutput = await service.StandardOutput.ReadToEndAsync(token);
                await service.WaitForExitAsync(token);
                spoolerState = serviceOutput.Contains("RUNNING", StringComparison.OrdinalIgnoreCase) ? "RUNNING" : serviceOutput.Trim();
            }
            catch (Exception ex) { spoolerState = ex.Message; }

            throw new InvalidOperationException(
                $"Windows PrintBRM did not create a valid printer export. Exit code: {process.ExitCode}. " +
                $"Computer: {Environment.MachineName}. Print Spooler: {spoolerState}. " +
                $"Expected file: {targetPath}. Verify the Print Spooler service and Windows Print Management, then check Event Viewer > Applications and Services Logs > Microsoft > Windows > PrintBRM > Admin.");
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
