using System.Globalization;
using GoogleTasksDesktopWidget.Core.Models;

namespace GoogleTasksDesktopWidget.Core;

public sealed record TaskDateGroup(string Title, IReadOnlyList<TaskRecord> Tasks);

public static class TaskDateGrouping
{
    private static readonly CultureInfo JapaneseCulture = CultureInfo.GetCultureInfo("ja-JP");

    public static IReadOnlyList<TaskDateGroup> Group(IEnumerable<TaskRecord> tasks, DateOnly today)
    {
        var source = tasks.ToArray();
        var groups = new List<TaskDateGroup>();

        var dueThroughToday = source.Where(task => task.DueDate is { } due && due <= today).ToArray();
        if (dueThroughToday.Length > 0) groups.Add(new TaskDateGroup("今日", dueThroughToday));

        foreach (var dateGroup in source
                     .Where(task => task.DueDate is { } due && due > today)
                     .GroupBy(task => task.DueDate!.Value)
                     .OrderBy(group => group.Key))
        {
            groups.Add(new TaskDateGroup(dateGroup.Key.ToString("M月d日(ddd)", JapaneseCulture), dateGroup.ToArray()));
        }

        var withoutDueDate = source.Where(task => task.DueDate is null).ToArray();
        if (withoutDueDate.Length > 0) groups.Add(new TaskDateGroup("期限なし", withoutDueDate));

        return groups;
    }

    public static IReadOnlyList<TaskRecord> MostRecentlyCompleted(IEnumerable<TaskRecord> tasks) =>
        tasks.OrderByDescending(task => task.Completed ?? DateTimeOffset.MinValue)
            .ThenBy(task => task.Position, StringComparer.Ordinal)
            .ToArray();
}
