using System.Diagnostics;
using System.IO.Compression;

namespace QuantumCore.App;

public sealed record BusinessBackupResult(long BytesWritten, IReadOnlyList<string> CreatedFiles);

public static class BusinessBackupService
{
    private const string TaxProviderConfig = @"C:\Program Files (x86)\Sunsoft Ltd\ExternalTaxProvider\External.Tax.Provider\appsettings.production.json";
    private const string WebServiceConfig = @"C:\Program Files (x86)\Sunsoft Ltd\AmvrosiaWebService\Amvrosia.Web.Service\web.config";
    private const string FormPath = @"C:\form_path";

    private static readonly (string RegistryKey, string FileName)[] RegistryExports =
    [
        (@"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Sunsoft\System\Printers", "printers.reg"),
        (@"HKEY_CURRENT_USER\Software\VB and VBA Program Settings\spim_POSSystem", "vb.reg")
    ];

    public static async Task<BusinessBackupResult> BuildAsync(
        string printerExportPath,
        SavedSqlConnection sqlConnection,
        string databaseName,
        string stagingDirectory,
        CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(printerExportPath) || !File.Exists(printerExportPath))
            throw new FileNotFoundException("The required printer export file was not found. Select the printers.printerExport file before starting.", printerExportPath);
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
        string printerTarget = Path.Combine(stagingDirectory, "printers.printerExport");
        await CopyFileVerifiedAsync(printerExportPath, printerTarget, token);
        created.Add(printerTarget);
        bytes += new FileInfo(printerTarget).Length;

        string taxTarget = Path.Combine(stagingDirectory, "appsettings.production.json");
        await CopyFileVerifiedAsync(TaxProviderConfig, taxTarget, token);
        created.Add(taxTarget);
        bytes += new FileInfo(taxTarget).Length;

        string webTarget = Path.Combine(stagingDirectory, "web.config");
        await CopyFileVerifiedAsync(WebServiceConfig, webTarget, token);
        created.Add(webTarget);
        bytes += new FileInfo(webTarget).Length;

        foreach (var (registryKey, fileName) in RegistryExports)
        {
            token.ThrowIfCancellationRequested();
            string target = Path.Combine(stagingDirectory, fileName);
            await ExportRegistryKeyAsync(registryKey, target, token);
            created.Add(target);
            bytes += new FileInfo(target).Length;
        }

        string formArchive = Path.Combine(stagingDirectory, "form_path.zip");
        await Task.Run(() => ZipFile.CreateFromDirectory(FormPath, formArchive, CompressionLevel.Optimal, false), token);
        if (!File.Exists(formArchive) || new FileInfo(formArchive).Length == 0)
            throw new IOException("The form_path ZIP archive could not be created or is empty.");
        created.Add(formArchive);
        bytes += new FileInfo(formArchive).Length;

        token.ThrowIfCancellationRequested();
        string sqlServerBackup = await SqlBackupService.CreateBackupAsync(sqlConnection, databaseName, token);
        string sqlTarget = Path.Combine(stagingDirectory, "database.bak");
        await SqlBackupService.CopyAndCleanupAsync(sqlServerBackup, sqlTarget, token);
        created.Add(sqlTarget);
        bytes += new FileInfo(sqlTarget).Length;

        return new BusinessBackupResult(bytes, created);
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
