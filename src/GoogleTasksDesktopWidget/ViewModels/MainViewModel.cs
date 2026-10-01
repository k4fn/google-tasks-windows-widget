using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;
using GoogleTasksDesktopWidget.Core;
using GoogleTasksDesktopWidget.Core.Models;
using GoogleTasksDesktopWidget.Infrastructure;
using GoogleTasksDesktopWidget.Windows;

namespace GoogleTasksDesktopWidget.ViewModels;

public sealed class MainViewModel : ObservableObject
{
    private readonly AppSettings _settings;
    private readonly SettingsStore _settingsStore;
    private readonly CredentialStore _credentials;
    private readonly OAuthClientSecretStore _clientSecretStore;
    private readonly OAuthService _oauth;
    private readonly GoogleTasksClient _tasksClient;
    private readonly EncryptedCacheStore _cache;
    private readonly AutoStartManager _autoStart;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly SemaphoreSlim _mutationGate = new(1, 1);
    private readonly DispatcherTimer _pollTimer;
    private Dictionary<string, List<TaskRecord>> _tasksByList = new(StringComparer.Ordinal);
    private Dictionary<string, List<TaskRecord>> _completedTasksByList = new(StringComparer.Ordinal);
    // Legacy cache data is retained for migration compatibility, but never creates local recurring tasks.
    private Dictionary<string, RecurrenceEntry> _recurrences = new(StringComparer.Ordinal);
    private IReadOnlyList<TaskRecord> _currentTasks = Array.Empty<TaskRecord>();
    private IReadOnlyList<TaskRecord> _currentCompletedTasks = Array.Empty<TaskRecord>();
    private TaskListRecord? _selectedList;
    private string _statusText = UiText.Get("ConnectHint");
    private string _snackbarMessage = string.Empty;
    private string _clientIdInput = string.Empty;
    private string _clientSecretInput = string.Empty;
    private bool _clearExistingClientSecret;
    private string _clientIdSetupMessage = string.Empty;
    private bool _showClientIdSetupPanel;
    private bool _isBusy;
    private bool _isDraftVisible;
    private bool _hasCache;
    private bool _hasError;

