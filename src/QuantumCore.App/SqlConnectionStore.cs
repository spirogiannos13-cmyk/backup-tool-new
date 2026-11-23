using Microsoft.Data.SqlClient;
using Microsoft.Win32;
using System.Security.Cryptography;
using System.Text.Json;

namespace QuantumCore.App;

public sealed class SavedSqlConnection
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string Server { get; set; } = "";
    // SQL Server-side directory. For remote servers this must be a UNC share accessible to both SQL Server and this PC.
    public string BackupDirectory { get; set; } = "";
    public bool UseWindowsAuthentication { get; set; } = true;
    public string Username { get; set; } = "";
    public string? ProtectedPassword { get; set; }

    public string GetConnectionString(string? database = null)
    {
        var builder = new SqlConnectionStringBuilder
        {
            DataSource = Server,
            InitialCatalog = database ?? "master",
            IntegratedSecurity = UseWindowsAuthentication,
            Encrypt = true,
            TrustServerCertificate = true,
            ConnectTimeout = 8,
            ApplicationName = "Quantum Core"
        };
        if (! UseWindowsAuthentication)
        {
            builder.UserID = Username;
            builder.Password = SqlConnectionStore.UnprotectPassword(ProtectedPassword);
        }
        return builder.ConnectionString;
    }
}

public static class SqlConnectionStore
{
    private static readonly byte[] Entropy = "QuantumCore.SqlConnections.v1"u8.ToArray();
    private static string StorePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "QuantumCore", "sql-connections.dat");

    public static List<SavedSqlConnection> Load()
    {
        try
        {
            if (!File.Exists(StorePath)) return [];
            var encrypted = File.ReadAllBytes(StorePath);
            var json = ProtectedData.Unprotect(encrypted, Entropy, DataProtectionScope.CurrentUser);
            return JsonSerializer.Deserialize<List<SavedSqlConnection>>(json) ?? [];
        }
        catch
        {
            return [];
        }
    }

    public static void Save(List<SavedSqlConnection> connections)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(connections, new JsonSerializerOptions { WriteIndented = true });
        var encrypted = ProtectedData.Protect(json, Entropy, DataProtectionScope.CurrentUser);
        Directory.CreateDirectory(Path.GetDirectoryName(StorePath)!);
        var temporaryPath = StorePath + ".tmp";
        File.WriteAllBytes(temporaryPath, encrypted);
        File.Move(temporaryPath, StorePath, overwrite: true);
    }

    public static string ProtectPassword(string password)
    {
        if (string.IsNullOrEmpty(password)) return "";
        return Convert.ToBase64String(ProtectedData.Protect(
            System.Text.Encoding.UTF8.GetBytes(password), Entropy, DataProtectionScope.CurrentUser));
    }

    public static string UnprotectPassword(string? protectedPassword)
    {
        if (string.IsNullOrEmpty(protectedPassword)) return "";
        var bytes = ProtectedData.Unprotect(Convert.FromBase64String(protectedPassword), Entropy, DataProtectionScope.CurrentUser);
        return System.Text.Encoding.UTF8.GetString(bytes);
    }
}

public static class SqlServerDiscovery
{
    public static IReadOnlyList<string> FindLocalInstances()
    {
        var servers = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        AddInstances(RegistryView.Registry64, servers);
        AddInstances(RegistryView.Registry32, servers);
        servers.Add(@"(localdb)\MSSQLLocalDB");
        return servers.ToList();
    }

    private static void AddInstances(RegistryView view, ISet<string> servers)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
            using var instances = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\Microsoft SQL Server\Instance Names\SQL");
            if (instances is null) return;
            foreach (var instanceName in instances.GetValueNames())
            {
                if (string.IsNullOrWhiteSpace(instanceName)) continue;
                servers.Add(instanceName.Equals("MSSQLSERVER", StringComparison.OrdinalIgnoreCase)
                    ? "."
                    : @".\" + instanceName);
            }
        }
        catch (UnauthorizedAccessException) { }
        catch (System.Security.SecurityException) { }
    }
}
