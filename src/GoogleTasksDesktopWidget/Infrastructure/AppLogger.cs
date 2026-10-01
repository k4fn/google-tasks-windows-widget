using System.Text;

namespace GoogleTasksDesktopWidget.Infrastructure;

/// <summary>Writes operational events without task content, tokens, URLs, or exception messages.</summary>
public static class AppLogger
{
    private static readonly object Gate = new();
    private static readonly Dictionary<string, DateTimeOffset> LastEvents = new(StringComparer.Ordinal);
    private static readonly AppDataPaths Paths = AppDataPaths.Current;
    private static readonly string LogPath = Paths.LogFilePath;

    public static void WriteEvent(string eventCode, int errorCode = 0)
    {
        try
        {
            var eventTime = DateTimeOffset.UtcNow;
            var safeCode = Sanitize(eventCode);
            var line = $"{eventTime:O} {safeCode} error={errorCode}{Environment.NewLine}";
            lock (Gate)
            {
                if (LastEvents.TryGetValue(safeCode, out var lastEvent) && eventTime - lastEvent < TimeSpan.FromSeconds(30)) return;
                LastEvents[safeCode] = eventTime;
                Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
                if (File.Exists(LogPath) && new FileInfo(LogPath).Length > 512 * 1024)
                {
                    File.Delete(Paths.PreviousLogFilePath);
                    File.Move(LogPath, Paths.PreviousLogFilePath);
                }
                File.AppendAllText(LogPath, line, Encoding.UTF8);
            }
        }
        catch
        {
            // Logging must never prevent the widget from starting or syncing.
        }
    }

    public static void WriteRemoteFailure(string eventCode, int statusCode, string? reasonCode = null)
    {
        try
        {
            var eventTime = DateTimeOffset.UtcNow;
            var safeCode = Sanitize(eventCode);
            var safeReason = Sanitize(reasonCode ?? string.Empty);
            var line = $"{eventTime:O} {safeCode} status={statusCode} reason={safeReason}{Environment.NewLine}";
            lock (Gate)
            {
                if (LastEvents.TryGetValue(safeCode, out var lastEvent) && eventTime - lastEvent < TimeSpan.FromSeconds(30)) return;
                LastEvents[safeCode] = eventTime;
                Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
                if (File.Exists(LogPath) && new FileInfo(LogPath).Length > 512 * 1024)
                {
                    File.Delete(Paths.PreviousLogFilePath);
                    File.Move(LogPath, Paths.PreviousLogFilePath);
                }
                File.AppendAllText(LogPath, line, Encoding.UTF8);
            }
        }
        catch
        {
            // Logging must never prevent the widget from starting or syncing.
        }
    }

    public static void WriteError(string eventCode, Exception exception)
    {
        if (exception is OAuthFlowException oauthFailure)
        {
            WriteRemoteFailure($"{eventCode}.OAuth.{oauthFailure.Stage}", (int)(oauthFailure.StatusCode ?? 0), oauthFailure.ErrorCode);
            return;
        }

        if (exception is GoogleApiException apiFailure)
        {
            WriteRemoteFailure($"{eventCode}.GoogleTasksApi", (int)apiFailure.StatusCode, apiFailure.ReasonCode);
            return;
        }

        WriteEvent(eventCode + "." + exception.GetType().Name, exception.HResult);
    }

    private static string Sanitize(string value) =>
        new(value.Where(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-').Take(80).ToArray());
}
