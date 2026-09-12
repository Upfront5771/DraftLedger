using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DraftLedger.Core;

namespace DraftLedger.App;

public sealed class AiSettingsFile(string path)
{
    public AiSettings Load()
    {
        if (!File.Exists(path)) return new();
        var settings = JsonSerializer.Deserialize<AiSettings>(File.ReadAllText(path), ProjectStore.Json) ?? throw new InvalidDataException("AI settings are empty.");
        if (settings.FormatVersion != 1) throw new InvalidDataException("Unsupported AI settings version.");
        if (settings.Presets.Count == 0) settings.Presets.Add(new());
        return settings;
    }
    public void Save(AiSettings settings) => AtomicFile.Write(path, JsonSerializer.Serialize(settings, ProjectStore.Json));
    private static byte[] Entropy(ApiConnection connection) => SHA256.HashData(Encoding.UTF8.GetBytes("DraftLedger|" + connection.Id + "|" + ApiEndpoint.Normalize(connection).AbsoluteUri));
    public static string Protect(ApiConnection connection, string key)
    {
        if (key.Length == 0) return "";
        byte[] bytes = Encoding.UTF8.GetBytes(key);
        try { return Convert.ToBase64String(ProtectedData.Protect(bytes, Entropy(connection), DataProtectionScope.CurrentUser)); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
    public static string Unprotect(ApiConnection connection)
    {
        if (connection.ProtectedKey.Length == 0) return "";
        try
        {
            byte[] bytes = ProtectedData.Unprotect(Convert.FromBase64String(connection.ProtectedKey), Entropy(connection), DataProtectionScope.CurrentUser);
            try { return Encoding.UTF8.GetString(bytes); } finally { CryptographicOperations.ZeroMemory(bytes); }
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException) { throw new InvalidOperationException("This key cannot be decrypted for the current Windows user and endpoint. Edit the connection and enter the key again."); }
    }
}
