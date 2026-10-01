using System.ComponentModel;
using System.Security.Cryptography;
using System.Text;

namespace GoogleTasksDesktopWidget.Infrastructure;

public sealed class CredentialStore
{
    private readonly string _path;
    private bool _unreadable;

    public CredentialStore()
        : this(AppDataPaths.Current.TokenFilePath)
    {
    }

    public CredentialStore(string path) => _path = path;

    public bool HasRefreshToken => !_unreadable && File.Exists(_path);

    public string? ReadRefreshToken()
    {
        if (!File.Exists(_path)) return null;
        try
        {
            return Encoding.UTF8.GetString(DpapiProtector.Unprotect(File.ReadAllBytes(_path)));
        }
        catch (Exception exception) when (exception is Win32Exception or CryptographicException)
        {
            AppLogger.WriteError("OAuth.token_store_unprotect", exception);
            _unreadable = true;
            return null;
        }
    }

    public void SaveRefreshToken(string refreshToken)
    {
        var directory = Path.GetDirectoryName(_path)!;
        Directory.CreateDirectory(directory);
        var protectedBytes = DpapiProtector.Protect(Encoding.UTF8.GetBytes(refreshToken));
        var temporaryPath = _path + ".tmp";
        File.WriteAllBytes(temporaryPath, protectedBytes);
        File.Move(temporaryPath, _path, overwrite: true);
        _unreadable = false;
    }

    public void Delete()
    {
        if (File.Exists(_path)) File.Delete(_path);
        _unreadable = false;
    }
}
