using CG.Web.MegaApiClient;

namespace QuantumCore.App;

public sealed class MegaCloudItem
{
    public required INode Node { get; init; }
    public required string DisplayName { get; init; }
    public required bool IsFolder { get; init; }
    public string SizeDisplay { get; init; } = "—";
    public string ModifiedDisplay { get; init; } = "—";
    public override string ToString() => IsFolder
        ? $"📁  {DisplayName}"
        : $"📦  {DisplayName}     {SizeDisplay}     {ModifiedDisplay}";
}

public static class MegaCloudService
{
    public const string BackupFileName = "QuantumCore_Backup.zip";
    public const string UploadingFileName = "QuantumCore_Backup.uploading.zip";

    public static string GetPcFolderName(string? machineName = null)
    {
        string name = string.IsNullOrWhiteSpace(machineName) ? Environment.MachineName : machineName.Trim();
        var safe = new string(name.Select(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' ? ch : '-').ToArray()).Trim('-');
        if (string.IsNullOrWhiteSpace(safe)) safe = "UNKNOWN-PC";
        return "PC-" + safe.ToUpperInvariant();
    }

    public static async Task<INode> GetOrCreatePcFolderAsync(MegaApiClient client, CancellationToken token = default)
    {
        var root = client.GetNodes().Single(n => n.Type == NodeType.Root);
        string folderName = GetPcFolderName();
        var folder = client.GetNodes(root).FirstOrDefault(n => n.Type == NodeType.Directory &&
            n.Name.Equals(folderName, StringComparison.OrdinalIgnoreCase));
        return folder ?? await client.CreateFolderAsync(folderName, root);
    }

    public static async Task<List<MegaCloudItem>> ListRootFoldersAsync(MegaApiClient client)
    {
        var root = client.GetNodes().Single(n => n.Type == NodeType.Root);
        return client.GetNodes(root)
            .Where(n => n.Type == NodeType.Directory && n.Name.StartsWith("PC-", StringComparison.OrdinalIgnoreCase))
            .OrderBy(n => n.Name, StringComparer.OrdinalIgnoreCase)
            .Select(n => new MegaCloudItem { Node = n, DisplayName = n.Name, IsFolder = true })
            .ToList();
    }

    public static async Task<List<MegaCloudItem>> ListFolderContentsAsync(MegaApiClient client, INode folder)
    {
        return client.GetNodes(folder)
            .Where(n => n.Type == NodeType.File)
            .OrderByDescending(n => n.ModificationDate ?? DateTime.MinValue)
            .Select(n => new MegaCloudItem
            {
                Node = n,
                DisplayName = n.Name,
                IsFolder = false,
                SizeDisplay = FormatBytes(n.Size),
                ModifiedDisplay = n.ModificationDate?.ToLocalTime().ToString("dd/MM/yyyy HH:mm") ?? "—"
            })
            .ToList();
    }

    public static async Task ReplacePcBackupAsync(MegaApiClient client, string localZipPath,
        IProgress<double>? progress = null, CancellationToken token = default)
    {
        if (!File.Exists(localZipPath) || new FileInfo(localZipPath).Length == 0)
            throw new IOException("The local backup ZIP is missing or empty.");

        var folder = await GetOrCreatePcFolderAsync(client, token);
        var oldUploading = client.GetNodes(folder)
            .FirstOrDefault(n => n.Type == NodeType.File && n.Name == UploadingFileName);
        if (oldUploading is not null)
            await client.DeleteAsync(oldUploading, moveToTrash: false);

        var oldFinal = client.GetNodes(folder)
            .FirstOrDefault(n => n.Type == NodeType.File && n.Name == BackupFileName);

        INode uploaded = await client.UploadFileAsync(localZipPath, folder, progress, token);
        token.ThrowIfCancellationRequested();
        await client.RenameAsync(uploaded, BackupFileName);
        if (oldFinal is not null)
            await client.DeleteAsync(oldFinal, moveToTrash: false);
    }

    public static async Task DeleteFileAsync(MegaApiClient client, INode file)
    {
        if (file.Type != NodeType.File)
            throw new InvalidOperationException("Only backup files can be deleted from this view.");
        await client.DeleteAsync(file, moveToTrash: true);
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double size = Math.Max(0, bytes);
        int unit = 0;
        while (size >= 1024 && unit < units.Length - 1) { size /= 1024; unit++; }
        return $"{size:0.##} {units[unit]}";
    }
}
