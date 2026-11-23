using Microsoft.Data.SqlClient;

namespace QuantumCore.App;

public static class SqlBackupService
{
    public static async Task<string> CreateBackupAsync(
        SavedSqlConnection connection, string databaseName, string localDestinationDirectory, CancellationToken cancellationToken)
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

        bool localServer = IsLocalServer(connection.Server);
        string backupDirectory;
        string fileName;

        if (localServer)
        {
            // For a local SQL Server, write the .bak directly into this backup run's
            // staging directory. This is the folder selected by the user (or its
            // temporary staging child), so there is no second copy from SQL's default
            // Program Files\...\Backup directory.
            backupDirectory = localDestinationDirectory;
            Directory.CreateDirectory(backupDirectory);
            fileName = "database.bak";
        }
        else
        {
            backupDirectory = connection.BackupDirectory.Trim();
            if (string.IsNullOrWhiteSpace(backupDirectory) || !backupDirectory.StartsWith(@"\\", StringComparison.Ordinal))
                throw new InvalidOperationException(
                    "For a remote SQL Server, set a UNC network share (\\\\server\\share\\folder) that the SQL Server service can write to and this PC can read from.");
            fileName = $"QuantumCore_{MakeSafeFileName(databaseName)}_{DateTime.Now:yyyyMMdd_HHmmss}_{Guid.NewGuid():N}.bak";
        }

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
        string destinationDirectory = Path.GetDirectoryName(destinationPath)!;
        Directory.CreateDirectory(destinationDirectory);
        string stagingPath = destinationPath + ".partial";

        try
        {
            // SQL Server or an antivirus/indexing process can briefly hold the completed .bak.
            // Retry sharing/lock violations rather than failing the entire backup immediately.
            await using (var input = await OpenReadWithRetryAsync(serverFilePath, token))
            await using (var output = new FileStream(stagingPath, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 1024, true))
            {
                await input.CopyToAsync(output, 1024 * 1024, token);
                await output.FlushAsync(token);
            }

            await using var sourceHashStream = await OpenReadWithRetryAsync(serverFilePath, token);
            await using var destinationHashStream = new FileStream(stagingPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, true);
            byte[] sourceHash = await System.Security.Cryptography.SHA256.HashDataAsync(sourceHashStream, token);
            byte[] destinationHash = await System.Security.Cryptography.SHA256.HashDataAsync(destinationHashStream, token);
            if (!System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(sourceHash, destinationHash))
                throw new IOException("The SQL .bak file failed the integrity check after copying.");

            File.Move(stagingPath, destinationPath, overwrite: false);
            try { File.Delete(serverFilePath); } catch { /* Keep the verified local copy; server-side cleanup can be done later. */ }
        }
        catch
        {
            try { if (File.Exists(stagingPath)) File.Delete(stagingPath); } catch { }
            try { if (File.Exists(destinationPath)) File.Delete(destinationPath); } catch { }
            throw;
        }
        // Keep the SQL Server .bak when copying or verification fails. It is the
        // evidence needed to diagnose a sharing violation and may be the only valid copy.
        // Delete it only after the verified copy has been promoted successfully.
    }

    private static async Task<FileStream> OpenReadWithRetryAsync(string path, CancellationToken token)
    {
        const int maxAttempts = 30;
        const int retryDelayMilliseconds = 1000;

        for (int attempt = 1; ; attempt++)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(path, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete, 1024 * 1024, FileOptions.Asynchronous);
            }
            catch (IOException ex) when (IsSharingViolation(ex) && attempt < maxAttempts)
            {
                await Task.Delay(retryDelayMilliseconds, token);
            }
            catch (IOException ex) when (IsSharingViolation(ex))
            {
                throw new IOException(
                    $"The SQL backup file is still locked after {maxAttempts} attempts (about {maxAttempts} seconds): {path}. Close any program using this .bak file and check antivirus/backup scanners.",
                    ex);
            }
        }
    }

    private static bool IsSharingViolation(IOException exception)
    {
        int win32Error = exception.HResult & 0xFFFF;
        return win32Error is 32 or 33; // ERROR_SHARING_VIOLATION / ERROR_LOCK_VIOLATION
    }

    private static bool IsLocalServer(string server)
    {
        string value = server.Trim();
        string host = value.Split('\\', ',')[0].Trim();
        return value.StartsWith(@".\", StringComparison.Ordinal)
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
