using System.Buffers;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using GoogleTasksDesktopWidget.Core.Models;

namespace GoogleTasksDesktopWidget.Infrastructure;

public sealed record CachedSnapshot(
    DateTimeOffset SavedAt,
    List<TaskListRecord> Lists,
    Dictionary<string, List<TaskRecord>> TasksByList,
    string? OAuthClientId = null,
    Dictionary<string, List<TaskRecord>>? CompletedTasksByList = null,
    string? OAuthCredentialFingerprint = null,
    Dictionary<string, RecurrenceEntry>? Recurrences = null);

public sealed record RecurrenceEntry(GoogleTasksDesktopWidget.Core.RecurrenceFrequency Frequency, bool Pending = false);

public sealed class EncryptedCacheStore
{
    private readonly string _path;

    public EncryptedCacheStore()
        : this(AppDataPaths.Current.CacheFilePath)
    {
    }

    public EncryptedCacheStore(string path) => _path = path;

    public async Task SaveAsync(CachedSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var jsonBuffer = new ArrayBufferWriter<byte>();
        using (var jsonWriter = new Utf8JsonWriter(jsonBuffer))
        {
            JsonSerializer.Serialize(jsonWriter, snapshot);
        }

        if (!MemoryMarshal.TryGetArray(jsonBuffer.WrittenMemory, out var jsonSegment) || jsonSegment.Array is null)
        {
            throw new InvalidOperationException("Serialized cache JSON is not backed by an array.");
        }

        var encrypted = DpapiProtector.Protect(jsonSegment);
        var temporaryPath = _path + ".tmp";
        await File.WriteAllBytesAsync(temporaryPath, encrypted, cancellationToken).ConfigureAwait(false);
        File.Move(temporaryPath, _path, overwrite: true);
    }

    public async Task<CachedSnapshot?> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_path)) return null;
        try
        {
            var encrypted = await File.ReadAllBytesAsync(_path, cancellationToken).ConfigureAwait(false);
            var json = DpapiProtector.Unprotect(encrypted);
            return JsonSerializer.Deserialize<CachedSnapshot>(json);
        }
        catch (Exception exception) when (exception is Win32Exception or CryptographicException or JsonException or InvalidDataException)
        {
            AppLogger.WriteError("Cache.unprotect", exception);
            BackupCorruptFile();
            return null;
        }
    }

    public void Delete()
    {
        if (File.Exists(_path)) File.Delete(_path);
    }

    private void BackupCorruptFile()
    {
        try
        {
            if (File.Exists(_path))
            {
                File.Move(_path, _path + ".corrupt-" + DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmssfff"), overwrite: true);
            }
        }
        catch
        {
            // The cache is disposable; ignore file-system recovery errors.
        }
    }
}
