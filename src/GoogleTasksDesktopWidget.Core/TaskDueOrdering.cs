using GoogleTasksDesktopWidget.Core.Models;

namespace GoogleTasksDesktopWidget.Core;

public static class TaskDueOrdering
{
    public static IReadOnlyList<TaskRecord> Order(IEnumerable<TaskRecord> tasks) => tasks
        .OrderBy(task => task.DueDate is null)
        .ThenBy(task => task.DueDate)
        .ThenBy(task => task.Position, StringComparer.Ordinal)
        .ToArray();
}
