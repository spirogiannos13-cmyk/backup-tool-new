namespace QuantumCore.App;

public sealed class QuantumCoreBackupManifest
{
    public const string FileName = "quantumcore-backup-manifest.json";
    public int FormatVersion { get; set; } = 1;
    public string DatabaseName { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
}
