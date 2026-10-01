using System.ComponentModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace GoogleTasksDesktopWidget.Infrastructure;

/// <summary>Stores the OAuth client secret encrypted for the current Windows user.</summary>
public sealed class OAuthClientSecretStore
{
    private readonly string _path;

    public OAuthClientSecretStore()
        : this(AppDataPaths.Current.ClientSecretFilePath)
    {
    }

    public OAuthClientSecretStore(string path) => _path = path;

    public bool HasClientSecret => File.Exists(_path);

    public string? ReadClientSecret(string? clientId)
    {
        if (string.IsNullOrWhiteSpace(clientId) || !File.Exists(_path)) return null;
        try
        {
            var plaintext = DpapiProtector.Unprotect(File.ReadAllBytes(_path));
            try
            {
                var stored = JsonSerializer.Deserialize<StoredSecret>(plaintext);
                if (stored is null || string.IsNullOrWhiteSpace(stored.ClientId) || string.IsNullOrEmpty(stored.ClientSecret))
                {
                    Delete();
                    return null;
                }
                if (!string.Equals(clientId, stored.ClientId, StringComparison.Ordinal)) return null;
                return stored.ClientSecret;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plaintext);
            }
        }
        catch (Exception exception) when (exception is Win32Exception or CryptographicException or JsonException or InvalidDataException)
        {
            AppLogger.WriteError("OAuth.client_secret_store_unprotect", exception);
            return null;
        }
    }

    public void SaveClientSecret(string clientId, string clientSecret)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);
        ArgumentException.ThrowIfNullOrWhiteSpace(clientSecret);
        var directory = Path.GetDirectoryName(_path)!;
        Directory.CreateDirectory(directory);

        var plaintext = JsonSerializer.SerializeToUtf8Bytes(new StoredSecret { ClientId = clientId, ClientSecret = clientSecret });
        byte[] protectedBytes;
        try
        {
            protectedBytes = DpapiProtector.Protect(plaintext);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }

        var temporaryPath = _path + ".tmp";
        try
        {
            File.WriteAllBytes(temporaryPath, protectedBytes);
            File.Move(temporaryPath, _path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            CryptographicOperations.ZeroMemory(protectedBytes);
        }
    }

    public void Delete()
    {
        if (File.Exists(_path)) File.Delete(_path);
    }

    private sealed class StoredSecret
    {
        public StoredSecret() { }
        public string ClientId { get; set; } = string.Empty;
        public string ClientSecret { get; set; } = string.Empty;
    }
}
