using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using GoogleTasksDesktopWidget.Core;
using GoogleTasksDesktopWidget.Core.Models;

namespace GoogleTasksDesktopWidget.Infrastructure;

public sealed class GoogleTasksClient(OAuthService oauth)
{
    private const string ApiBase = "https://tasks.googleapis.com/tasks/v1";
    private static readonly HttpClient Http = new();
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly SemaphoreSlim _mutationQueue = new(1, 1);

    public async Task<IReadOnlyList<TaskListRecord>> GetTaskListsAsync(CancellationToken cancellationToken = default)
    {
        var result = new List<TaskListRecord>();
        string? pageToken = null;
        do
        {
            var query = "maxResults=1000" + (pageToken is null ? string.Empty : "&pageToken=" + Uri.EscapeDataString(pageToken));
            var page = await GetAsync<TaskListPage>($"{ApiBase}/users/@me/lists?{query}", cancellationToken).ConfigureAwait(false);
            if (page.Items is not null)
            {
                result.AddRange(page.Items.Where(item => !string.IsNullOrWhiteSpace(item.Id))
                    .Select(item => new TaskListRecord(item.Id!, item.Title ?? string.Empty)));
            }
            pageToken = page.NextPageToken;
        } while (!string.IsNullOrWhiteSpace(pageToken));

        return result;
    }

