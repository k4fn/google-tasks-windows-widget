using GoogleTasksDesktopWidget.Core.Models;

namespace GoogleTasksDesktopWidget.Core;

public static class TaskMutationPolicy
{
    public static TaskRecord SetCompleted(TaskRecord task, bool completed, string latestTitle, DateTimeOffset completedAt) =>
        task with
        {
            Title = latestTitle,
            Status = completed ? "completed" : "needsAction",
            Completed = completed ? completedAt : null
        };
}
