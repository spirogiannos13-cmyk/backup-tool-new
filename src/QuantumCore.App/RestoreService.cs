using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;

namespace QuantumCore.App;

public static class RestoreService
{
    private const string TaxProviderConfig = @"C:\Program Files (x86)\Sunsoft Ltd\ExternalTaxProvider\External.Tax.Provider\appsettings.production.json";
    private const string WebServiceConfig = @"C:\Program Files (x86)\Sunsoft Ltd\AmvrosiaWebService\Amvrosia.Web.Service\web.config";
    private const string FormPath = @"C:\form_path";

    public static QuantumCoreBackupManifest ReadManifest(string archivePath)
    {
        using var archive = ZipFile.OpenRead(archivePath);
        var entry = archive.GetEntry(QuantumCoreBackupManifest.FileName)
            ?? throw new InvalidDataException("This backup does not contain recovery metadata. Create a new backup with the latest Quantum Core version first.");
        using var stream = entry.Open();
        return JsonSerializer.Deserialize<QuantumCoreBackupManifest>(stream)
            ?? throw new InvalidDataException("The backup recovery metadata is invalid.");
    }

    public static void RunElevatedRestore(string archivePath)
    {
        if (!File.Exists(archivePath)) throw new FileNotFoundException("Downloaded backup archive was not found.", archivePath);
        string work = Path.Combine(Path.GetTempPath(), "QuantumCore-Restore-" + Guid.NewGuid().ToString("N"));
        string extracted = Path.Combine(work, "package");
        string formExtract = Path.Combine(work, "form_path");
        string safety = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "QuantumCore", "PreRestore-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        Directory.CreateDirectory(extracted);
        Directory.CreateDirectory(formExtract);
        try
        {
            ZipFile.ExtractToDirectory(archivePath, extracted, overwriteFiles: false);
            var manifest = JsonSerializer.Deserialize<QuantumCoreBackupManifest>(
                File.ReadAllText(Path.Combine(extracted, QuantumCoreBackupManifest.FileName)))
                ?? throw new InvalidDataException("The backup recovery metadata is invalid.");
            if (manifest.FormatVersion != 1 || string.IsNullOrWhiteSpace(manifest.DatabaseName))
                throw new InvalidDataException("The backup format or database name is not supported.");

            string[] required = ["appsettings.production.json", "web.config", "printers.reg", "vb.reg",
                "printers.printerExport", "form_path.zip", "database.bak"];
            foreach (string file in required)
            {
                string path = Path.Combine(extracted, file);
                if (!File.Exists(path) || new FileInfo(path).Length == 0)
                    throw new InvalidDataException($"Required recovery file is missing or empty: {file}");
            }

            // Validate and stage the form_path archive before touching the live directory.
            ZipFile.ExtractToDirectory(Path.Combine(extracted, "form_path.zip"), formExtract, overwriteFiles: false);
            if (!Directory.EnumerateFileSystemEntries(formExtract).Any())
                throw new InvalidDataException("The form_path archive is empty.");

            Directory.CreateDirectory(safety);
            BackupExistingFile(TaxProviderConfig, Path.Combine(safety, "appsettings.production.json"));
            BackupExistingFile(WebServiceConfig, Path.Combine(safety, "web.config"));
            if (Directory.Exists(FormPath))
                Directory.Move(FormPath, Path.Combine(safety, "form_path"));
            ExportRegistryBeforeRestore(@"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Sunsoft\System\Printers",
                Path.Combine(safety, "printers.reg"));
            ExportRegistryBeforeRestore(@"HKEY_CURRENT_USER\Software\VB and VBA Program Settings\spim_POSSystem",
                Path.Combine(safety, "vb.reg"));

            Directory.CreateDirectory(Path.GetDirectoryName(TaxProviderConfig)!);
            Directory.CreateDirectory(Path.GetDirectoryName(WebServiceConfig)!);
            File.Copy(Path.Combine(extracted, "appsettings.production.json"), TaxProviderConfig, overwrite: true);
            File.Copy(Path.Combine(extracted, "web.config"), WebServiceConfig, overwrite: true);
            Directory.Move(formExtract, FormPath);

            RunProcess("reg.exe", ["import", Path.Combine(extracted, "printers.reg")]);
            RunProcess("reg.exe", ["import", Path.Combine(extracted, "vb.reg")]);

            string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            string printBrm = Path.Combine(windows, "System32", "spool", "tools", "PrintBrm.exe");
            if (!File.Exists(printBrm) && Environment.Is64BitOperatingSystem && !Environment.Is64BitProcess)
                printBrm = Path.Combine(windows, "Sysnative", "spool", "tools", "PrintBrm.exe");
            if (!File.Exists(printBrm)) throw new FileNotFoundException("Windows PrintBRM is required to restore printers.", printBrm);
            RunProcess(printBrm, ["-r", "-f", Path.Combine(extracted, "printers.printerExport")]);

            string sqlFolder = @"C:\sunsoftsetups";
            Directory.CreateDirectory(sqlFolder);
            string databaseFileName = MakeSafeFileName(manifest.DatabaseName) + ".bak";
            string sqlTarget = Path.Combine(sqlFolder, databaseFileName);
            string sqlSafety = Path.Combine(safety, databaseFileName);
            BackupExistingFile(sqlTarget, sqlSafety);
            File.Copy(Path.Combine(extracted, "database.bak"), sqlTarget, overwrite: true);

            File.WriteAllText(Path.Combine(safety, "RESTORE-INFO.txt"),
                $"Restore completed: {DateTime.Now:O}{Environment.NewLine}Database file copied to: {sqlTarget}{Environment.NewLine}The SQL database was NOT restored or attached.");
        }
        finally
        {
            try { if (Directory.Exists(work)) Directory.Delete(work, recursive: true); } catch { }
        }
    }

    private static void BackupExistingFile(string source, string destination)
    {
        if (!File.Exists(source)) return;
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.Copy(source, destination, overwrite: true);
    }

    private static void ExportRegistryBeforeRestore(string key, string target)
    {
        try { RunProcess("reg.exe", ["export", key, target, "/y"]); }
        catch { /* Some keys may not exist on a clean replacement disk. */ }
    }

    private static void RunProcess(string fileName, IEnumerable<string> arguments)
    {
        var start = new ProcessStartInfo(fileName) { UseShellExecute = false, CreateNoWindow = true };
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException($"Could not start {fileName}.");
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"{Path.GetFileName(fileName)} failed with exit code {process.ExitCode}.");
    }

    private static string MakeSafeFileName(string value)
    {
        foreach (char invalid in Path.GetInvalidFileNameChars()) value = value.Replace(invalid, '_');
        return string.IsNullOrWhiteSpace(value) ? "Database" : value;
    }
}
