using Microsoft.Data.SqlClient;

namespace QuantumCore.App;

public static class SqlBackupService
{
    public static async Task<string> CreateBackupAsync(
        SavedSqlConnection connection, string databaseName, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(databaseName))
            throw new InvalidOperationException("Select a SQL database before starting the backup.");

        await using var sql = new SqlConnection(connection.GetConnectionString("master"));
        await sql.OpenAsync(cancellationToken);

        string? serverDefaultDirectory = null;
        await using (var pathCommand = new SqlCommand(
            "SELECT CAST(SERVERPROPERTY('InstanceDefaultBackupPath') AS nvarchar(4000));", sql))
        {
            pathCommand.CommandTimeout = 15;
            serverDefaultDirectory = (await pathCommand.ExecuteScalarAsync(cancellationToken)) as string;
        }

        string backupDirectory = connection.BackupDirectory.Trim();
        bool localServer = IsLocalServer(connection.Server);
        if (string.IsNullOrWhiteSpace(backupDirectory))
        {
            if (!localServer || string.IsNullOrWhiteSpace(serverDefaultDirectory))
                throw new InvalidOperationException(
                    "Set a SQL backup folder for this connection. For a remote SQL Server, use a UNC network share (\\\\server\\share\\folder) that the SQL Server service can write to and this PC can read from.");
            backupDirectory = serverDefaultDirectory;
        }
        else if (!localServer && !backupDirectory.StartsWith(@"\\", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "For a remote SQL Server, the backup folder must be a UNC network path (\\\\server\\share\\folder), not a drive letter or local path.");
        }

        string safeDatabase = MakeSafeFileName(databaseName);
        string fileName = $"QuantumCore_{safeDatabase}_{DateTime.Now:yyyyMMdd_HHmmss}_{Guid.NewGuid():N}.bak";
        string serverFilePath = Path.Combine(backupDirectory, fileName);
        string escapedPath = serverFilePath.Replace("'", "''");
        string escapedDatabase = databaseName.Replace("]", "]]");

        await using (var command = new SqlCommand(
            $"BACKUP DATABASE [{escapedDatabase}] TO DISK = N'{escapedPath}' WITH COPY_ONLY, INIT, CHECKSUM, STATS = 10;", sql))
        {
            command.CommandTimeout = 0;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        // Verify that the SQL Server-created file is visible to this PC before declaring success.
        if (!File.Exists(serverFilePath))
            throw new IOException(
                $"SQL Server reported that the backup completed, but the file is not accessible from this PC: {serverFilePath}. For a remote server, configure a UNC share and grant the SQL Server service write access and this Windows user read access.");

        var fileInfo = new FileInfo(serverFilePath);
        if (fileInfo.Length == 0)
            throw new IOException("The SQL Server backup file is empty.");

        await using (var verify = new SqlCommand($"RESTORE VERIFYONLY FROM DISK = N'{escapedPath}' WITH CHECKSUM;", sql))
        {
            verify.CommandTimeout = 0;
            await verify.ExecuteNonQueryAsync(cancellationToken);
        }

        return serverFilePath;
    }

    public static async Task CopyAndCleanupAsync(string serverFilePath, string destinationPath, CancellationToken token)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        await using (var input = new FileStream(serverFilePath, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, true))
        await using (var output = new FileStream(destinationPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024, true))
        {
            await input.CopyToAsync(output, 1024 * 1024, token);
            await output.FlushAsync(token);
        }

        await using var sourceHashStream = new FileStream(serverFilePath, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, true);
        await using var destinationHashStream = new FileStream(destinationPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, true);
        byte[] sourceHash = await System.Security.Cryptography.SHA256.HashDataAsync(sourceHashStream, token);
        byte[] destinationHash = await System.Security.Cryptography.SHA256.HashDataAsync(destinationHashStream, token);
        if (!System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(sourceHash, destinationHash))
        {
            File.Delete(destinationPath);
            throw new IOException("The SQL .bak file failed the integrity check after copying. Backup is marked as failed.");
        }

        try { File.Delete(serverFilePath); } catch { /* The completed local copy is valid; leave server-side cleanup for an administrator. */ }
    }

    private static bool IsLocalServer(string server)
    {
        string value = server.Trim();
        string host = value.Split('\\', ',')[0].Trim();
        return value.StartsWith(@".\\", StringComparison.Ordinal)
            || value.StartsWith(@"(localdb)", StringComparison.OrdinalIgnoreCase)
            || host is "." or "(local)" or "localhost"
            || host.Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase);
    }

    private static string MakeSafeFileName(string value)
    {
        foreach (char invalid in Path.GetInvalidFileNameChars()) value = value.Replace(invalid, '_');
        return string.IsNullOrWhiteSpace(value) ? "Database" : value;
    }
}
