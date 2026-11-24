using Microsoft.Data.SqlClient;

namespace QuantumCore.App;

public static class SqlBackupService
{
    public static async Task<string> GetBackupStagingParentAsync(
        SavedSqlConnection connection, CancellationToken cancellationToken)
    {
        await using var sql = new SqlConnection(connection.GetConnectionString("master"));
        await sql.OpenAsync(cancellationToken);

        await using var command = new SqlCommand(
            "SELECT CAST(SERVERPROPERTY('InstanceDefaultBackupPath') AS nvarchar(4000));", sql);
        command.CommandTimeout = 15;
        string? defaultDirectory = await command.ExecuteScalarAsync(cancellationToken) as string;

        if (IsLocalServer(connection.Server))
        {
            if (string.IsNullOrWhiteSpace(defaultDirectory))
                throw new DirectoryNotFoundException(
                    "SQL Server does not report a configured default backup directory.");
        }
        else
        {
            string remoteDirectory = connection.BackupDirectory.Trim();
            if (string.IsNullOrWhiteSpace(remoteDirectory) || !remoteDirectory.StartsWith(@"\\", StringComparison.Ordinal))
                throw new InvalidOperationException(
                    "For a remote SQL Server, configure a UNC backup share accessible to both SQL Server and this PC.");
            if (!Directory.Exists(remoteDirectory))
                throw new DirectoryNotFoundException($"The configured SQL backup share is not accessible: {remoteDirectory}");
        }

        // Keep application staging in the signed-in user's writable profile.
        // SQL Server itself writes database.bak to its configured server-side path.
        string stagingRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "QuantumCore", "Staging");
        Directory.CreateDirectory(stagingRoot);
        return stagingRoot;
    }

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
            // SQL Server's service account writes the .bak. Keep it in the
            // configured SQL backup directory, then copy and verify it into the
            // user's writable Quantum Core staging folder.
            if (string.IsNullOrWhiteSpace(serverDefaultDirectory))
                throw new DirectoryNotFoundException(
                    "SQL Server does not report a configured default backup directory.");
            backupDirectory = serverDefaultDirectory;
            fileName = $"QuantumCore_{MakeSafeFileName(databaseName)}_{DateTime.Now:yyyyMMdd_HHmmss}_{Guid.NewGuid():N}.bak";
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

        // Explicitly request native SQL Server backup compression to reduce the
        // amount of data written locally and uploaded to MEGA. Express Edition does
        // not support backup compression; only that specific unsupported-feature
        // error is allowed to fall back to a normal backup.
        string compressedBackupSql =
            $"BACKUP DATABASE [{escapedDatabase}] TO DISK = N'{escapedPath}' WITH COPY_ONLY, INIT, CHECKSUM, COMPRESSION, STATS = 10;";
        string uncompressedBackupSql =
            $"BACKUP DATABASE [{escapedDatabase}] TO DISK = N'{escapedPath}' WITH COPY_ONLY, INIT, CHECKSUM, STATS = 10;";

        try
        {
            await using var command = new SqlCommand(compressedBackupSql, sql)
            {
                CommandTimeout = 0
            };
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (SqlException ex) when (ex.Number == 1844)
        {
            // SQL Server Express: compression isn't supported by this edition.
            // Re-run without COMPRESSION rather than failing the whole backup.
            await using var fallbackCommand = new SqlCommand(uncompressedBackupSql, sql)
            {
                CommandTimeout = 0
            };
            await fallbackCommand.ExecuteNonQueryAsync(cancellationToken);
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
