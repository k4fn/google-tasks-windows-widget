using System.Globalization;
using GoogleTasksDesktopWidget.Core.Models;

namespace GoogleTasksDesktopWidget.Core;

public enum TaskFilter
{
    All,
    Today
}

public static class TaskDatePolicy
{
    public static DateOnly? ParseGoogleDueDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length < 10)
        {
            return null;
        }

        return DateOnly.TryParseExact(
            value.AsSpan(0, 10),
            "yyyy-MM-dd",
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out var date)
            ? date
            : null;
    }

    public static bool Includes(TaskRecord task, TaskFilter filter, DateOnly today) =>
        filter == TaskFilter.All || task.DueDate is { } due && due <= today;
}
