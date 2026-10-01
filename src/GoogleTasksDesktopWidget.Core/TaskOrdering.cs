using GoogleTasksDesktopWidget.Core.Models;

namespace GoogleTasksDesktopWidget.Core;

public static class TaskOrdering
{
    public static IReadOnlyList<OrderedTask> Flatten(IEnumerable<TaskRecord> source) =>
        Flatten(source, TaskSortOrder.Manual);

    public static IReadOnlyList<OrderedTask> Flatten(IEnumerable<TaskRecord> source, TaskSortOrder sortOrder)
    {
        var tasks = source.ToArray();
        var children = tasks
            .GroupBy(task => task.ParentId ?? string.Empty, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => SortSiblings(group, sortOrder),
                StringComparer.Ordinal);
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<OrderedTask>(tasks.Length);

        void AddChildren(string parentId, int depth)
        {
            if (!children.TryGetValue(parentId, out var siblings))
            {
                return;
            }

            foreach (var task in siblings)
            {
                if (!visited.Add(task.Id))
                {
                    continue;
                }

                result.Add(new OrderedTask(task, depth));
                AddChildren(task.Id, depth + 1);
            }
        }

        AddChildren(string.Empty, 0);

        // Keep malformed/orphaned API rows visible while still guarding against cycles.
        foreach (var task in tasks)
        {
            if (visited.Add(task.Id))
            {
                result.Add(new OrderedTask(task, 0));
                AddChildren(task.Id, 1);
            }
        }

        return result;
    }

    private static TaskRecord[] SortSiblings(IEnumerable<TaskRecord> tasks, TaskSortOrder sortOrder)
    {
        var positioned = tasks.OrderBy(task => task.Position ?? string.Empty, StringComparer.Ordinal);
        return sortOrder switch
        {
            TaskSortOrder.Manual => positioned.ToArray(),
            TaskSortOrder.DueDate => positioned
                .OrderBy(task => task.DueDate is null)
                .ThenBy(task => task.DueDate)
                .ToArray(),
            TaskSortOrder.Title => positioned
                .OrderBy(task => task.Title, StringComparer.CurrentCultureIgnoreCase)
                .ToArray(),
            _ => throw new ArgumentOutOfRangeException(nameof(sortOrder))
        };
    }
}