    public MainViewModel(
        AppSettings settings,
        SettingsStore settingsStore,
        CredentialStore credentials,
        OAuthService oauth,
        GoogleTasksClient tasksClient,
        EncryptedCacheStore cache,
        AutoStartManager autoStart,
        OAuthClientSecretStore? clientSecretStore = null)
    {
        _settings = settings;
        _settingsStore = settingsStore;
        _credentials = credentials;
        _clientSecretStore = clientSecretStore ?? new OAuthClientSecretStore();
        _oauth = oauth;
        _tasksClient = tasksClient;
        _cache = cache;
        _autoStart = autoStart;
        _clientIdInput = settings.OAuthClientId ?? string.Empty;
        _showClientIdSetupPanel = !_oauth.IsConfigured;

        ConnectCommand = new AsyncRelayCommand(_ => ConnectAsync());
        RefreshCommand = new AsyncRelayCommand(_ => RefreshAsync());
        SelectListCommand = new AsyncRelayCommand(SelectListAsync);
        AllFilterCommand = new AsyncRelayCommand(_ => SetFilterAsync(TaskFilter.All));
        TodayFilterCommand = new AsyncRelayCommand(_ => SetFilterAsync(TaskFilter.Today));
        ToggleCompletedCommand = new AsyncRelayCommand(_ => ToggleCompletedSectionAsync());
        ToggleLockCommand = new RelayCommand(_ => ToggleLock());
        ToggleAutoStartCommand = new RelayCommand(_ => ToggleAutoStart());
        SetThemeCommand = new RelayCommand(parameter => SetTheme(parameter is WidgetTheme theme ? theme : WidgetTheme.System));
        ReconnectCommand = new AsyncRelayCommand(_ => ConnectAsync());
        LogoutCommand = new AsyncRelayCommand(_ => LogoutAsync());
        ExitCommand = new RelayCommand(_ => Application.Current.Shutdown());
        ConfigureClientIdCommand = new RelayCommand(_ => BeginClientIdSetup());
        SaveClientIdCommand = new AsyncRelayCommand(_ => SaveClientIdAsync(), _ => !IsBusy && CanSaveClientId);
        CancelClientIdSetupCommand = new RelayCommand(_ => CancelClientIdSetup());

        _pollTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(60) };
        _pollTimer.Tick += async (_, _) => await PollAsync();
    }

    public ObservableCollection<TaskListRecord> TaskLists { get; } = [];
    public ObservableCollection<TaskItemViewModel> Tasks { get; } = [];
    public ObservableCollection<TaskItemViewModel> DatedTasks { get; } = [];
    public ObservableCollection<TaskItemViewModel> UndatedTasks { get; } = [];
    public ObservableCollection<TaskItemViewModel> CompletedTasks { get; } = [];

    public AsyncRelayCommand ConnectCommand { get; }
    public AsyncRelayCommand RefreshCommand { get; }
    public AsyncRelayCommand SelectListCommand { get; }
    public AsyncRelayCommand AllFilterCommand { get; }
    public AsyncRelayCommand TodayFilterCommand { get; }
    public AsyncRelayCommand ToggleCompletedCommand { get; }
    public RelayCommand ToggleLockCommand { get; }
    public RelayCommand ToggleAutoStartCommand { get; }
    public RelayCommand SetThemeCommand { get; }
    public AsyncRelayCommand ReconnectCommand { get; }
    public AsyncRelayCommand LogoutCommand { get; }
    public RelayCommand ExitCommand { get; }
    public RelayCommand ConfigureClientIdCommand { get; }
    public AsyncRelayCommand SaveClientIdCommand { get; }
    public RelayCommand CancelClientIdSetupCommand { get; }

    public TaskListRecord? SelectedList
    {
        get => _selectedList;
        private set
        {
            if (!SetProperty(ref _selectedList, value)) return;
            OnPropertyChanged(nameof(SelectedListTitle));
        }
    }

    public string SelectedListTitle => SelectedList?.Title ?? "マイタスク";

    public string StatusText { get => _statusText; private set => SetProperty(ref _statusText, value); }
    public string SnackbarMessage { get => _snackbarMessage; private set => SetProperty(ref _snackbarMessage, value); }
    public string ClientIdInput
    {
        get => _clientIdInput;
        set
        {
            if (!SetProperty(ref _clientIdInput, value)) return;
            if (!string.Equals(value.Trim(), _settings.OAuthClientId, StringComparison.Ordinal))
            {
                ClientSecretInput = string.Empty;
                ClearExistingClientSecret = false;
            }
            OnPropertyChanged(nameof(CanSaveClientId));
            SaveClientIdCommand.NotifyCanExecuteChanged();
        }
    }
    public string ClientSecretInput
    {
        get => _clientSecretInput;
        set
        {
            if (!SetProperty(ref _clientSecretInput, value)) return;
            if (!string.IsNullOrEmpty(value)) ClearExistingClientSecret = false;
            OnPropertyChanged(nameof(CanSaveClientId));
            SaveClientIdCommand.NotifyCanExecuteChanged();
        }
    }
    public bool ClearExistingClientSecret
    {
        get => _clearExistingClientSecret;
        set => SetProperty(ref _clearExistingClientSecret, value);
    }
    public string ClientIdSetupMessage { get => _clientIdSetupMessage; private set => SetProperty(ref _clientIdSetupMessage, value); }
    public bool ShowClientIdSetupPanel => _showClientIdSetupPanel;
    public bool CanSaveClientId => OAuthClientIdConfiguration.IsValid(ClientIdInput) &&
                                   OAuthClientIdConfiguration.IsValidClientSecret(ClientSecretInput);
    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (!SetProperty(ref _isBusy, value)) return;
            OnPropertyChanged(nameof(IsNotBusy));
            SaveClientIdCommand.NotifyCanExecuteChanged();
        }
    }
    public bool IsNotBusy => !IsBusy;
    public bool IsLocked => _settings.IsLocked;
    public WidgetTheme Theme => _settings.Theme;
    public double WidgetWidth => Math.Clamp(_settings.WidthDip, 300, 460);
    public double WidgetMaxHeight => Math.Clamp(_settings.MaxHeightDip, 300, 520);
    public bool IsAutoStartEnabled => _autoStart.IsEnabled;
    public bool HasError => _hasError;
    public bool IsAllFilter => _settings.Filter == TaskFilter.All;
    public bool IsTodayFilter => _settings.Filter == TaskFilter.Today;
    public TaskSortOrder SortOrder => _settings.SortOrder;
    public bool HasTasks => Tasks.Count > 0;
    public int CompletedTaskCount => _currentCompletedTasks.Count;
    public bool IsCompletedSectionExpanded => _settings.IsCompletedSectionExpanded;
    public bool IsConnected => _oauth.HasCredentials;
    public bool ShowConnectPanel => _oauth.IsConfigured && !IsConnected && !_showClientIdSetupPanel;
    public bool HasTaskList => SelectedList is not null;
    public bool IsDraftVisible
    {
        get => _isDraftVisible;
        set
        {
            if (!SetProperty(ref _isDraftVisible, value)) return;
            OnPropertyChanged(nameof(HasNoTasks));
        }
    }
    public bool HasNoTasks => IsConnected && !IsBusy && HasTaskList && Tasks.Count == 0 && !IsDraftVisible;
    public bool HasNoLists => IsConnected && !IsBusy && TaskLists.Count == 0;

    public async Task InitializeAsync()
    {
        var credentialFingerprint = GetCurrentCredentialFingerprint();
        var cached = await _cache.LoadAsync();
        if (cached is not null && !CacheIdentityPolicy.MatchesOwner(
                cached.OAuthClientId,
                _settings.OAuthClientId,
                cached.OAuthCredentialFingerprint,
                credentialFingerprint))
        {
            DeleteCacheBestEffort("Cache.owner_mismatch_cleanup");
            cached = null;
        }

        if (cached is not null)
        {
            ApplyLists(cached.Lists);
            _tasksByList = cached.TasksByList ?? new Dictionary<string, List<TaskRecord>>(StringComparer.Ordinal);
            _completedTasksByList = cached.CompletedTasksByList ?? new Dictionary<string, List<TaskRecord>>(StringComparer.Ordinal);
            _recurrences = cached.Recurrences ?? new Dictionary<string, RecurrenceEntry>(StringComparer.Ordinal);
            if (SelectConfiguredList()) LoadVisibleTasks();
            _hasCache = true;
            StatusText = UiText.Get("OfflineCached");
        }

        if (credentialFingerprint is null)
        {
            ClearTaskData();
            StatusText = !_oauth.IsConfigured ? UiText.Get("ConfigureClientId") : UiText.Get("ConnectHint");
            RaiseConnectionProperties();
            OnPropertyChanged(nameof(ShowClientIdSetupPanel));
            return;
        }

        _pollTimer.Start();
        await RefreshAsync();
    }

    public async Task<bool> ToggleTaskCompletionAsync(TaskItemViewModel item, bool completed)
    {
        if (!IsConnected || SelectedList is null) return false;
        if (!await SaveOpenTaskEditsAsync()) return false;
        await _mutationGate.WaitAsync();
        var selectedList = SelectedList;
        if (selectedList is null)
        {
            _mutationGate.Release();
            return false;
        }
        var listId = string.IsNullOrWhiteSpace(item.Task.TaskListId) ? selectedList.Id : item.Task.TaskListId;
        if (selectedList.Id != listId)
        {
            _mutationGate.Release();
            return false;
        }
        var previous = item.IsCompleted;
        if (previous == completed)
        {
            _mutationGate.Release();
            return true;
        }
        item.IsCompleted = completed;
        try
        {
            await _tasksClient.SetTaskCompletedAsync(listId, item.Id, completed);
            var updated = TaskMutationPolicy.SetCompleted(item.Task, completed, item.Title, DateTimeOffset.UtcNow);
            _currentTasks = _currentTasks.Where(task => task.Id != item.Id).ToArray();
            _currentCompletedTasks = _currentCompletedTasks.Where(task => task.Id != item.Id).ToArray();
            if (completed) _currentCompletedTasks = _currentCompletedTasks.Append(updated).ToArray();
            else _currentTasks = _currentTasks.Append(updated).ToArray();
            _tasksByList[listId] = _currentTasks.ToList();
            _completedTasksByList[listId] = _currentCompletedTasks.ToList();
            LoadVisibleTasks();
            await SaveCacheBestEffortAsync();
            SetOnlineOrPendingStatus();
            return true;
        }
        catch (Exception exception)
        {
            item.IsCompleted = previous;
            PresentError(exception);
            return false;
        }
        finally
        {
            _mutationGate.Release();
        }
    }

    public async Task<bool> SaveTaskTitleAsync(TaskItemViewModel item)
    {
        if (!item.IsEditing) return true;
        var title = item.EditDraft.Trim();
        item.IsEditing = false;
        if (string.IsNullOrWhiteSpace(title) || title == item.Title) return true;
        var selectedListId = SelectedList?.Id;
        var itemListId = string.IsNullOrWhiteSpace(item.Task.TaskListId) ? selectedListId : item.Task.TaskListId;
        if (string.IsNullOrWhiteSpace(itemListId)) return true;

        await _mutationGate.WaitAsync();
        var listId = itemListId;
        var previous = item.Title;
        item.Title = title;
        try
        {
            await _tasksClient.SetTaskTitleAsync(listId, item.Id, title);
            var taskMap = item.IsCompleted ? _completedTasksByList : _tasksByList;
            var currentTasks = item.IsCompleted ? _currentCompletedTasks : _currentTasks;
            var listTasks = taskMap.TryGetValue(listId, out var cachedTasks) ? cachedTasks :
                SelectedList?.Id == listId ? currentTasks.ToList() : new List<TaskRecord>();
            listTasks = listTasks.Select(task => task.Id == item.Id ? task with { Title = title } : task).ToList();
            taskMap[listId] = listTasks;
            if (SelectedList?.Id == listId)
            {
                if (item.IsCompleted) _currentCompletedTasks = listTasks;
                else _currentTasks = listTasks;
            }
            var visibleItem = Tasks.FirstOrDefault(task => task.Id == item.Id && task.Task.TaskListId == listId);
            if (visibleItem is not null) visibleItem.Title = title;
            var completedItem = CompletedTasks.FirstOrDefault(task => task.Id == item.Id && task.Task.TaskListId == listId);
            if (completedItem is not null) completedItem.Title = title;
            await SaveCacheBestEffortAsync();
            SetOnlineOrPendingStatus();
            ShowSnackbar(UiText.Get("TaskSaved"));
            return true;
        }
        catch (Exception exception)
        {
            item.Title = previous;
            item.EditDraft = title;
            item.IsEditing = true;
            PresentError(exception);
            return false;
        }
        finally
        {
            _mutationGate.Release();
        }
    }

    public async Task<bool> SaveTaskDetailsAsync(TaskItemViewModel item, string title, string? notes, DateOnly? dueDate)
    {
        title = title.Trim();
        if (title.Length == 0 || title.Length > 1024 || SelectedList is null) return false;
        var listId = item.Task.TaskListId;
        if (string.IsNullOrEmpty(listId)) listId = SelectedList.Id;
        if (listId != SelectedList.Id) return false;
        await _mutationGate.WaitAsync();
        try
        {
            await _tasksClient.UpdateTaskDetailsAsync(listId, item.Id, title, notes, dueDate);
            var updated = item.Task with { Title = title, Notes = notes, DueDate = dueDate };
            var map = item.IsCompleted ? _completedTasksByList : _tasksByList;
            var rows = map.TryGetValue(listId, out var cached) ? cached : [];
            map[listId] = rows.Select(task => task.Id == item.Id ? updated : task).ToList();
            if (item.IsCompleted) _currentCompletedTasks = map[listId];
            else _currentTasks = map[listId];
            LoadVisibleTasks();
            await SaveCacheBestEffortAsync();
            SetOnlineOrPendingStatus();
            ShowSnackbar(UiText.Get("TaskSaved"));
            return true;
        }
        catch (Exception exception)
        {
            PresentError(exception);
            return false;
        }
        finally { _mutationGate.Release(); }
    }

    public void CancelTaskEdit(TaskItemViewModel item)
    {
        item.EditDraft = item.Title;
        item.IsEditing = false;
    }

    public async Task DeleteTaskAsync(TaskItemViewModel item)
    {
        if (SelectedList is null) return;
        if (!await SaveOpenTaskEditsAsync()) return;
        await _mutationGate.WaitAsync();
        var selectedList = SelectedList;
        if (selectedList is null)
        {
            _mutationGate.Release();
            return;
        }
        var listId = string.IsNullOrWhiteSpace(item.Task.TaskListId) ? selectedList.Id : item.Task.TaskListId;
        if (selectedList.Id != listId)
        {
            _mutationGate.Release();
            return;
        }
        var wasCompleted = item.IsCompleted;
        var sourceTasks = wasCompleted ? _currentCompletedTasks : _currentTasks;
        var index = sourceTasks.ToList().FindIndex(task => task.Id == item.Id);
        var restoreTask = sourceTasks.FirstOrDefault(task => task.Id == item.Id) ?? item.Task;
        if (wasCompleted)
        {
            _currentCompletedTasks = sourceTasks.Where(task => task.Id != item.Id).ToArray();
            _completedTasksByList[listId] = _currentCompletedTasks.ToList();
        }
        else
        {
            _currentTasks = sourceTasks.Where(task => task.Id != item.Id).ToArray();
            _tasksByList[listId] = _currentTasks.ToList();
        }
        LoadVisibleTasks();
        try
        {
            await _tasksClient.DeleteTaskAsync(listId, item.Id);
            await SaveCacheBestEffortAsync();
            SetOnlineOrPendingStatus();
            ShowSnackbar(UiText.Get("TaskDeleted"));
        }
        catch (Exception exception)
        {
            var restored = (wasCompleted ? _currentCompletedTasks : _currentTasks).ToList();
            if (index >= 0) restored.Insert(Math.Min(index, restored.Count), restoreTask);
            if (wasCompleted)
            {
                _currentCompletedTasks = restored;
                _completedTasksByList[listId] = restored.ToList();
            }
            else
            {
                _currentTasks = restored;
                _tasksByList[listId] = restored.ToList();
            }
            LoadVisibleTasks();
            PresentError(exception);
        }
        finally
        {
            _mutationGate.Release();
        }
    }

    private async Task ConnectAsync()
    {
        if (IsBusy) return;
        if (!_oauth.IsConfigured)
        {
            StatusText = UiText.Get("ConfigureClientId");
            BeginClientIdSetup();
            return;
        }

        if (!await SaveOpenTaskEditsAsync()) return;

        IsBusy = true;
        StatusText = UiText.Get("Loading");
        await _mutationGate.WaitAsync();
        try
        {
            _oauth.RequireReauthorization();
            DeleteCacheBestEffort("Cache.interactive_connect_cleanup");
            ClearTaskData();
            RaiseConnectionProperties();
            RaiseContentProperties();
            await _oauth.ConnectAsync();
            RaiseConnectionProperties();
            await RefreshDataAsync();
            SetOnlineOrPendingStatus();
            _pollTimer.Start();
        }
        catch (OperationCanceledException)
        {
            StatusText = UiText.Get("OAuthError");
        }
        catch (Exception exception)
        {
            PresentError(exception);
        }
        finally
        {
            IsBusy = false;
            _mutationGate.Release();
            RaiseContentProperties();
        }
    }

    private async Task RefreshAsync()
    {
        if (GetCurrentCredentialFingerprint() is null)
        {
            ClearAccountTaskData();
            _pollTimer.Stop();
            StatusText = !_oauth.IsConfigured ? UiText.Get("ConfigureClientId") : UiText.Get("ConnectHint");
            RaiseConnectionProperties();
            RaiseContentProperties();
            return;
        }

        if (!await SaveOpenTaskEditsAsync()) return;

        if (!await _refreshGate.WaitAsync(0)) return;
        if (!await _mutationGate.WaitAsync(0))
        {
            _refreshGate.Release();
            return;
        }
        IsBusy = true;
        if (!_hasCache) StatusText = UiText.Get("Loading");
        try
        {
            await RefreshDataAsync();
            SetOnlineOrPendingStatus();
        }
        catch (Exception exception)
        {
            PresentError(exception);
            if (GetCurrentCredentialFingerprint() is null)
            {
                ClearAccountTaskData();
                _pollTimer.Stop();
                RaiseConnectionProperties();
                RaiseContentProperties();
            }
        }
        finally
        {
            IsBusy = false;
            _mutationGate.Release();
            _refreshGate.Release();
            RaiseContentProperties();
        }
    }

    private async Task RefreshDataAsync()
    {
        var lists = await _tasksClient.GetTaskListsAsync();
        ApplyLists(lists);
        if (lists.Count == 0)
        {
            SelectedList = null;
            Tasks.Clear();
            DatedTasks.Clear();
            UndatedTasks.Clear();
            CompletedTasks.Clear();
            _currentTasks = Array.Empty<TaskRecord>();
            _currentCompletedTasks = Array.Empty<TaskRecord>();
            SetOnlineOrPendingStatus();
            await SaveCacheBestEffortAsync();
            RaiseContentProperties();
            return;
        }

        if (SelectedList is null || lists.All(item => item.Id != SelectedList.Id))
        {
            SelectedList = lists.FirstOrDefault(item => item.Id == _settings.SelectedListId) ?? lists[0];
            _settings.SelectedListId = SelectedList.Id;
            try { _settingsStore.Save(_settings); }
            catch (Exception exception) { PresentError(exception); }
        }

        await LoadSelectedListAsync();
        SetOnlineOrPendingStatus();
    }

    private async Task LoadSelectedListAsync()
    {
        if (SelectedList is null) return;
        var listId = SelectedList.Id;
        var activeTasksTask = _tasksClient.GetTasksAsync(listId);
        var completedTasksTask = _tasksClient.GetCompletedTasksAsync(listId);
        await Task.WhenAll(activeTasksTask, completedTasksTask);
        var tasks = await activeTasksTask;
        var completedTasks = await completedTasksTask;
        _currentTasks = tasks;
        _currentCompletedTasks = completedTasks;
        _tasksByList[listId] = tasks.ToList();
        _completedTasksByList[listId] = completedTasks.ToList();
        LoadVisibleTasks();
        await SaveCacheBestEffortAsync();
        RaiseContentProperties();
    }

    private async Task SelectListAsync(object? parameter)
    {
        if (parameter is not TaskListRecord list || SelectedList?.Id == list.Id) return;
        if (!await SaveOpenTaskEditsAsync()) return;
        await _mutationGate.WaitAsync();
        try
        {
            if (SelectedList?.Id == list.Id) return;
            var previousList = SelectedList;
            var previousListId = _settings.SelectedListId;
            SelectedList = list;
            _settings.SelectedListId = list.Id;
            try { _settingsStore.Save(_settings); }
            catch (Exception exception)
            {
                SelectedList = previousList;
                _settings.SelectedListId = previousListId;
                PresentError(exception);
                return;
            }
            if (_tasksByList.TryGetValue(list.Id, out var cachedTasks))
            {
                _currentTasks = cachedTasks;
            }
            else
            {
                _currentTasks = Array.Empty<TaskRecord>();
            }
            _currentCompletedTasks = _completedTasksByList.TryGetValue(list.Id, out var cachedCompletedTasks)
                ? cachedCompletedTasks
                : Array.Empty<TaskRecord>();
            LoadVisibleTasks();
        }
        finally { _mutationGate.Release(); }

        if (_oauth.HasCredentials) await RefreshAsync();
    }

    public async Task<bool> CreateTaskFromDraftAsync(string title, string? notes, DateOnly? dueDate)
    {
        title = title.Trim();
        if (title.Length is 0 or > 1024 || SelectedList is null) return false;
        if (!await SaveOpenTaskEditsAsync()) return false;
        await _mutationGate.WaitAsync();
        var selectedList = SelectedList;
        if (selectedList is null)
        {
            _mutationGate.Release();
            return false;
        }
        var listId = selectedList.Id;
        IsBusy = true;
        try
        {
            var task = await _tasksClient.CreateTaskAsync(listId, title, notes, dueDate);
            _currentTasks = _currentTasks.Append(task).ToArray();
            _tasksByList[listId] = _currentTasks.ToList();
            LoadVisibleTasks();
            await SaveCacheBestEffortAsync();
            SetOnlineOrPendingStatus();
            return true;
        }
        catch (Exception exception)
        {
            PresentError(exception);
            return false;
        }
        finally
        {
            IsBusy = false;
            _mutationGate.Release();
            RaiseContentProperties();
        }
    }

    public async Task<TaskItemViewModel?> CreateSubtaskAsync(TaskItemViewModel parent)
    {
        if (!IsConnected || SelectedList is null || !await SaveOpenTaskEditsAsync()) return null;
        await _mutationGate.WaitAsync();
        try
        {
            var selectedList = SelectedList;
            var listId = string.IsNullOrWhiteSpace(parent.Task.TaskListId) ? selectedList?.Id : parent.Task.TaskListId;
            if (selectedList is null || string.IsNullOrWhiteSpace(listId) || listId != selectedList.Id) return null;

            var previousSibling = _currentTasks.Concat(_currentCompletedTasks)
                .Where(task => task.ParentId == parent.Id)
                .OrderBy(task => task.Position, StringComparer.Ordinal)
                .LastOrDefault();
            var created = await _tasksClient.CreateSubtaskAsync(listId, parent.Id, previousTaskId: previousSibling?.Id);
            _currentTasks = _currentTasks.Append(created).ToArray();
            _tasksByList[listId] = _currentTasks.ToList();
            LoadVisibleTasks();
            await SaveCacheBestEffortAsync();
            SetOnlineOrPendingStatus();
            return Tasks.FirstOrDefault(task => task.Id == created.Id);
        }
        catch (Exception exception)
        {
            PresentError(exception);
            return null;
        }
        finally { _mutationGate.Release(); }
    }

    public async Task<bool> MoveTaskToListAsync(TaskItemViewModel item, TaskListRecord destination)
    {
        if (!IsConnected || SelectedList is null || !await SaveOpenTaskEditsAsync()) return false;
        var sourceListId = string.IsNullOrWhiteSpace(item.Task.TaskListId) ? SelectedList.Id : item.Task.TaskListId;
        if (sourceListId != SelectedList.Id) return false;
        if (destination.Id == sourceListId) return true;

        await _mutationGate.WaitAsync();
        try
        {
            await _tasksClient.MoveTaskAsync(sourceListId, item.Id, destination.Id);
            await RefreshTaskListCacheAsync(sourceListId);
            await RefreshTaskListCacheAsync(destination.Id);
            await SaveCacheBestEffortAsync();
            SetOnlineOrPendingStatus();
            ShowSnackbar(UiText.Get("TaskSaved"));
            return true;
        }
        catch (Exception exception)
        {
            PresentError(exception);
            return false;
        }
        finally { _mutationGate.Release(); }
    }

    public async Task<bool> CreateTaskListAsync(string title)
    {
        title = title.Trim();
        if (title.Length == 0 || title.Length > 1024)
        {
            ShowSnackbar("リスト名は1〜1024文字で入力してください");
            return false;
        }
        if (!IsConnected || !await SaveOpenTaskEditsAsync()) return false;

        TaskListRecord created;
        await _mutationGate.WaitAsync();
        IsBusy = true;
        try
        {
            created = await _tasksClient.CreateTaskListAsync(title);
            ApplyLists(TaskLists.Where(list => list.Id != created.Id).Append(created));
            await SaveCacheBestEffortAsync();
            SetOnlineOrPendingStatus();
        }
        catch (Exception exception)
        {
            PresentError(exception);
            return false;
        }
        finally
        {
            IsBusy = false;
            _mutationGate.Release();
            RaiseContentProperties();
        }

        var createdList = TaskLists.FirstOrDefault(list => list.Id == created.Id);
        if (createdList is null) return false;
        await SelectListAsync(createdList);
        ShowSnackbar("リストを作成しました");
        return true;
    }

    private async Task RefreshTaskListCacheAsync(string listId)
    {
        var activeTasksTask = _tasksClient.GetTasksAsync(listId);
        var completedTasksTask = _tasksClient.GetCompletedTasksAsync(listId);
        await Task.WhenAll(activeTasksTask, completedTasksTask);
        var activeTasks = await activeTasksTask;
        var completedTasks = await completedTasksTask;
        _tasksByList[listId] = activeTasks.ToList();
        _completedTasksByList[listId] = completedTasks.ToList();
        if (SelectedList?.Id != listId) return;
        _currentTasks = activeTasks;
        _currentCompletedTasks = completedTasks;
        LoadVisibleTasks();
    }

    private async Task PollAsync()
    {
        foreach (var item in Tasks.Concat(CompletedTasks)) item.RefreshDateStatus();
        if (_mutationGate.CurrentCount == 0) return;
        if (GetCurrentCredentialFingerprint() is null)
        {
            await RefreshAsync();
            return;
        }
        if (HasOpenTaskEdits) return;
        await RefreshAsync();
    }

    private bool HasOpenTaskEdits =>
        Tasks.Any(task => task.IsEditing || task.IsDetailsExpanded) ||
        CompletedTasks.Any(task => task.IsEditing || task.IsDetailsExpanded);

    private async Task<bool> SaveOpenTaskEditsAsync()
    {
        while (true)
        {
            var editingTasks = Tasks.Concat(CompletedTasks)
                .Where(task => task.IsEditing || task.IsDetailsExpanded).ToArray();
            if (editingTasks.Length == 0) return true;
            foreach (var item in editingTasks)
            {
                if (item.IsDetailsExpanded)
                {
                    var draftDue = item.DraftDueDate is { } due
                        ? DateOnly.FromDateTime(due)
                        : (DateOnly?)null;
                    if (!await SaveTaskDetailsAsync(item, item.EditDraft, item.DraftNotes, draftDue)) return false;
                    item.IsDetailsExpanded = false;
                    continue;
                }
                if (!await SaveTaskTitleAsync(item)) return false;
            }
        }
    }

    private string? GetCurrentCredentialFingerprint()
    {
        if (!_oauth.HasCredentials) return null;
        return CacheIdentityPolicy.FingerprintRefreshToken(_credentials.ReadRefreshToken());
    }

    private void DeleteCacheBestEffort(string operation)
    {
        try { _cache.Delete(); }
        catch (Exception exception) { AppLogger.WriteError(operation, exception); }
        _hasCache = false;
    }

    private void ClearAccountTaskData()
    {
        DeleteCacheBestEffort("Cache.credential_cleanup");
        ClearTaskData();
    }

    private async Task SetFilterAsync(TaskFilter filter)
    {
        if (_settings.Filter == filter) return;
        if (!await SaveOpenTaskEditsAsync()) return;
        var previousFilter = _settings.Filter;
        _settings.Filter = filter;
        try { _settingsStore.Save(_settings); }
        catch (Exception exception)
        {
            _settings.Filter = previousFilter;
            PresentError(exception);
            return;
        }
        OnPropertyChanged(nameof(IsAllFilter));
        OnPropertyChanged(nameof(IsTodayFilter));
        LoadVisibleTasks();
    }

    public void SetSortOrder(TaskSortOrder sortOrder)
    {
        if (!Enum.IsDefined(sortOrder) || _settings.SortOrder == sortOrder) return;
        var previousSortOrder = _settings.SortOrder;
        _settings.SortOrder = sortOrder;
        try
        {
            _settingsStore.Save(_settings);
            OnPropertyChanged(nameof(SortOrder));
            LoadVisibleTasks();
        }
        catch (Exception exception)
        {
            _settings.SortOrder = previousSortOrder;
            PresentError(exception);
        }
    }

    private async Task ToggleCompletedSectionAsync()
    {
        if (IsCompletedSectionExpanded && !await SaveOpenTaskEditsAsync()) return;
        var previousExpanded = _settings.IsCompletedSectionExpanded;
        _settings.IsCompletedSectionExpanded = !_settings.IsCompletedSectionExpanded;
        try { _settingsStore.Save(_settings); }
        catch (Exception exception)
        {
            _settings.IsCompletedSectionExpanded = previousExpanded;
            PresentError(exception);
            return;
        }
        if (_settings.IsCompletedSectionExpanded) RefreshCompletedTaskItems();
        else CompletedTasks.Clear();
        OnPropertyChanged(nameof(IsCompletedSectionExpanded));
        OnPropertyChanged(nameof(CompletedTaskCount));
    }

    private void ToggleLock()
    {
        var previousLocked = _settings.IsLocked;
        _settings.IsLocked = !_settings.IsLocked;
        try { _settingsStore.Save(_settings); }
        catch (Exception exception)
        {
            _settings.IsLocked = previousLocked;
            PresentError(exception);
            return;
        }
        OnPropertyChanged(nameof(IsLocked));
    }

    private void SetTheme(WidgetTheme theme)
    {
        if (_settings.Theme == theme) return;
        var previousTheme = _settings.Theme;
        _settings.Theme = theme;
        try { _settingsStore.Save(_settings); }
        catch (Exception exception)
        {
            _settings.Theme = previousTheme;
            PresentError(exception);
            return;
        }
        _ = ThemeManager.Apply(_settings.Theme);
        OnPropertyChanged(nameof(Theme));
    }

    private void ToggleAutoStart()
    {
        var previousEnabled = _autoStart.IsEnabled;
        var previousSetting = _settings.StartWithWindows;
        try
        {
            _autoStart.SetEnabled(!previousEnabled);
            _settings.StartWithWindows = _autoStart.IsEnabled;
            _settingsStore.Save(_settings);
            OnPropertyChanged(nameof(IsAutoStartEnabled));
        }
        catch (Exception exception)
        {
            _settings.StartWithWindows = previousSetting;
            try { _autoStart.SetEnabled(previousEnabled); }
            catch (Exception rollbackException) { AppLogger.WriteError("AutoStart.rollback", rollbackException); }
            PresentError(exception);
        }
    }

    private async Task LogoutAsync()
    {
        await _mutationGate.WaitAsync();
        try
        {
            try { await _oauth.RevokeAsync(); }
            catch (Exception exception) { AppLogger.WriteError("OAuth token revoke", exception); }
            try { _oauth.SignOut(); }
            catch (Exception exception) { AppLogger.WriteError("OAuth.sign_out", exception); }
            DeleteCacheBestEffort("Cache.logout_cleanup");
            ClearTaskData();
            _pollTimer.Stop();
            StatusText = !_oauth.IsConfigured ? UiText.Get("ConfigureClientId") : UiText.Get("ConnectHint");
            _hasError = false;
            RaiseConnectionProperties();
            RaiseContentProperties();
        }
        finally
        {
            _mutationGate.Release();
        }
    }

    private void ApplyLists(IEnumerable<TaskListRecord> lists)
    {
        var selectedId = SelectedList?.Id ?? _settings.SelectedListId;
        var snapshot = lists.ToArray();
        if (!TaskLists.SequenceEqual(snapshot))
        {
            TaskLists.Clear();
            foreach (var list in snapshot) TaskLists.Add(list);
        }
        SelectedList = TaskLists.FirstOrDefault(item => item.Id == selectedId);
        RaiseContentProperties();
    }

    private bool SelectConfiguredList()
    {
        if (TaskLists.Count == 0)
        {
            SelectedList = null;
            return false;
        }

        SelectedList = TaskLists.FirstOrDefault(item => item.Id == _settings.SelectedListId) ?? TaskLists[0];
        _settings.SelectedListId = SelectedList.Id;
        _currentTasks = _tasksByList.TryGetValue(SelectedList.Id, out var cachedTasks)
            ? cachedTasks
            : Array.Empty<TaskRecord>();
        _currentCompletedTasks = _completedTasksByList.TryGetValue(SelectedList.Id, out var cachedCompletedTasks)
            ? cachedCompletedTasks
            : Array.Empty<TaskRecord>();
        try { _settingsStore.Save(_settings); }
        catch (Exception exception) { PresentError(exception); }
        return true;
    }

    private void LoadVisibleTasks()
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        var visibleTasks = TaskOrdering.Flatten(
            _currentTasks.Where(task => TaskDatePolicy.Includes(task, _settings.Filter, today)), _settings.SortOrder);
        ReconcileTaskItems(Tasks, visibleTasks);
        ReconcileTaskReferences(DatedTasks, Tasks.Where(item => item.HasDueDate));
        ReconcileTaskReferences(UndatedTasks, Tasks.Where(item => !item.HasDueDate));

        if (IsCompletedSectionExpanded) RefreshCompletedTaskItems();

        RaiseContentProperties();
    }

    private void RefreshCompletedTaskItems()
    {
        var orderedTasks = TaskDateGrouping.MostRecentlyCompleted(_currentCompletedTasks);
        var depths = TaskOrdering.Flatten(_currentCompletedTasks)
            .ToDictionary(item => item.Task.Id, item => item.Depth, StringComparer.Ordinal);
        ReconcileTaskItems(CompletedTasks, orderedTasks.Select(task =>
            new OrderedTask(task, depths.GetValueOrDefault(task.Id))));
    }

    // Polls normally return the same tasks. Keep those row objects and avoid a
    // Clear/Add cycle that empties the list, resets scroll, and closes row state.
    private static void ReconcileTaskItems(ObservableCollection<TaskItemViewModel> items, IEnumerable<OrderedTask> records)
    {
        var desired = records as IReadOnlyList<OrderedTask> ?? records.ToArray();
        if (items.Count == desired.Count && items.Select(item => item.Task).SequenceEqual(desired.Select(item => item.Task)) &&
            items.Select(item => item.Depth).SequenceEqual(desired.Select(item => item.Depth))) return;

        for (var index = 0; index < desired.Count; index++)
        {
            var orderedTask = desired[index];
            var record = orderedTask.Task;
            var existingIndex = -1;
            for (var candidate = index; candidate < items.Count; candidate++)
            {
                if (items[candidate].Id == record.Id &&
                    items[candidate].Task.TaskListId == record.TaskListId)
                {
                    existingIndex = candidate;
                    break;
                }
            }

            if (existingIndex < 0)
            {
                items.Insert(index, new TaskItemViewModel(orderedTask));
                continue;
            }

            items[existingIndex].UpdateTask(orderedTask);
            if (existingIndex != index) items.Move(existingIndex, index);
        }

        while (items.Count > desired.Count) items.RemoveAt(items.Count - 1);
    }

    private static void ReconcileTaskReferences(
        ObservableCollection<TaskItemViewModel> items, IEnumerable<TaskItemViewModel> desiredItems)
    {
        var desired = desiredItems.ToArray();
        for (var index = 0; index < desired.Length; index++)
        {
            if (index < items.Count && ReferenceEquals(items[index], desired[index])) continue;
            var existingIndex = items.IndexOf(desired[index]);
            if (existingIndex >= 0) items.Move(existingIndex, index);
            else items.Insert(index, desired[index]);
        }
        while (items.Count > desired.Length) items.RemoveAt(items.Count - 1);
    }

    private async Task SaveCacheAsync()
    {
        var credentialFingerprint = GetCurrentCredentialFingerprint();
        if (credentialFingerprint is null)
        {
            DeleteCacheBestEffort("Cache.missing_credential_cleanup");
            return;
        }

        await _cache.SaveAsync(new CachedSnapshot(DateTimeOffset.UtcNow, TaskLists.ToList(), _tasksByList,
            _settings.OAuthClientId, _completedTasksByList, credentialFingerprint, _recurrences));
        _hasCache = true;
    }

    public RecurrenceFrequency GetRecurrence(TaskItemViewModel item)
        => RecurrenceFrequency.None;

    public bool IsRecurrencePending(TaskItemViewModel item) => false;

    public Task<bool> RetryPendingRecurrenceAsync(TaskItemViewModel item) => Task.FromResult(false);

    private void SetOnlineOrPendingStatus()
    {
        StatusText = UiText.Get("Online");
        _hasError = false;
        OnPropertyChanged(nameof(HasError));
    }

    private async Task SaveCacheBestEffortAsync()
    {
        try { await SaveCacheAsync(); }
        catch (Exception exception) { AppLogger.WriteError("Cache.save", exception); }
    }

    private void PresentError(Exception exception)
    {
        AppLogger.WriteError("Widget operation", exception);
        if (exception is GoogleApiException { StatusCode: System.Net.HttpStatusCode.Unauthorized })
        {
            StatusText = UiText.Get("ReauthorizationRequired");
        }
        else if (exception is GoogleApiException apiFailure && apiFailure.StatusCode == System.Net.HttpStatusCode.Forbidden)
        {
            var resourceKey = GoogleApiErrorPolicy.ClassifyForbiddenReason(apiFailure.ReasonCode) switch
            {
                GoogleApiErrorKind.TasksApiNotEnabled => "GoogleTasksApiNotEnabled",
                GoogleApiErrorKind.InsufficientPermissions => "GoogleTasksInsufficientPermissions",
                GoogleApiErrorKind.DomainPolicy => "GoogleTasksDomainPolicy",
                _ => "PermissionError"
            };
            StatusText = UiText.Get(resourceKey);
        }
        else if (exception is OAuthConfigurationException)
        {
            StatusText = UiText.Get("ConfigureClientId");
        }
        else if (exception is OAuthFlowException { ErrorCode: "client_secret_required" })
        {
            StatusText = UiText.Get("OAuthClientSecretRequired");
            ClientIdSetupMessage = StatusText;
            _showClientIdSetupPanel = true;
        }
        else if (exception is OAuthFlowException)
        {
            StatusText = UiText.Get("OAuthError");
        }
        else if (exception is SettingsStorageException)
        {
            StatusText = UiText.Get("SettingsWriteFailed");
        }
        else if (exception is HttpRequestException or TaskCanceledException)
        {
            StatusText = _hasCache ? UiText.Get("OfflineCached") : UiText.Get("NetworkError");
        }
        else
        {
            StatusText = UiText.Get("GeneralError");
        }

        ShowSnackbar(StatusText);
        _hasError = true;
        OnPropertyChanged(nameof(HasError));
        RaiseConnectionProperties();
    }

    private void ShowSnackbar(string message)
    {
        SnackbarMessage = message;
        var current = message;
        _ = Task.Delay(TimeSpan.FromSeconds(4)).ContinueWith(_ =>
        {
            Application.Current.Dispatcher.BeginInvoke(() =>
            {
                if (SnackbarMessage == current) SnackbarMessage = string.Empty;
            });
        });
    }

    private void RaiseConnectionProperties()
    {
        OnPropertyChanged(nameof(IsConnected));
        OnPropertyChanged(nameof(ShowConnectPanel));
        OnPropertyChanged(nameof(ShowClientIdSetupPanel));
        OnPropertyChanged(nameof(CanSaveClientId));
    }

    private void RaiseContentProperties()
    {
        OnPropertyChanged(nameof(HasTasks));
        OnPropertyChanged(nameof(CompletedTaskCount));
        OnPropertyChanged(nameof(HasTaskList));
        OnPropertyChanged(nameof(HasNoTasks));
        OnPropertyChanged(nameof(HasNoLists));
        OnPropertyChanged(nameof(ShowConnectPanel));
        OnPropertyChanged(nameof(ShowClientIdSetupPanel));
        OnPropertyChanged(nameof(IsConnected));
    }

    public void ImportClientIdFromJson(string path)
    {
        try
        {
            var configuration = OAuthClientIdConfiguration.ReadDesktopClientConfiguration(path);
            ClientIdInput = configuration.ClientId;
            ClientSecretInput = configuration.ClientSecret ?? string.Empty;
            ClearExistingClientSecret = false;
            ClientIdSetupMessage = UiText.Get("OAuthClientJsonLoaded");
        }
        catch (OAuthClientConfigurationException exception)
        {
            ClientIdSetupMessage = exception.Error switch
            {
                OAuthClientConfigurationError.NotDesktopClient => UiText.Get("OAuthClientNotDesktop"),
                OAuthClientConfigurationError.MissingClientId => UiText.Get("OAuthClientIdMissing"),
                OAuthClientConfigurationError.InvalidClientId => UiText.Get("OAuthClientIdInvalid"),
                OAuthClientConfigurationError.InvalidClientSecret => UiText.Get("OAuthClientSecretInvalid"),
                _ => UiText.Get("OAuthClientJsonInvalid")
            };
        }
    }

    private void BeginClientIdSetup()
    {
        ClientIdInput = _settings.OAuthClientId ?? string.Empty;
        ClientSecretInput = string.Empty;
        ClearExistingClientSecret = false;
        ClientIdSetupMessage = string.Empty;
        _showClientIdSetupPanel = true;
        OnPropertyChanged(nameof(ShowClientIdSetupPanel));
        OnPropertyChanged(nameof(ShowConnectPanel));
    }

    private void CancelClientIdSetup()
    {
        if (!_oauth.IsConfigured) return;
        _showClientIdSetupPanel = false;
        ClientIdSetupMessage = string.Empty;
        OnPropertyChanged(nameof(ShowClientIdSetupPanel));
        OnPropertyChanged(nameof(ShowConnectPanel));
    }

    private async Task SaveClientIdAsync()
    {
        var clientId = ClientIdInput.Trim();
        var clientSecret = ClientSecretInput.Trim();
        if (!OAuthClientIdConfiguration.IsValid(clientId))
        {
            ClientIdSetupMessage = UiText.Get("OAuthClientIdInvalid");
            return;
        }
        if (!OAuthClientIdConfiguration.IsValidClientSecret(clientSecret))
        {
            ClientIdSetupMessage = UiText.Get("OAuthClientSecretInvalid");
            return;
        }

        await _mutationGate.WaitAsync();
        IsBusy = true;
        try
        {
            var previousClientId = _settings.OAuthClientId;
            var previousClientSecret = _clientSecretStore.ReadClientSecret(previousClientId);
            var previousReauthorizationRequired = _settings.OAuthReauthorizationRequired;
            var changedClientId = !string.Equals(previousClientId, clientId, StringComparison.Ordinal);
            var configuredClientSecret = string.IsNullOrEmpty(clientSecret)
                ? (ClearExistingClientSecret || changedClientId ? null : previousClientSecret)
                : clientSecret;
            var changedClientSecret = !string.Equals(previousClientSecret, configuredClientSecret, StringComparison.Ordinal);
            var changed = changedClientId || changedClientSecret;
            _settings.OAuthClientId = clientId;
            if (changed) _settings.OAuthReauthorizationRequired = true;
            try
            {
                _settingsStore.Save(_settings);
            }
            catch (Exception exception)
            {
                _settings.OAuthClientId = previousClientId;
                _settings.OAuthReauthorizationRequired = previousReauthorizationRequired;
                AppLogger.WriteError("OAuth.client_id_save", exception);
                ClientIdSetupMessage = UiText.Get("OAuthClientIdSaveFailed");
                return;
            }

            if (changed)
            {
                try
                {
                    if (string.IsNullOrEmpty(configuredClientSecret)) _clientSecretStore.Delete();
                    else _clientSecretStore.SaveClientSecret(clientId, configuredClientSecret);
                }
                catch (Exception exception)
                {
                    AppLogger.WriteError("OAuth.client_secret_save", exception);
                    _showClientIdSetupPanel = true;
                    try
                    {
                        _settings.OAuthClientId = previousClientId;
                        _settings.OAuthReauthorizationRequired = previousReauthorizationRequired;
                        _settingsStore.Save(_settings);
                        ClientIdSetupMessage = UiText.Get("OAuthClientSecretSaveFailed");
                        StatusText = ClientIdSetupMessage;
                        RaiseConnectionProperties();
                        RaiseContentProperties();
                        return;
                    }
                    catch (Exception rollbackException)
                    {
                        AppLogger.WriteError("OAuth.client_settings_rollback", rollbackException);
                        // Keep the already persisted new ID and require reconnection if rollback is impossible.
                        _settings.OAuthClientId = clientId;
                        _settings.OAuthReauthorizationRequired = true;
                        _oauth.RequireReauthorization();
                        try { _cache.Delete(); }
                        catch (Exception cacheException) { AppLogger.WriteError("OAuth.client_secret_cache_cleanup", cacheException); }
                        ClearTaskData();
                        ClientIdSetupMessage = UiText.Get("OAuthClientRollbackFailed");
                        StatusText = ClientIdSetupMessage;
                        RaiseConnectionProperties();
                        RaiseContentProperties();
                        return;
                    }
                }

                _oauth.ClearAccessTokenForClientChange();
                try
                {
                    _cache.Delete();
                    _credentials.Delete();
                }
                catch (Exception exception)
                {
                    AppLogger.WriteError("OAuth.client_change_cleanup", exception);
                    // The new ID is already persisted. Keep it and prevent the old token from being used.
                    _oauth.RequireReauthorization();
                    ClearTaskData();
                    ClientSecretInput = string.Empty;
                    ClearExistingClientSecret = false;
                    _showClientIdSetupPanel = false;
                    StatusText = UiText.Get("OAuthClientChangeCleanupFailed");
                    RaiseConnectionProperties();
                    RaiseContentProperties();
                    return;
                }

                ClearTaskData();
            }

            _showClientIdSetupPanel = false;
            ClientSecretInput = string.Empty;
            ClearExistingClientSecret = false;
            ClientIdSetupMessage = string.Empty;
            if (changed) StatusText = UiText.Get("OAuthClientIdSaved");
            RaiseConnectionProperties();
            RaiseContentProperties();
        }
        finally
        {
            IsBusy = false;
            _mutationGate.Release();
            RaiseContentProperties();
        }
    }

    private void ClearTaskData()
    {
        _recurrences.Clear();
        _tasksByList.Clear();
        _completedTasksByList.Clear();
        _currentTasks = Array.Empty<TaskRecord>();
        _currentCompletedTasks = Array.Empty<TaskRecord>();
        TaskLists.Clear();
        Tasks.Clear();
        DatedTasks.Clear();
        UndatedTasks.Clear();
        CompletedTasks.Clear();
        SelectedList = null;
        _hasCache = false;
        _pollTimer.Stop();
    }
}
