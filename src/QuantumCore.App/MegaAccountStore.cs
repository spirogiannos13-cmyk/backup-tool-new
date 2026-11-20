using CG.Web.MegaApiClient;
using System.Security.Cryptography;
using System.Text.Json;

namespace QuantumCore.App;

public sealed class StoredMegaAccount
{
    public string Email { get; set; } = "";
    public string SessionId { get; set; } = "";
    public string MasterKey { get; set; } = "";
    public LogonSessionToken ToToken() => new(SessionId, Convert.FromBase64String(MasterKey));
}

public static class MegaAccountStore
{
    private static readonly byte[] Entropy = "QuantumCore.MegaAccount.v1"u8.ToArray();
    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "QuantumCore", "mega-account.dat");

    public static bool TryLoad(out StoredMegaAccount? account)
    {
        account = null;
        try
        {
            if (!File.Exists(FilePath)) return false;
            var encrypted = File.ReadAllBytes(FilePath);
            var json = ProtectedData.Unprotect(encrypted, Entropy, DataProtectionScope.CurrentUser);
            account = JsonSerializer.Deserialize<StoredMegaAccount>(json);
            return account is not null && !string.IsNullOrWhiteSpace(account.SessionId) && !string.IsNullOrWhiteSpace(account.MasterKey);
        }
        catch (CryptographicException) { return false; }
        catch (JsonException) { return false; }
    }

    public static void Save(string email, LogonSessionToken token)
    {
        var account = new StoredMegaAccount
        {
            Email = email,
            SessionId = token.SessionId,
            MasterKey = Convert.ToBase64String(token.MasterKey)
        };
        var json = JsonSerializer.SerializeToUtf8Bytes(account);
        var encrypted = ProtectedData.Protect(json, Entropy, DataProtectionScope.CurrentUser);
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllBytes(FilePath, encrypted);
    }

    public static void Remove()
    {
        if (File.Exists(FilePath)) File.Delete(FilePath);
    }
}
