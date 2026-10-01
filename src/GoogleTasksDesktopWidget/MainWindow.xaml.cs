using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.ComponentModel;
using System.Windows.Threading;
using Microsoft.Win32;
using GoogleTasksDesktopWidget.Core.Models;
using GoogleTasksDesktopWidget.Core;
using GoogleTasksDesktopWidget.Infrastructure;
using GoogleTasksDesktopWidget.ViewModels;
using GoogleTasksDesktopWidget.Windows;

namespace GoogleTasksDesktopWidget;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly DesktopHostService _desktopHost;
    private readonly ObservableCollection<TaskItemViewModel> _visibleCompletedTasks = [];
    private Point? _lastDragPoint;
    private bool _isManualResize;
    private bool _completedRowsRefreshQueued;
    private bool _isSynchronizingCalendar;
    private Point _resizeStartScreen;
    private double _resizeStartWidth;
    private double _resizeStartHeight;
    private TaskItemViewModel? _expandedTask;
    private FrameworkElement? _expandedTaskRow;
    private TaskItemViewModel? _draftTask;
    private MenuItem? _createTaskListMenuItem;
    private TextBox? _createTaskListNameBox;
    private Button? _createTaskListSubmitButton;

    public MainWindow(MainViewModel viewModel, DesktopHostService desktopHost)
    {
        InitializeComponent();
        _viewModel = viewModel;
        _desktopHost = desktopHost;
        DataContext = viewModel;
        CompletedTasksItemsControl.ItemsSource = _visibleCompletedTasks;
        Width = Math.Clamp(_desktopHost.RestoreWidthDip, MinWidth, MaxWidth);
        Height = Math.Clamp(_desktopHost.RestoreHeightDip, MinHeight, MaxHeight);
        viewModel.PropertyChanged += OnViewModelPropertyChanged;
        viewModel.Tasks.CollectionChanged += OnOpenTasksCollectionChanged;
        viewModel.CompletedTasks.CollectionChanged += OnCompletedTasksCollectionChanged;
        SourceInitialized += (_, _) => _desktopHost.Attach(this);
        Closed += (_, _) =>
        {
            viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            viewModel.Tasks.CollectionChanged -= OnOpenTasksCollectionChanged;
            viewModel.CompletedTasks.CollectionChanged -= OnCompletedTasksCollectionChanged;
            _desktopHost.SavePosition();
            _desktopHost.Dispose();
        };
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        _desktopHost.UpdatePosition();
        await _viewModel.InitializeAsync();
    }

    private async void OnWindowDeactivated(object? sender, EventArgs e)
    {
        await SaveExpandedTaskAsync();
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!_isManualResize) _desktopHost.UpdatePosition();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.ClientSecretInput))
        {
            if (ClientSecretBox.Password != _viewModel.ClientSecretInput)
                ClientSecretBox.Password = _viewModel.ClientSecretInput;
            return;
        }
        if (e.PropertyName == nameof(MainViewModel.SelectedList) &&
            _expandedTask is { } selected &&
            selected.Task.TaskListId != _viewModel.SelectedList?.Id)
        {
            if (selected.IsDraft) DiscardDraftTask();
            else ClearInlineTaskEditorReference();
        }
        if (e.PropertyName == nameof(MainViewModel.IsConnected) && !_viewModel.IsConnected && _draftTask is not null)
            DiscardDraftTask();
        if (e.PropertyName == nameof(MainViewModel.IsCompletedSectionExpanded)) QueueCompletedRowsRefresh();
    }

    private async void OnWidgetRightClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject clicked &&
            FindVisualParent<Border>(clicked)?.DataContext is TaskItemViewModel)
        {
            return;
        }
        if (e.OriginalSource is DependencyObject source && FindAncestor<ContextMenu>(source) is not null) return;
        e.Handled = true;
        if (!await SaveExpandedTaskAsync()) return;
        WidgetMenu.PlacementTarget = this;
        WidgetMenu.IsOpen = true;
    }

    private async void OnMenuClick(object sender, RoutedEventArgs e)
    {
        if (!await SaveExpandedTaskAsync()) return;
        WidgetMenu.PlacementTarget = SettingsButton;
        WidgetMenu.IsOpen = true;
    }

    private async void OnListButtonClick(object sender, RoutedEventArgs e)
    {
        if (!await SaveExpandedTaskAsync()) return;
        var menu = (ContextMenu)FindResource("ListSelectorMenu");
        menu.PlacementTarget = ListButton;
        menu.IsOpen = true;
    }

    private void OnRefreshClick(object sender, RoutedEventArgs e)
    {
        _viewModel.RefreshCommand.Execute(null);
    }

    private void OnImportClientIdClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "Google OAuth Desktop client (*.json)|*.json|JSON files (*.json)|*.json",
            CheckFileExists = true,
            Multiselect = false,
            Title = UiText.Get("OAuthImportJson")
        };
        if (dialog.ShowDialog(this) == true) _viewModel.ImportClientIdFromJson(dialog.FileName);
    }

    private void OnClientSecretChanged(object sender, RoutedEventArgs e)
    {
        if (sender is PasswordBox passwordBox && passwordBox.Password != _viewModel.ClientSecretInput)
            _viewModel.ClientSecretInput = passwordBox.Password;
    }

    private void OnContextMenuOpened(object sender, RoutedEventArgs e)
    {
        LockMenuItem.IsChecked = _viewModel.IsLocked;
        AutoStartMenuItem.IsChecked = _viewModel.IsAutoStartEnabled;
        SystemThemeMenuItem.IsChecked = _viewModel.Theme == WidgetTheme.System;
        LightThemeMenuItem.IsChecked = _viewModel.Theme == WidgetTheme.Light;
        DarkThemeMenuItem.IsChecked = _viewModel.Theme == WidgetTheme.Dark;
    }

    private void OnSortMenuOpened(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem menu) return;
        foreach (var item in menu.Items.OfType<MenuItem>())
        {
            item.IsChecked = item.Tag?.ToString() switch
            {
                "Manual" => _viewModel.SortOrder == TaskSortOrder.Manual,
                "DueDate" => _viewModel.SortOrder == TaskSortOrder.DueDate,
                "Title" => _viewModel.SortOrder == TaskSortOrder.Title,
                _ => false
            };
        }
    }

    private async void OnSortOrderClick(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string value }) return;
        if (!await SaveExpandedTaskAsync()) return;
        var order = value switch
        {
            "DueDate" => TaskSortOrder.DueDate,
            "Title" => TaskSortOrder.Title,
            _ => TaskSortOrder.Manual
        };
        _viewModel.SetSortOrder(order);
    }

    private async void OnAddSubtaskClick(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem item || item.DataContext is not TaskItemViewModel parent) return;
        if (!await SaveExpandedTaskAsync()) return;
        var created = await _viewModel.CreateSubtaskAsync(parent);
        if (created is null) return;
        await Dispatcher.InvokeAsync(() =>
        {
            var itemsControl = created.HasDueDate ? DatedTasksItemsControl : OpenTasksItemsControl;
            if (itemsControl.ItemContainerGenerator.ContainerFromItem(created) is FrameworkElement row)
                OpenTaskDetails(row, created);
        }, DispatcherPriority.Loaded);
    }

    private void OnTaskMoveListMenuOpened(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem menu) return;
        menu.Items.Clear();
        var context = FindAncestor<ContextMenu>(menu);
        var task = context?.PlacementTarget is FrameworkElement row ? row.DataContext as TaskItemViewModel : null;
        var currentListId = task?.Task.TaskListId ?? _viewModel.SelectedList?.Id;
        menu.Header = _viewModel.TaskLists.FirstOrDefault(list => list.Id == currentListId)?.Title ?? "マイタスク";
        foreach (var list in _viewModel.TaskLists)
        {
            var moveItem = new MenuItem
            {
                Header = list.Title,
                IsCheckable = true,
                IsChecked = list.Id == currentListId,
                IsEnabled = list.Id != currentListId,
                CommandParameter = list
            };
            moveItem.Click += OnTaskMoveListClick;
            menu.Items.Add(moveItem);
        }
        if (menu.Items.Count == 0)
            menu.Items.Add(new MenuItem { Header = UiText.Get("NoLists"), IsEnabled = false });
    }

    private async void OnTaskMoveListClick(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { CommandParameter: TaskListRecord destination } menuItem) return;
        var context = FindAncestor<ContextMenu>(menuItem);
        if (context?.PlacementTarget is not FrameworkElement row || row.DataContext is not TaskItemViewModel task) return;
        if (!await SaveExpandedTaskAsync()) return;
        await _viewModel.MoveTaskToListAsync(task, destination);
    }

    private void OnListSelectorMenuOpened(object sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu menu) return;
        menu.Items.Clear();
        foreach (var list in _viewModel.TaskLists)
        {
            var item = new MenuItem
            {
                Header = list.Title,
                IsCheckable = true,
                IsChecked = _viewModel.SelectedList?.Id == list.Id,
                Command = _viewModel.SelectListCommand,
                CommandParameter = list
            };
            menu.Items.Add(item);
        }

        if (menu.Items.Count == 0)
        {
            menu.Items.Add(new MenuItem { Header = UiText.Get("NoLists"), IsEnabled = false });
        }
        menu.Items.Add(new Separator());
        var createListItem = new MenuItem
        {
            Header = "新しいリスト",
            StaysOpenOnClick = true
        };
        createListItem.Click += OnCreateTaskListClick;
        menu.Items.Add(createListItem);
    }

    private void OnLockMenuClick(object sender, RoutedEventArgs e) => _viewModel.ToggleLockCommand.Execute(null);
    private void OnAutoStartMenuClick(object sender, RoutedEventArgs e) => _viewModel.ToggleAutoStartCommand.Execute(null);

    private void OnThemeMenuClick(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem menuItem) return;
        var theme = ReferenceEquals(menuItem, SystemThemeMenuItem) ? WidgetTheme.System
            : ReferenceEquals(menuItem, LightThemeMenuItem) ? WidgetTheme.Light
            : WidgetTheme.Dark;
        _viewModel.SetThemeCommand.Execute(theme);
        OnContextMenuOpened(WidgetMenu, new RoutedEventArgs());
    }

    private void OnDragHandleMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_viewModel.IsLocked) return;
        _lastDragPoint = DragHandle.PointToScreen(e.GetPosition(DragHandle));
        DragHandle.CaptureMouse();
        e.Handled = true;
    }

    private void OnDragHandleMouseMove(object sender, MouseEventArgs e)
    {
        if (_lastDragPoint is not { } previous || e.LeftButton != MouseButtonState.Pressed || _viewModel.IsLocked) return;
        var current = DragHandle.PointToScreen(e.GetPosition(DragHandle));
        var dx = (int)Math.Round(current.X - previous.X);
        var dy = (int)Math.Round(current.Y - previous.Y);
        if (dx != 0 || dy != 0)
        {
            _desktopHost.MoveByPhysicalDelta(dx, dy);
            _lastDragPoint = current;
        }
    }

    private void OnDragHandleMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_lastDragPoint is null) return;
        _lastDragPoint = null;
        DragHandle.ReleaseMouseCapture();
        _desktopHost.SavePosition();
        e.Handled = true;
    }

    private void OnDragHandleLostCapture(object sender, MouseEventArgs e)
    {
        if (_lastDragPoint is null) return;
        _lastDragPoint = null;
        _desktopHost.SavePosition();
    }

    private void OnTaskRowMouseEnter(object sender, MouseEventArgs e)
    {
        if (sender is Border row) row.Background = (Brush)FindResource("HoverBrush");
    }

    private void OnTaskRowMouseLeave(object sender, MouseEventArgs e)
    {
        if (sender is Border row) row.Background = Brushes.Transparent;
    }

    private void OnOpenTasksCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => QueueTaskSelectionRefresh();

    private void OnCompletedTasksCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        QueueCompletedRowsRefresh();
        QueueTaskSelectionRefresh();
    }

    private void QueueCompletedRowsRefresh()
    {
        if (_completedRowsRefreshQueued) return;
        _completedRowsRefreshQueued = true;
        Dispatcher.BeginInvoke(() =>
        {
            _completedRowsRefreshQueued = false;
            if (!_viewModel.IsCompletedSectionExpanded)
            {
                _visibleCompletedTasks.Clear();
                return;
            }

            const int pageSize = 64;
            var source = _viewModel.CompletedTasks;
            var visibleCount = Math.Min(source.Count, Math.Max(pageSize, _visibleCompletedTasks.Count));
            ReconcileVisibleCompletedRows(source, visibleCount);
        }, DispatcherPriority.Background);
    }

    private void ReconcileVisibleCompletedRows(ObservableCollection<TaskItemViewModel> source, int visibleCount)
    {
        for (var index = 0; index < visibleCount; index++)
        {
            var desired = source[index];
            if (index < _visibleCompletedTasks.Count && ReferenceEquals(_visibleCompletedTasks[index], desired)) continue;

            var previousIndex = -1;
            for (var candidate = index + 1; candidate < _visibleCompletedTasks.Count; candidate++)
            {
                if (ReferenceEquals(_visibleCompletedTasks[candidate], desired))
                {
                    previousIndex = candidate;
                    break;
                }
            }

            if (previousIndex >= 0) _visibleCompletedTasks.Move(previousIndex, index);
            else if (index < _visibleCompletedTasks.Count &&
                     _visibleCompletedTasks[index].Id == desired.Id &&
                     _visibleCompletedTasks[index].Task.TaskListId == desired.Task.TaskListId)
                _visibleCompletedTasks[index] = desired;
            else _visibleCompletedTasks.Insert(index, desired);
        }

        while (_visibleCompletedTasks.Count > visibleCount)
            _visibleCompletedTasks.RemoveAt(_visibleCompletedTasks.Count - 1);
    }

    private void QueueTaskSelectionRefresh()
    {
        if (_expandedTask is null) return;
        Dispatcher.BeginInvoke(() =>
        {
            if (_expandedTask is not { } selected) return;
            var current = _viewModel.Tasks.Concat(_viewModel.CompletedTasks)
                .FirstOrDefault(task => task.Id == selected.Id && task.Task.TaskListId == selected.Task.TaskListId);
            if (current is not null) _expandedTask = current;
        }, DispatcherPriority.Background);
    }

    private void AppendNextCompletedPage()
    {
        const int pageSize = 64;
        var source = _viewModel.CompletedTasks;
        if (_visibleCompletedTasks.Count > source.Count)
            ReconcileVisibleCompletedRows(source, source.Count);
        var end = Math.Min(source.Count, _visibleCompletedTasks.Count + pageSize);
        for (var index = _visibleCompletedTasks.Count; index < end; index++)
        {
            _visibleCompletedTasks.Add(source[index]);
        }
    }

    private void OnTaskScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (!_viewModel.IsCompletedSectionExpanded || _visibleCompletedTasks.Count >= _viewModel.CompletedTasks.Count) return;
        var loadThreshold = Math.Max(160, e.ViewportHeight * 0.35);
        if (e.VerticalOffset + e.ViewportHeight >= e.ExtentHeight - loadThreshold) AppendNextCompletedPage();
    }

    private async void OnTaskRowMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || sender is not FrameworkElement row || row.DataContext is not TaskItemViewModel task) return;
        if (e.OriginalSource is DependencyObject source &&
            (FindAncestor<CheckBox>(source) is not null || FindAncestor<TextBox>(source) is not null ||
             FindAncestor<Button>(source) is not null || FindAncestor<System.Windows.Controls.Calendar>(source) is not null ||
             FindAncestor<ComboBox>(source) is not null)) return;
        e.Handled = true;
        if (task.IsDetailsExpanded)
        {
            return;
        }
        if (_expandedTask is { IsDetailsExpanded: true } && !await SaveExpandedTaskAsync())
        {
            return;
        }
        OpenTaskDetails(row, task);
    }

    private void OpenTaskDetails(FrameworkElement row, TaskItemViewModel task, bool focusDate = false)
    {
        if (!task.IsDetailsExpanded) task.BeginDetailsEditing();
        _expandedTask = task;
        _expandedTaskRow = row;

        Dispatcher.BeginInvoke(() =>
        {
            if (!task.IsDetailsExpanded) return;
            var title = FindVisualChild<TextBox>(row, "InlineTaskTitle");
            if (title is null) return;
            if (focusDate)
            {
                OpenTaskCalendar(row, task);
                FindVisualChild<Button>(row, "InlineTaskCalendarButton")?.Focus();
            }
            else
            {
                title.Focus();
                title.SelectAll();
            }
        }, DispatcherPriority.Input);
    }

    private async void OnTopAddClick(object sender, RoutedEventArgs e)
    {
        if (_draftTask is { } existingDraft)
        {
            if (IsBlankDraft(existingDraft)) DiscardDraftTask();
            else if (!await SaveExpandedTaskAsync()) return;
        }
        else if (!await SaveExpandedTaskAsync()) return;
        if (_viewModel.SelectedList is not { } selectedList) return;
        _draftTask = new TaskItemViewModel(new OrderedTask(
            new TaskRecord($"draft-{Guid.NewGuid():N}", string.Empty, "needsAction", null, null, "~", selectedList.Id), 0),
            isDraft: true);
        DraftTaskPresenter.Content = _draftTask;
        DraftTaskPresenter.Visibility = Visibility.Visible;
        _viewModel.IsDraftVisible = true;
        DraftTaskPresenter.UpdateLayout();
        OpenTaskDetails(DraftTaskPresenter, _draftTask);
        DraftTaskPresenter.BringIntoView();
    }

    private void OnWidgetSurfacePreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_draftTask is not { } draft || !IsBlankDraft(draft) ||
            e.OriginalSource is DependencyObject source && IsWithin(source, DraftTaskPresenter)) return;

        // Mouse-up produces Button.Click; update the tree after that routed event completes.
        Dispatcher.BeginInvoke(() =>
        {
            if (ReferenceEquals(_draftTask, draft) && IsBlankDraft(draft)) DiscardDraftTask();
        }, DispatcherPriority.Background);
    }

    private static bool IsBlankDraft(TaskItemViewModel draft) =>
        string.IsNullOrWhiteSpace(draft.EditDraft) &&
        string.IsNullOrWhiteSpace(draft.DraftNotes) &&
        draft.DraftDueDate is null;

    private async void OnCompletedToggleClick(object sender, RoutedEventArgs e)
    {
        if (!await SaveExpandedTaskAsync()) return;
        _viewModel.ToggleCompletedCommand.Execute(null);
    }

    private void CancelInlineTaskEditor()
    {
        if (_expandedTaskRow is { } row && FindDescendant<Popup>(row, "InlineTaskCalendarPopup") is { } popup)
            popup.IsOpen = false;
        if (_expandedTask is { } task)
        {
            task.CancelDetailsEditing();
            if (task.IsDraft)
            {
                DiscardDraftTask();
                return;
            }
        }
        ClearInlineTaskEditorReference();
    }

    private void DiscardDraftTask()
    {
        DraftTaskPresenter.Content = null;
        DraftTaskPresenter.Visibility = Visibility.Collapsed;
        _viewModel.IsDraftVisible = false;
        _draftTask = null;
        ClearInlineTaskEditorReference();
    }

    private static bool IsWithin(DependencyObject child, DependencyObject ancestor)
    {
        for (DependencyObject? current = child; current is not null;)
        {
            if (ReferenceEquals(current, ancestor)) return true;
            current = current is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(current)
                : LogicalTreeHelper.GetParent(current);
        }
        return false;
    }

    private void ClearInlineTaskEditorReference()
    {
        _expandedTask = null;
        _expandedTaskRow = null;
    }

    private async void OnInlineTaskTitleKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox title || title.DataContext is not TaskItemViewModel task) return;
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            await SaveInlineTaskAsync(task, FindVisualParent<Border>(title));
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            CancelInlineTaskEditor();
        }
    }

    private async void OnInlineTaskNotesKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox notes || notes.DataContext is not TaskItemViewModel task) return;
        if (e.Key == Key.Enter && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            e.Handled = true;
            await SaveInlineTaskAsync(task, FindVisualParent<Border>(notes));
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            CancelInlineTaskEditor();
        }
    }

    private void OnOpenTaskCalendarClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.DataContext is not TaskItemViewModel task ||
            FindVisualParent<Border>(button) is not { } row) return;
        OpenTaskCalendar(row, task);
    }

    private void OpenTaskCalendar(FrameworkElement row, TaskItemViewModel task)
    {
        var calendar = FindDescendant<System.Windows.Controls.Calendar>(row, "InlineTaskCalendar");
        var popup = FindDescendant<Popup>(row, "InlineTaskCalendarPopup");
        if (calendar is null || popup is null) return;
        _isSynchronizingCalendar = true;
        calendar.SelectedDate = task.DraftDueDate;
        calendar.DisplayDate = task.DraftDueDate ?? DateTime.Today;
        _isSynchronizingCalendar = false;
        popup.IsOpen = true;
    }

    private void OnInlineTaskCalendarSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isSynchronizingCalendar || sender is not System.Windows.Controls.Calendar { SelectedDate: { } selected } || _expandedTask is not { } task)
            return;
        task.DraftDueDate = selected.Date;
        if (_expandedTaskRow is { } row && FindDescendant<Popup>(row, "InlineTaskCalendarPopup") is { } popup)
            popup.IsOpen = false;
    }

    private async Task<bool> SaveExpandedTaskAsync()
    {
        if (_expandedTask is not { IsDetailsExpanded: true } task)
        {
            ClearInlineTaskEditorReference();
            return true;
        }
        if (task.IsDraft && IsBlankDraft(task))
        {
            DiscardDraftTask();
            return true;
        }
        return await SaveInlineTaskAsync(task, _expandedTaskRow);
    }

    private async Task<bool> SaveInlineTaskAsync(TaskItemViewModel task, FrameworkElement? row)
    {
        if (!task.IsDetailsExpanded) return true;
        var title = task.EditDraft.Trim();
        if (title.Length == 0)
        {
            if (row is not null) FindVisualChild<TextBox>(row, "InlineTaskTitle")?.Focus();
            return false;
        }
        var due = task.DraftDueDate is { } date ? DateOnly.FromDateTime(date) : (DateOnly?)null;
        if (task.IsDraft)
        {
            if (!await _viewModel.CreateTaskFromDraftAsync(title, task.DraftNotes, due)) return false;
            DiscardDraftTask();
            return true;
        }
        if (!await _viewModel.SaveTaskDetailsAsync(task, title, task.DraftNotes, due)) return false;
        task.IsDetailsExpanded = false;
        if (ReferenceEquals(_expandedTask, task)) ClearInlineTaskEditorReference();
        return true;
    }

    private void OnQuickDueClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string choice, DataContext: TaskItemViewModel task }) return;
        task.DraftDueDate = choice switch
        {
            "Today" => DateTime.Today,
            "Tomorrow" => DateTime.Today.AddDays(1),
            _ => null
        };
    }

    private async void OnTaskOptionsClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button) return;
        var row = FindVisualParent<Border>(button);
        if (row?.ContextMenu is not { } menu) return;
        e.Handled = true;
        if (!await SaveExpandedTaskAsync()) return;
        menu.PlacementTarget = row;
        menu.IsOpen = true;
    }

    private async void OnTaskContextMenuOpened(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.ContextMenu) return;
        await SaveExpandedTaskAsync();
    }

    private void OnTaskContextMenuClosed(object sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu menu || _createTaskListMenuItem is not { } item ||
            !ReferenceEquals(FindAncestor<ContextMenu>(item), menu)) return;
        RestoreCreateTaskListMenuItem(item);
    }

    private void OnCreateTaskListClick(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem item) return;
        e.Handled = true;
        if (e.OriginalSource is DependencyObject source && FindVisualParent<Button>(source) is not null) return;
        if (ReferenceEquals(_createTaskListMenuItem, item))
        {
            _createTaskListNameBox?.Focus();
            return;
        }

        var nameBox = new TextBox
        {
            Width = 145,
            MaxLength = 1024,
            ToolTip = "新しいタスクリスト名",
            Style = (Style)FindResource("WidgetTextBox"),
            Margin = new Thickness(2, 2, 4, 2),
            VerticalContentAlignment = VerticalAlignment.Center
        };
        nameBox.PreviewKeyDown += OnCreateTaskListNameKeyDown;
        var createButton = new Button
        {
            Content = "作成",
            Style = (Style)FindResource("QuietButton"),
            Foreground = (Brush)FindResource("ActionBlueBrush"),
            Padding = new Thickness(7, 4, 7, 4),
            Margin = new Thickness(0, 0, 2, 0),
            ToolTip = "リストを作成"
        };
        createButton.Click += OnCreateTaskListSubmitClick;
        var cancelButton = new Button
        {
            Content = "×",
            Style = (Style)FindResource("QuietButton"),
            Padding = new Thickness(5, 4, 5, 4),
            ToolTip = "キャンセル"
        };
        cancelButton.Click += OnCreateTaskListCancelClick;
        var content = new StackPanel { Orientation = Orientation.Horizontal, MinWidth = 225 };
        content.Children.Add(nameBox);
        content.Children.Add(createButton);
        content.Children.Add(cancelButton);

        _createTaskListMenuItem = item;
        _createTaskListNameBox = nameBox;
        _createTaskListSubmitButton = createButton;
        item.Header = content;
        item.StaysOpenOnClick = true;
        Dispatcher.BeginInvoke(() => nameBox.Focus(), DispatcherPriority.Input);
    }

    private async void OnCreateTaskListNameKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            await SubmitCreateTaskListAsync();
        }
        else if (e.Key == Key.Escape && _createTaskListMenuItem is { } item)
        {
            e.Handled = true;
            RestoreCreateTaskListMenuItem(item);
        }
    }

    private async void OnCreateTaskListSubmitClick(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        await SubmitCreateTaskListAsync();
    }

    private void OnCreateTaskListCancelClick(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (_createTaskListMenuItem is { } item) RestoreCreateTaskListMenuItem(item);
    }

    private async Task SubmitCreateTaskListAsync()
    {
        if (_createTaskListMenuItem is not { } item || _createTaskListNameBox is not { } nameBox) return;
        var title = nameBox.Text.Trim();
        if (title.Length == 0)
        {
            nameBox.Focus();
            return;
        }

        nameBox.IsEnabled = false;
        if (_createTaskListSubmitButton is { } submit) submit.IsEnabled = false;
        var menu = FindAncestor<ContextMenu>(item);
        var created = await _viewModel.CreateTaskListAsync(title);
        if (created)
        {
            RestoreCreateTaskListMenuItem(item);
            if (menu is not null) menu.IsOpen = false;
            return;
        }

        if (!ReferenceEquals(_createTaskListMenuItem, item)) return;
        nameBox.IsEnabled = true;
        if (_createTaskListSubmitButton is { } failedSubmit) failedSubmit.IsEnabled = true;
        nameBox.Focus();
        nameBox.SelectAll();
    }

    private void RestoreCreateTaskListMenuItem(MenuItem item)
    {
        item.Header = "新しいリスト";
        item.StaysOpenOnClick = false;
        if (!ReferenceEquals(_createTaskListMenuItem, item)) return;
        _createTaskListMenuItem = null;
        _createTaskListNameBox = null;
        _createTaskListSubmitButton = null;
    }

    private async void OnTaskDueMenuClick(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem item || FindAncestor<ContextMenu>(item)?.PlacementTarget is not FrameworkElement row ||
            row.DataContext is not TaskItemViewModel task) return;
        if (!ReferenceEquals(_expandedTask, task) && !await SaveExpandedTaskAsync()) return;
        OpenTaskDetails(row, task, focusDate: true);
    }

    private void OnResizeGripMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        _isManualResize = true;
        _resizeStartScreen = ResizeGrip.PointToScreen(e.GetPosition(ResizeGrip));
        _resizeStartWidth = Width;
        _resizeStartHeight = Height;
        ResizeGrip.CaptureMouse();
        e.Handled = true;
    }

    private void OnResizeGripMouseMove(object sender, MouseEventArgs e)
    {
        if (!_isManualResize || e.LeftButton != MouseButtonState.Pressed) return;
        var current = ResizeGrip.PointToScreen(e.GetPosition(ResizeGrip));
        var dpi = VisualTreeHelper.GetDpi(this);
        Width = Math.Clamp(_resizeStartWidth + (current.X - _resizeStartScreen.X) / dpi.DpiScaleX, MinWidth, MaxWidth);
        Height = Math.Clamp(_resizeStartHeight + (current.Y - _resizeStartScreen.Y) / dpi.DpiScaleY, MinHeight, MaxHeight);
        e.Handled = true;
    }

    private void OnResizeGripMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isManualResize) return;
        _isManualResize = false;
        ResizeGrip.ReleaseMouseCapture();
        _desktopHost.SavePosition();
        e.Handled = true;
    }

    private void OnResizeGripLostCapture(object sender, MouseEventArgs e)
    {
        if (!_isManualResize) return;
        _isManualResize = false;
        _desktopHost.SavePosition();
    }

    private async void OnTaskCompletionChanged(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox checkBox || checkBox.DataContext is not TaskItemViewModel task) return;
        var completed = checkBox.IsChecked == true;
        if (task.IsCompleted == completed) return;
        if (!await SaveExpandedTaskAsync())
        {
            checkBox.IsChecked = task.IsCompleted;
            return;
        }
        var row = FindVisualParent<Border>(checkBox);
        if (completed && row is not null && SystemParameters.ClientAreaAnimation)
        {
            var fade = new DoubleAnimation(0.18, TimeSpan.FromMilliseconds(240));
            row.BeginAnimation(OpacityProperty, fade);
            await Task.Delay(250);
        }

        var saved = await _viewModel.ToggleTaskCompletionAsync(task, completed);
        if (!saved && row is not null) row.BeginAnimation(OpacityProperty, null);
    }

    private async void OnDeleteTaskClick(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { DataContext: TaskItemViewModel task }) await _viewModel.DeleteTaskAsync(task);
    }

    private static T? FindVisualChild<T>(DependencyObject parent, string? name = null) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match &&
                (name is null || child is FrameworkElement element && element.Name == name)) return match;
            var nested = FindVisualChild<T>(child, name);
            if (nested is not null) return nested;
        }
        return null;
    }

    private static T? FindDescendant<T>(DependencyObject parent, string name) where T : DependencyObject
    {
        var pending = new Stack<DependencyObject>();
        var visited = new HashSet<DependencyObject>();
        pending.Push(parent);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            if (!visited.Add(current)) continue;
            if (current is FrameworkElement element && element.Name == name && current is T match)
                return match;
            if (current is Visual or System.Windows.Media.Media3D.Visual3D)
            {
                for (var index = 0; index < VisualTreeHelper.GetChildrenCount(current); index++)
                    pending.Push(VisualTreeHelper.GetChild(current, index));
            }
            if (current is FrameworkElement or FrameworkContentElement)
            {
                foreach (var child in LogicalTreeHelper.GetChildren(current).OfType<DependencyObject>())
                    pending.Push(child);
            }
        }
        return null;
    }

    private static T? FindVisualParent<T>(DependencyObject? child) where T : DependencyObject
    {
        while (child is not null)
        {
            if (child is T match) return match;
            child = VisualTreeHelper.GetParent(child);
        }
        return null;
    }

    private static T? FindAncestor<T>(DependencyObject? child) where T : DependencyObject
    {
        while (child is not null)
        {
            if (child is T match) return match;
            child = child is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(child)
                : LogicalTreeHelper.GetParent(child);
        }
        return null;
    }
}

public sealed class EmptyStringToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        string.IsNullOrWhiteSpace(value as string) ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class TaskDepthPaddingConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var depth = value is int level ? Math.Clamp(level, 0, 8) : 0;
        return new Thickness(4 + depth * 13, 5, 3, 6);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
