using System.Threading;
using System.Windows;
using System.Net.NetworkInformation;
using Microsoft.Win32;
using GoogleTasksDesktopWidget.Infrastructure;
using GoogleTasksDesktopWidget.ViewModels;
using GoogleTasksDesktopWidget.Windows;

namespace GoogleTasksDesktopWidget;

public partial class App : Application
{
    private Mutex? _instanceMutex;
    private MainViewModel? _viewModel;
    private bool _ownsInstanceMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _instanceMutex = new Mutex(initiallyOwned: true, "Local\\GoogleTasksDesktopWidget", out var createdNew);
        _ownsInstanceMutex = createdNew;
        if (!createdNew)
        {
            MessageBox.Show(UiText.Get("AlreadyRunning"), UiText.Get("AppTitle"), MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        var paths = AppDataPaths.Current;
        SettingsStore settingsStore;
        AppSettings settings;
        AutoStartManager autoStart;
        try
        {
            var migration = new PortableDataMigrator(paths).Migrate();
            settingsStore = new SettingsStore(paths.SettingsFilePath);
            settings = settingsStore.Load();
            autoStart = new AutoStartManager();

            if (settingsStore.LastRecoveryBackupPath is { } backupPath)
            {
                settings.StartWithWindows = autoStart.IsEnabled;
                settingsStore.Save(settings);
                MessageBox.Show(
                    $"設定ファイルを読み込めなかったため、初期設定に戻しました。元のファイルは次の場所に保存しています。{Environment.NewLine}{backupPath}",
                    UiText.Get("AppTitle"), MessageBoxButton.OK, MessageBoxImage.Warning);
            }

            if (!File.Exists(paths.SettingsFilePath) && migration.HasLegacyData)
            {
                // Preserve a prior registry choice when an older install had no settings file yet.
                settings.StartWithWindows = autoStart.IsEnabled;
            }

            if (migration.ConflictingFiles.Count > 0)
            {
                var conflicts = string.Join(Environment.NewLine, migration.ConflictingFiles.Select(path => $"  {path}"));
                MessageBox.Show(
                    $"Data フォルダー内の既存ファイルは上書きせず、そのまま使用します。旧データは LocalAppData に残しています。{Environment.NewLine}{Environment.NewLine}{conflicts}",
                    UiText.Get("AppTitle"), MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                $"ポータブルデータを準備または読み込みできませんでした。Data フォルダーの書き込み権限を確認してください。{Environment.NewLine}{Environment.NewLine}{exception.Message}{Environment.NewLine}{Environment.NewLine}旧 LocalAppData データは削除していません。",
                UiText.Get("AppTitle"), MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown();
            return;
        }

        ThemeManager.Apply(settings.Theme);
        try { autoStart.SetEnabled(settings.StartWithWindows); }
        catch (Exception exception) { AppLogger.WriteError("AutoStart.initialize", exception); }

        var credentials = new CredentialStore(paths.TokenFilePath);
        var clientSecretStore = new OAuthClientSecretStore(paths.ClientSecretFilePath);
        var oauth = new OAuthService(credentials, settings, settingsStore, clientSecretStore);
        var cache = new EncryptedCacheStore(paths.CacheFilePath);
        var tasksClient = new GoogleTasksClient(oauth);
        _viewModel = new MainViewModel(settings, settingsStore, credentials, oauth, tasksClient, cache, autoStart, clientSecretStore);
        var window = new MainWindow(_viewModel, new DesktopHostService(settings, settingsStore));
        MainWindow = window;
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        NetworkChange.NetworkAddressChanged += OnNetworkAddressChanged;
        window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        NetworkChange.NetworkAddressChanged -= OnNetworkAddressChanged;
        if (_ownsInstanceMutex) _instanceMutex?.ReleaseMutex();
        _instanceMutex?.Dispose();
        base.OnExit(e);
    }

    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (_viewModel?.Theme != WidgetTheme.System) return;
        Dispatcher.BeginInvoke(() => ThemeManager.Apply(WidgetTheme.System));
    }

    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Resume) Dispatcher.BeginInvoke(RefreshTasks);
    }

    private void OnNetworkAddressChanged(object? sender, EventArgs e) => Dispatcher.BeginInvoke(RefreshTasks);

    private void RefreshTasks() => _viewModel?.RefreshCommand.Execute(null);
}
