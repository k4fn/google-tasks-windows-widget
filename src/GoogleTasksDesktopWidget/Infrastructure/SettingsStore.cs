using System.Text.Json;
using System.Text.Json.Serialization;

namespace GoogleTasksDesktopWidget.Infrastructure;

public sealed class SettingsStore
{
    private readonly string _directory;
    private readonly string _path;
    private readonly JsonSerializerOptions _json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public string? LastRecoveryBackupPath { get; private set; }

    public SettingsStore()
        : this(AppDataPaths.Current.SettingsFilePath)
    {
    }

    public SettingsStore(string path)
    {
        _path = path;
        _directory = Path.GetDirectoryName(path)!;
    }

    public AppSettings Load()
    {
        LastRecoveryBackupPath = null;
        if (Directory.Exists(_path))
        {
            throw new IOException($"The settings path is a directory: '{_path}'.");
        }

        if (!File.Exists(_path))
        {
            return new AppSettings();
        }

        var contents = File.ReadAllText(_path);
        try
        {
            var settings = JsonSerializer.Deserialize<AppSettings>(contents, _json);
            using var document = JsonDocument.Parse(contents);
            var root = document.RootElement;
            if (settings is not null)
            {
                if (!root.TryGetProperty("Theme", out _) && !root.TryGetProperty("theme", out _) &&
                    (root.TryGetProperty("DarkTheme", out var legacyDarkTheme) || root.TryGetProperty("darkTheme", out legacyDarkTheme)) &&
                    legacyDarkTheme.ValueKind is JsonValueKind.True or JsonValueKind.False)
                {
                    settings.Theme = legacyDarkTheme.GetBoolean() ? WidgetTheme.Dark : WidgetTheme.Light;
                }

                if (!HasProperty(root, "StartWithWindows"))
                {
                    // Existing settings files predate portable defaults, where autostart was enabled by default.
                    settings.StartWithWindows = true;
                }
            }
            if (settings is null || settings.SchemaVersion is not (1 or 2 or 3 or 4) ||
                !double.IsFinite(settings.RightOffsetDip) || !double.IsFinite(settings.TopOffsetDip) ||
                !double.IsFinite(settings.WidthDip) || !double.IsFinite(settings.HeightDip) || !double.IsFinite(settings.MaxHeightDip) ||
                !Enum.IsDefined(settings.Theme) || !Enum.IsDefined(settings.SortOrder))
            {
                throw new InvalidDataException("Settings schema is not supported.");
            }

            settings.RightOffsetDip = Math.Clamp(settings.RightOffsetDip, 0, 10000);
            settings.TopOffsetDip = Math.Clamp(settings.TopOffsetDip, 0, 10000);
            settings.WidthDip = Math.Clamp(settings.WidthDip, 300, 700);
            settings.HeightDip = Math.Clamp(settings.HeightDip, 300, 850);
            settings.MaxHeightDip = Math.Clamp(settings.MaxHeightDip, 300, 520);
            if (settings.RefreshIntervalSeconds is < AppSettings.MinRefreshIntervalSeconds or > AppSettings.MaxRefreshIntervalSeconds)
                settings.RefreshIntervalSeconds = AppSettings.DefaultRefreshIntervalSeconds;
            if (settings.SchemaVersion == 1)
            {
                if (settings.Theme == WidgetTheme.System) settings.Theme = WidgetTheme.Dark;
                settings.SchemaVersion = 2;
            }
            if (settings.SchemaVersion == 2)
            {
                settings.Theme = WidgetTheme.Light;
                settings.IsCompletedSectionExpanded = false;
                settings.Filter = GoogleTasksDesktopWidget.Core.TaskFilter.All;
                settings.SchemaVersion = 3;
            }
            if (settings.SchemaVersion == 3)
            {
                settings.SchemaVersion = 4;
            }
            return settings;
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException or NotSupportedException)
        {
            LastRecoveryBackupPath = BackupCorruptFile();
            var defaults = new AppSettings();
            Save(defaults);
            AppLogger.WriteEvent("Settings.corrupt_backup_created");
            return defaults;
        }
    }

    public void Save(AppSettings settings)
    {
        settings.SchemaVersion = 4;
        var temporaryPath = _path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            Directory.CreateDirectory(_directory);
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(settings, _json));
            File.Move(temporaryPath, _path, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            throw new SettingsStorageException(_path, exception);
        }
        finally
        {
            try
            {
                if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                // Preserve the original write failure while leaving the settings file itself untouched.
            }
        }
    }

    private string BackupCorruptFile()
    {
        var backupPath = _path + ".corrupt-" + DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmssfff") + "-" + Guid.NewGuid().ToString("N");
        File.Copy(_path, backupPath, overwrite: false);
        return backupPath;
    }

    private static bool HasProperty(JsonElement root, string name) =>
        root.ValueKind == JsonValueKind.Object &&
        root.EnumerateObject().Any(property => string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase));
}

public sealed class SettingsStorageException(string path, Exception innerException)
    : IOException($"Settings could not be saved to '{path}'.", innerException);
