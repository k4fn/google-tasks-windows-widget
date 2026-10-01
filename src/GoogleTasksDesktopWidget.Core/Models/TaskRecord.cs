namespace GoogleTasksDesktopWidget.Core.Models;

public sealed record TaskRecord(
    string Id,
    string Title,
    string Status,
    DateOnly? DueDate,
    string? ParentId,
    string Position,
    string TaskListId = "",
    string? Notes = null,
    DateTimeOffset? Updated = null,
    DateTimeOffset? Completed = null,
    string? WebViewLink = null);

public sealed record OrderedTask(TaskRecord Task, int Depth);

public sealed record TaskListRecord(string Id, string Title);
