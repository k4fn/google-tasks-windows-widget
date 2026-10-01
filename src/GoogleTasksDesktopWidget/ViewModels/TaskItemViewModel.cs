using System.Globalization;
using GoogleTasksDesktopWidget.Core.Models;
using GoogleTasksDesktopWidget.Infrastructure;

namespace GoogleTasksDesktopWidget.ViewModels;

public sealed class TaskItemViewModel : ObservableObject
{
    private string _title;
    private string _editDraft;
    private string _draftNotes;
    private DateTime? _draftDueDate;
    private bool _isCompleted;
    private bool _isEditing;
    private bool _isDetailsExpanded;
    private int _depth;
    private string _detailsBaseTitle;
    private string _detailsBaseNotes;
    private DateTime? _detailsBaseDueDate;
    private DateOnly _lastLocalDate = DateOnly.FromDateTime(DateTime.Today);

    public TaskItemViewModel(OrderedTask orderedTask, string dateGroupTitle = "", bool isDateGroupStart = false, bool isDraft = false)
    {
        IsDraft = isDraft;
        Task = orderedTask.Task;
        _depth = orderedTask.Depth;
        _title = Task.Title;
        _editDraft = _title;
        _draftNotes = Task.Notes ?? string.Empty;
        _draftDueDate = Task.DueDate?.ToDateTime(TimeOnly.MinValue);
        _detailsBaseTitle = _title;
        _detailsBaseNotes = _draftNotes;
        _detailsBaseDueDate = _draftDueDate;
        _isCompleted = string.Equals(Task.Status, "completed", StringComparison.OrdinalIgnoreCase);
        DateGroupTitle = dateGroupTitle;
        IsDateGroupStart = isDateGroupStart;
    }

    public TaskRecord Task { get; private set; }
    public bool IsDraft { get; }
    public bool IsPersisted => !IsDraft;
    public string Id => Task.Id;
    public string? ParentId => Task.ParentId;
    public int Depth => _depth;
    public string DueDateText
    {
        get
        {
            if (Task.DueDate is not { } due) return string.Empty;
            var today = DateOnly.FromDateTime(DateTime.Today);
            if (due == today) return "今日";
            if (today > DateOnly.MinValue && due == today.AddDays(-1)) return "昨日";
            if (today < DateOnly.MaxValue && due == today.AddDays(1)) return "明日";
            return due.ToString("M月d日(ddd)", CultureInfo.GetCultureInfo("ja-JP"));
        }
    }
    public bool IsOverdue => Task.DueDate is { } due && due < DateOnly.FromDateTime(DateTime.Today);
    public bool HasDueDate => Task.DueDate is not null;
    public string DateGroupTitle { get; }
    public bool IsDateGroupStart { get; }
    public DateTimeOffset? CompletedAt => Task.Completed;
    public string CompletedDateText => Task.Completed is { } completed
        ? $"完了日: {completed.ToLocalTime().ToString("M月d日(ddd)", CultureInfo.GetCultureInfo("ja-JP"))}"
        : string.Empty;

    public string Title
    {
        get => _title;
        set => SetProperty(ref _title, value);
    }

    public string EditDraft
    {
        get => _editDraft;
        set => SetProperty(ref _editDraft, value);
    }

    public string DraftNotes
    {
        get => _draftNotes;
        set => SetProperty(ref _draftNotes, value);
    }

    public DateTime? DraftDueDate
    {
        get => _draftDueDate;
        set => SetProperty(ref _draftDueDate, value?.Date);
    }

    public bool IsCompleted
    {
        get => _isCompleted;
        set => SetProperty(ref _isCompleted, value);
    }

    public bool IsEditing
    {
        get => _isEditing;
        set => SetProperty(ref _isEditing, value);
    }

    public bool IsDetailsExpanded
    {
        get => _isDetailsExpanded;
        set => SetProperty(ref _isDetailsExpanded, value);
    }

    public void BeginEditing()
    {
        EditDraft = Title;
        IsEditing = true;
    }

    public void BeginDetailsEditing()
    {
        EditDraft = Title;
        DraftNotes = Task.Notes ?? string.Empty;
        DraftDueDate = Task.DueDate?.ToDateTime(TimeOnly.MinValue);
        _detailsBaseTitle = Title;
        _detailsBaseNotes = DraftNotes;
        _detailsBaseDueDate = DraftDueDate;
        IsDetailsExpanded = true;
    }

    public void CancelDetailsEditing()
    {
        EditDraft = Title;
        DraftNotes = Task.Notes ?? string.Empty;
        DraftDueDate = Task.DueDate?.ToDateTime(TimeOnly.MinValue);
        _detailsBaseTitle = Title;
        _detailsBaseNotes = DraftNotes;
        _detailsBaseDueDate = DraftDueDate;
        IsDetailsExpanded = false;
    }

    public void UpdateTask(OrderedTask orderedTask)
    {
        var task = orderedTask.Task;
        if (Task == task && Depth == orderedTask.Depth) return;
        if (_depth != orderedTask.Depth)
        {
            _depth = orderedTask.Depth;
            OnPropertyChanged(nameof(Depth));
        }
        if (Task == task) return;
        var newDueDate = task.DueDate?.ToDateTime(TimeOnly.MinValue);
        if (IsDetailsExpanded)
        {
            // Polls can bring in edits made elsewhere while this editor is open.
            // Rebase untouched fields and keep drafts the user has changed.
            if (EditDraft == _detailsBaseTitle) EditDraft = task.Title;
            if (DraftNotes == _detailsBaseNotes) DraftNotes = task.Notes ?? string.Empty;
            if (DraftDueDate == _detailsBaseDueDate) DraftDueDate = newDueDate;
            _detailsBaseTitle = task.Title;
            _detailsBaseNotes = task.Notes ?? string.Empty;
            _detailsBaseDueDate = newDueDate;
        }
        Task = task;
        OnPropertyChanged(nameof(Task));
        OnPropertyChanged(nameof(ParentId));
        OnPropertyChanged(nameof(DueDateText));
        OnPropertyChanged(nameof(IsOverdue));
        OnPropertyChanged(nameof(HasDueDate));
        OnPropertyChanged(nameof(CompletedAt));
        OnPropertyChanged(nameof(CompletedDateText));
        if (!IsEditing)
        {
            Title = task.Title;
            if (!IsDetailsExpanded) EditDraft = task.Title;
        }
        IsCompleted = string.Equals(task.Status, "completed", StringComparison.OrdinalIgnoreCase);
    }

    public void RefreshDateStatus()
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        if (_lastLocalDate == today) return;
        _lastLocalDate = today;
        OnPropertyChanged(nameof(DueDateText));
        OnPropertyChanged(nameof(IsOverdue));
    }
}
