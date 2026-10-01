using System.Net;

namespace GoogleTasksDesktopWidget.Core;

public static class RetryPolicy
{
    public const int MaxRetries = 3;

    public static TimeSpan? GetDelay(HttpStatusCode statusCode, int completedRetries, bool allowTransientRetry = true)
    {
        if (!allowTransientRetry || completedRetries < 0 || completedRetries >= MaxRetries || !IsTransient(statusCode)) return null;
        return TimeSpan.FromSeconds(1 << completedRetries);
    }

    public static bool IsTransient(HttpStatusCode statusCode) =>
        statusCode == HttpStatusCode.TooManyRequests || (int)statusCode >= 500;

    public static bool RequiresReauthorization(HttpStatusCode statusCode, bool accessTokenWasRefreshed) =>
        statusCode == HttpStatusCode.Unauthorized && accessTokenWasRefreshed;
}
