namespace GoogleTasksDesktopWidget.Infrastructure;

/// <summary>Defines every portable and legacy location used by the application.</summary>
public sealed class AppDataPaths
{
    public const string ApplicationDataFolderName = "GoogleTasksDesktopWidget";
    public const string DataFolderName = "Data";

    public static AppDataPaths Current { get; } = new(
        AppContext.BaseDirectory,
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));

    public AppDataPaths(string executableDirectory, string localAppDataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executableDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(localAppDataDirectory);

        ExecutableDirectory = Path.GetFullPath(executableDirectory);
        LocalAppDataDirectory = Path.GetFullPath(localAppDataDirectory);
        DataDirectory = Path.Combine(ExecutableDirectory, DataFolderName);
        LegacyDataDirectory = Path.Combine(LocalAppDataDirectory, ApplicationDataFolderName);
        LogsDirectory = Path.Combine(DataDirectory, "logs");
    }

    public string ExecutableDirectory { get; }
    public string LocalAppDataDirectory { get; }
    public string DataDirectory { get; }
    public string LegacyDataDirectory { get; }
    public string LogsDirectory { get; }
    public string MigrationMarkerFilePath => Path.Combine(DataDirectory, ".legacy-migration-v1");
    public string SettingsFilePath => Path.Combine(DataDirectory, "settings.json");
    public string TokenFilePath => Path.Combine(DataDirectory, "token.dat");
    public string ClientSecretFilePath => Path.Combine(DataDirectory, "client-secret.dat");
    public string CacheFilePath => Path.Combine(DataDirectory, "cache.dat");
    public string LogFilePath => Path.Combine(LogsDirectory, "widget.log");
    public string PreviousLogFilePath => LogFilePath + ".1";

    public string GetLegacyFilePath(string fileName) =>
        Path.Combine(LegacyDataDirectory, fileName);

    public string GetLegacyLogsDirectory() =>
        Path.Combine(LegacyDataDirectory, "logs");
}