    public async Task<TaskListRecord> CreateTaskListAsync(string title, CancellationToken cancellationToken = default)
    {
        title = title.Trim();
        if (title.Length == 0 || title.Length > 1024)
            throw new ArgumentException("A task list title must contain 1 to 1024 characters.", nameof(title));

        await _mutationQueue.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var response = await SendAsync<TaskListApiItem>(
                () => CreateRequest(HttpMethod.Post, $"{ApiBase}/users/@me/lists", new { title }),
                cancellationToken, retryTransientResponses: false).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(response.Id))
                throw new GoogleApiException(HttpStatusCode.BadGateway);
            return new TaskListRecord(response.Id, response.Title ?? title);
        }
        finally { _mutationQueue.Release(); }
    }

    public async Task<IReadOnlyList<TaskRecord>> GetTasksAsync(string listId, CancellationToken cancellationToken = default)
        => await GetTasksAsync(listId, includeCompleted: false, cancellationToken).ConfigureAwait(false);

    public async Task<IReadOnlyList<TaskRecord>> GetCompletedTasksAsync(string listId, CancellationToken cancellationToken = default)
        => await GetTasksAsync(listId, includeCompleted: true, cancellationToken).ConfigureAwait(false);

    private async Task<IReadOnlyList<TaskRecord>> GetTasksAsync(string listId, bool includeCompleted, CancellationToken cancellationToken)
    {
        var result = new List<TaskRecord>();
        string? pageToken = null;
        do
        {
            var query = $"maxResults=100&showCompleted={includeCompleted.ToString().ToLowerInvariant()}&showDeleted=false&showHidden={includeCompleted.ToString().ToLowerInvariant()}&showAssigned=false" +
                (pageToken is null ? string.Empty : "&pageToken=" + Uri.EscapeDataString(pageToken));
            var url = $"{ApiBase}/lists/{Uri.EscapeDataString(listId)}/tasks?{query}";
            var page = await GetAsync<TaskPage>(url, cancellationToken).ConfigureAwait(false);
            if (page.Items is not null)
            {
                result.AddRange(page.Items.Where(item => !string.IsNullOrWhiteSpace(item.Id) &&
                                                         (!includeCompleted || string.Equals(item.Status, "completed", StringComparison.OrdinalIgnoreCase)))
                    .Select(item => new TaskRecord(
                        item.Id!,
                        item.Title ?? string.Empty,
                        item.Status ?? "needsAction",
                        TaskDatePolicy.ParseGoogleDueDate(item.Due),
                        item.Parent,
                        item.Position ?? string.Empty,
                        listId,
                        item.Notes,
                        ParseTimestamp(item.Updated),
                        ParseTimestamp(item.Completed),
                        item.WebViewLink)));
            }
            pageToken = page.NextPageToken;
        } while (!string.IsNullOrWhiteSpace(pageToken));

        return result;
    }

    public async Task<TaskRecord> CreateTaskAsync(string listId, string title, CancellationToken cancellationToken = default)
    {
        await _mutationQueue.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var response = await SendAsync<TaskApiItem>(
                () => CreateRequest(HttpMethod.Post, $"{ApiBase}/lists/{Uri.EscapeDataString(listId)}/tasks", new { title }),
                cancellationToken, retryTransientResponses: false).ConfigureAwait(false);
            return ToTaskRecord(response, listId);
        }
        finally { _mutationQueue.Release(); }
    }

    public async Task<TaskRecord> CreateTaskAsync(string listId, string title, string? notes, DateOnly? dueDate, CancellationToken cancellationToken = default)
    {
        await _mutationQueue.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var response = await SendAsync<TaskApiItem>(
                () => CreateRequest(HttpMethod.Post, $"{ApiBase}/lists/{Uri.EscapeDataString(listId)}/tasks", new
                {
                    title,
                    notes,
                    due = dueDate is { } date ? date.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) + "T00:00:00.000Z" : null
                }), cancellationToken, retryTransientResponses: false).ConfigureAwait(false);
            return ToTaskRecord(response, listId);
        }
        finally
        {
            _mutationQueue.Release();
        }
    }

    public async Task<TaskRecord> CreateSubtaskAsync(string listId, string parentTaskId, string title = "", string? previousTaskId = null,
        CancellationToken cancellationToken = default)
    {
        await _mutationQueue.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var query = new List<string> { "parent=" + Uri.EscapeDataString(parentTaskId) };
            if (!string.IsNullOrWhiteSpace(previousTaskId)) query.Add("previous=" + Uri.EscapeDataString(previousTaskId));
            var uri = $"{ApiBase}/lists/{Uri.EscapeDataString(listId)}/tasks?{string.Join('&', query)}";
            var response = await SendAsync<TaskApiItem>(
                () => CreateRequest(HttpMethod.Post, uri, new { title }),
                cancellationToken, retryTransientResponses: false).ConfigureAwait(false);
            return ToTaskRecord(response, listId);
        }
        finally { _mutationQueue.Release(); }
    }

    public async Task<TaskRecord> MoveTaskAsync(string listId, string taskId, string? destinationListId = null,
        string? parentTaskId = null, string? previousTaskId = null, CancellationToken cancellationToken = default)
    {
        await _mutationQueue.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var query = new List<string>();
            if (!string.IsNullOrWhiteSpace(parentTaskId)) query.Add("parent=" + Uri.EscapeDataString(parentTaskId));
            if (!string.IsNullOrWhiteSpace(previousTaskId)) query.Add("previous=" + Uri.EscapeDataString(previousTaskId));
            if (!string.IsNullOrWhiteSpace(destinationListId))
                query.Add("destinationTasklist=" + Uri.EscapeDataString(destinationListId));
            var suffix = query.Count == 0 ? string.Empty : "?" + string.Join('&', query);
            var uri = $"{TaskUrl(listId, taskId)}/move{suffix}";
            var response = await SendAsync<TaskApiItem>(
                () => CreateRequest(HttpMethod.Post, uri), cancellationToken).ConfigureAwait(false);
            return ToTaskRecord(response, string.IsNullOrWhiteSpace(destinationListId) ? listId : destinationListId);
        }
        finally { _mutationQueue.Release(); }
    }

    public async Task SetTaskCompletedAsync(string listId, string taskId, bool completed, CancellationToken cancellationToken = default)
    {
        await MutateAsync(() => SendAsync<JsonElement>(
            () => CreateRequest(HttpMethod.Patch, TaskUrl(listId, taskId), new { status = completed ? "completed" : "needsAction" }), cancellationToken), cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task SetTaskTitleAsync(string listId, string taskId, string title, CancellationToken cancellationToken = default)
    {
        await MutateAsync(() => SendAsync<JsonElement>(
            () => CreateRequest(HttpMethod.Patch, TaskUrl(listId, taskId), new { title }), cancellationToken), cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task UpdateTaskDetailsAsync(string listId, string taskId, string title, string? notes, DateOnly? dueDate, CancellationToken cancellationToken = default)
    {
        await MutateAsync(() => SendAsync<JsonElement>(
            () => CreateRequest(HttpMethod.Patch, TaskUrl(listId, taskId), new
            {
                title,
                notes = notes ?? string.Empty,
                due = dueDate is { } date ? date.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) + "T00:00:00.000Z" : null
            }), cancellationToken), cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteTaskAsync(string listId, string taskId, CancellationToken cancellationToken = default)
    {
        await MutateAsync(() => SendAsync<JsonElement>(
            () => new HttpRequestMessage(HttpMethod.Delete, TaskUrl(listId, taskId)), cancellationToken), cancellationToken)
            .ConfigureAwait(false);
    }

    private static string TaskUrl(string listId, string taskId) =>
        $"{ApiBase}/lists/{Uri.EscapeDataString(listId)}/tasks/{Uri.EscapeDataString(taskId)}";

    private static HttpRequestMessage CreateRequest(HttpMethod method, string uri, object? body = null)
    {
        var request = new HttpRequestMessage(method, uri);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (body is not null) request.Content = JsonContent.Create(body, options: Json);
        return request;
    }

    private async Task<T> GetAsync<T>(string uri, CancellationToken cancellationToken) =>
        await SendAsync<T>(() => CreateRequest(HttpMethod.Get, uri), cancellationToken).ConfigureAwait(false);

    private async Task<T> SendAsync<T>(Func<HttpRequestMessage> requestFactory, CancellationToken cancellationToken,
        bool retryTransientResponses = true)
    {
        var transientRetries = 0;
        var refreshedAfter401 = false;
        while (true)
        {
            var accessToken = await oauth.GetAccessTokenAsync(cancellationToken).ConfigureAwait(false);
            using var request = requestFactory();
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.Unauthorized && !refreshedAfter401)
            {
                refreshedAfter401 = true;
                await oauth.RefreshAfterUnauthorizedAsync(accessToken, cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (RetryPolicy.RequiresReauthorization(response.StatusCode, refreshedAfter401))
            {
                oauth.RequireReauthorization();
                throw await CreateApiExceptionAsync(response, cancellationToken).ConfigureAwait(false);
            }

            if (RetryPolicy.GetDelay(response.StatusCode, transientRetries, retryTransientResponses) is { } retryDelay)
            {
                transientRetries++;
                await Task.Delay(retryDelay, cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (response.StatusCode == HttpStatusCode.NoContent)
            {
                return default!;
            }

            if (response.StatusCode == HttpStatusCode.Forbidden)
            {
                throw await CreateApiExceptionAsync(response, cancellationToken).ConfigureAwait(false);
            }

            if (!response.IsSuccessStatusCode)
            {
                throw await CreateApiExceptionAsync(response, cancellationToken).ConfigureAwait(false);
            }

            if (typeof(T) == typeof(JsonElement) && response.Content.Headers.ContentLength is 0)
            {
                return default!;
            }

            var result = await response.Content.ReadFromJsonAsync<T>(Json, cancellationToken).ConfigureAwait(false);
            return result ?? throw new GoogleApiException(response.StatusCode);
        }
    }

    private static async Task<GoogleApiException> CreateApiExceptionAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var reasonCode = RemoteErrorDiagnostics.ParseGoogleApiReason(body);
        return new GoogleApiException(response.StatusCode, reasonCode);
    }

    private async Task MutateAsync(Func<Task<JsonElement>> mutation, CancellationToken cancellationToken)
    {
        await _mutationQueue.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _ = await mutation().ConfigureAwait(false);
        }
        finally
        {
            _mutationQueue.Release();
        }
    }

    private static TaskRecord ToTaskRecord(TaskApiItem item, string listId) => new(
        item.Id ?? Guid.NewGuid().ToString("N"),
        item.Title ?? string.Empty,
        item.Status ?? "needsAction",
        TaskDatePolicy.ParseGoogleDueDate(item.Due),
        item.Parent,
        item.Position ?? string.Empty,
        listId,
        item.Notes,
        ParseTimestamp(item.Updated),
        ParseTimestamp(item.Completed),
        item.WebViewLink);

    private static DateTimeOffset? ParseTimestamp(string? value) =>
        DateTimeOffset.TryParse(value, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal, out var result) ? result : null;

    private sealed class TaskListPage
    {
        [JsonPropertyName("items")] public List<TaskListApiItem>? Items { get; set; }
        [JsonPropertyName("nextPageToken")] public string? NextPageToken { get; set; }
    }

    private sealed class TaskPage
    {
        [JsonPropertyName("items")] public List<TaskApiItem>? Items { get; set; }
        [JsonPropertyName("nextPageToken")] public string? NextPageToken { get; set; }
    }

    private sealed class TaskListApiItem
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("title")] public string? Title { get; set; }
    }

    private sealed class TaskApiItem
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("title")] public string? Title { get; set; }
        [JsonPropertyName("status")] public string? Status { get; set; }
        [JsonPropertyName("due")] public string? Due { get; set; }
        [JsonPropertyName("parent")] public string? Parent { get; set; }
        [JsonPropertyName("position")] public string? Position { get; set; }
        [JsonPropertyName("notes")] public string? Notes { get; set; }
        [JsonPropertyName("updated")] public string? Updated { get; set; }
        [JsonPropertyName("completed")] public string? Completed { get; set; }
        [JsonPropertyName("webViewLink")] public string? WebViewLink { get; set; }
    }
}

public sealed class GoogleApiException(HttpStatusCode statusCode, string? reasonCode = null) : Exception
{
    public HttpStatusCode StatusCode { get; } = statusCode;
    public string? ReasonCode { get; } = reasonCode;
}
