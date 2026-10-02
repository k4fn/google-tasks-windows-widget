using GoogleTasksDesktopWidget.Core;
using GoogleTasksDesktopWidget.Core.Models;
using GoogleTasksDesktopWidget.Infrastructure;
using GoogleTasksDesktopWidget.ViewModels;
using GoogleTasksDesktopWidget.Windows;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

var tests = new (string Name, Action Run)[]
{
    ("Google due dates use the date segment", DueDateUsesDateSegment),
    ("Today includes overdue and current due dates", TodayIncludesDueOnOrBeforeToday),
    ("Tasks group by today, future due date, and no due date", TasksGroupByDueDate),
    ("Completed tasks sort by most recent completion", CompletedTasksSortByCompletionTime),
    ("Completion mutations preserve the latest edited title", CompletionMutationPreservesLatestTitle),
    ("Cached tasks belong to the current refresh token", CachedTasksAreBoundToCredential),
    ("Tasks sort by position and flatten depth first", TasksSortAndFlattenDepthFirst),
    ("Task sort modes preserve parent and child order", TaskSortModesPreserveHierarchy),
    ("Visible tasks sort by due date with undated last", VisibleTasksSortByDueDate),
    ("Recurrence advances calendar dates", RecurrenceAdvancesCalendarDates),
    ("Pending recurrence requires user review before retry", PendingRecurrenceRequiresReview),
    ("Orphaned tasks remain visible", OrphanedTasksRemainVisible),
    ("PKCE verifier follows S256 encoding", PkceVerifierUsesS256),
    ("Transient API retries use 1, 2, and 4 seconds", TransientRetriesBackOff),
    ("Task insertion does not retry uncertain transient responses", TaskInsertionDoesNotRetryTransientResponses),
    ("A second 401 requires reauthorization", SecondUnauthorizedRequiresReauthorization),
    ("OAuth diagnostics allow only known error codes", OAuthDiagnosticsAllowlistCodes),
    ("Google Tasks diagnostics allow only known reason codes", GoogleApiDiagnosticsAllowlistCodes),
    ("Google Tasks 403 reasons show actionable guidance", GoogleApiForbiddenReasonsHaveSpecificGuidance),
    ("Desktop OAuth JSON imports client ID and optional secret", DesktopOAuthJsonImportsClientCredentials),
    ("OAuth client IDs are validated", OAuthClientIdsAreValidated),
    ("OAuth missing-secret errors are identified without exposing descriptions", OAuthSecretRequirementIsDetectedSafely),
    ("Settings migration adopts the light redesign", SettingsStoreMigratesSystemThemeToDark),
    ("Portable paths keep all data beside the executable", PortablePathsKeepAllDataTogether),
    ("Legacy migration copies and verifies data while retaining the source", LegacyMigrationCopiesDataAndRetainsSource),
    ("Portable files win conflicts and are not repeatedly reported", ExistingPortableFilesWinMigrationConflicts),
    ("Legacy data arriving after an empty first run can still migrate", LegacyDataAfterEmptyFirstRunMigrates),
    ("New portable autostart defaults off and saved legacy settings persist", PortableAutostartDefaultsAndLegacySettingsPersist),
    ("Invalid settings remain available for recovery", InvalidSettingsRemainAvailableForRecovery),
};

var passedCount = 0;
foreach (var test in tests)
{
    test.Run();
    passedCount++;
    Console.WriteLine($"PASS {test.Name}");
}

await LoopbackCallbackIgnoresInvalidConnections();
passedCount++;
Console.WriteLine("PASS Loopback OAuth ignores malformed and wrong-state connections");

await CredentialStoreProtectsAndLoadsTokens();
passedCount++;
Console.WriteLine("PASS CredentialStore protects, loads, and deletes refresh tokens");
await ClientSecretStoreProtectsAndScopesSecretToClientId();
passedCount++;
Console.WriteLine("PASS OAuthClientSecretStore protects and scopes the secret to its client ID");
await EncryptedCacheStoreRoundTripsAndReplacesSnapshots();
passedCount++;
Console.WriteLine("PASS EncryptedCacheStore protects, loads, and replaces snapshots");
SettingsStorePersistsOAuthClientId();
passedCount++;
Console.WriteLine("PASS SettingsStore persists the user-configured OAuth client ID");
await ClientIdSettingsFailurePreservesOldCredentialsAndCache();
passedCount++;
Console.WriteLine("PASS Client ID settings failure preserves the old token and cache");
await ClientSecretSaveFailureRollsBackConfigurationAndKeepsTheOldConnection();
passedCount++;
Console.WriteLine("PASS Client Secret save failure rolls back to the old connection");
await ClientIdCleanupFailureRequiresReauthorization();
passedCount++;
Console.WriteLine("PASS Client ID cleanup failure keeps the new ID and blocks the old token");

Console.WriteLine($"Passed {passedCount} core tests.");
TaskRowMemoryTests.Run();

static void DueDateUsesDateSegment()
{
    Equal(new DateOnly(2026, 9, 30), TaskDatePolicy.ParseGoogleDueDate("2026-09-30T00:00:00.000Z"));
    Equal<DateOnly?>(null, TaskDatePolicy.ParseGoogleDueDate("invalid"));
}

static void TodayIncludesDueOnOrBeforeToday()
{
    var today = new DateOnly(2026, 9, 30);
    True(TaskDatePolicy.Includes(Task("late", new DateOnly(2026, 9, 29)), TaskFilter.Today, today));
    True(TaskDatePolicy.Includes(Task("today", today), TaskFilter.Today, today));
    False(TaskDatePolicy.Includes(Task("tomorrow", new DateOnly(2026, 10, 1)), TaskFilter.Today, today));
    False(TaskDatePolicy.Includes(Task("no due date", null), TaskFilter.Today, today));
    True(TaskDatePolicy.Includes(Task("no due date", null), TaskFilter.All, today));
}

static void TasksGroupByDueDate()
{
    var today = new DateOnly(2026, 10, 1);
    var groups = TaskDateGrouping.Group(new[]
    {
        Task("no-due", null),
        Task("future-next", new DateOnly(2026, 10, 3)),
        Task("today", today),
        Task("overdue", new DateOnly(2026, 9, 30)),
        Task("future-first", new DateOnly(2026, 10, 2)),
    }, today);

    Equal("今日,10月2日(金),10月3日(土),期限なし", string.Join(',', groups.Select(group => group.Title)));
    Equal("today,overdue", string.Join(',', groups[0].Tasks.Select(task => task.Id)));
}

static void CompletedTasksSortByCompletionTime()
{
    var ordered = TaskDateGrouping.MostRecentlyCompleted(new[]
    {
        Task("older", null) with { Status = "completed", Completed = DateTimeOffset.Parse("2026-09-30T12:00:00Z") },
        Task("newest", null) with { Status = "completed", Completed = DateTimeOffset.Parse("2026-10-01T08:00:00Z") },
        Task("missing-time", null) with { Status = "completed" },
    });

    Equal("newest,older,missing-time", string.Join(',', ordered.Select(task => task.Id)));
}

static void VisibleTasksSortByDueDate()
{
    var ordered = TaskDueOrdering.Order(new[]
    {
        Task("undated", null), Task("later", new DateOnly(2026, 10, 15)),
        Task("earlier", new DateOnly(2026, 10, 2)), Task("same", new DateOnly(2026, 10, 2))
    });
    Equal("earlier,same,later,undated", string.Join(',', ordered.Select(task => task.Id)));
}

static void RecurrenceAdvancesCalendarDates()
{
    Equal(new DateOnly(2026, 10, 2), RecurrencePolicy.Next(new DateOnly(2026, 10, 1), RecurrenceFrequency.Daily));
    Equal(new DateOnly(2026, 10, 8), RecurrencePolicy.Next(new DateOnly(2026, 10, 1), RecurrenceFrequency.Weekly));
    Equal(new DateOnly(2026, 2, 28), RecurrencePolicy.Next(new DateOnly(2026, 1, 31), RecurrenceFrequency.Monthly));
    Equal(new DateOnly(2026, 10, 2), RecurrencePolicy.NextAfter(new DateOnly(2026, 9, 28), new DateOnly(2026, 10, 1), RecurrenceFrequency.Daily));
    Equal(new DateOnly(2026, 10, 5), RecurrencePolicy.NextAfter(new DateOnly(2026, 9, 28), new DateOnly(2026, 10, 1), RecurrenceFrequency.Weekly));
    Equal(new DateOnly(2026, 3, 31), RecurrencePolicy.NextAfter(new DateOnly(2026, 1, 31), new DateOnly(2026, 3, 1), RecurrenceFrequency.Monthly));
}

static void TaskInsertionDoesNotRetryTransientResponses()
{
    Equal<TimeSpan?>(null, RetryPolicy.GetDelay(HttpStatusCode.TooManyRequests, 0, allowTransientRetry: false));
    Equal<TimeSpan?>(null, RetryPolicy.GetDelay(HttpStatusCode.InternalServerError, 0, allowTransientRetry: false));
    Equal(TimeSpan.FromSeconds(1), RetryPolicy.GetDelay(HttpStatusCode.InternalServerError, 0, allowTransientRetry: true));
}

static void PendingRecurrenceRequiresReview()
{
    True(RecurrencePolicy.CanAutomaticallyCreate(RecurrenceFrequency.Daily, pending: false));
    False(RecurrencePolicy.CanAutomaticallyCreate(RecurrenceFrequency.Daily, pending: true));
    False(RecurrencePolicy.CanAutomaticallyCreate(RecurrenceFrequency.None, pending: false));
}

static void CachedTasksAreBoundToCredential()
{
    var fingerprint = CacheIdentityPolicy.FingerprintRefreshToken("refresh-token-a");
    var sameCredential = CacheIdentityPolicy.FingerprintRefreshToken("refresh-token-a");
    var differentCredential = CacheIdentityPolicy.FingerprintRefreshToken("refresh-token-b");

    True(fingerprint is not null);
    False(string.Equals("refresh-token-a", fingerprint, StringComparison.Ordinal));
    True(CacheIdentityPolicy.MatchesOwner("client-id", "client-id", fingerprint, sameCredential));
    False(CacheIdentityPolicy.MatchesOwner("client-id", "client-id", fingerprint, differentCredential));
    False(CacheIdentityPolicy.MatchesOwner("client-id", "client-id", null, sameCredential));
    False(CacheIdentityPolicy.MatchesOwner("client-id", "other-client-id", fingerprint, sameCredential));
}

static void CompletionMutationPreservesLatestTitle()
{
    var oldTask = Task("task", null) with { Title = "Before edit" };
    var completedAt = DateTimeOffset.Parse("2026-10-01T08:00:00Z");
    var completed = TaskMutationPolicy.SetCompleted(oldTask, true, "Latest edit", completedAt);
    Equal("Latest edit", completed.Title);
    Equal("completed", completed.Status);
    Equal(completedAt, completed.Completed);

    var restored = TaskMutationPolicy.SetCompleted(completed, false, completed.Title, completedAt.AddMinutes(1));
    Equal("Latest edit", restored.Title);
    Equal("needsAction", restored.Status);
    Equal<DateTimeOffset?>(null, restored.Completed);
}

static void TasksSortAndFlattenDepthFirst()
{
    var ordered = TaskOrdering.Flatten(new[]
    {
        Task("child-b", null, "root-b", "2"),
        Task("root-b", null, null, "b"),
        Task("grandchild", null, "child-a", "1"),
        Task("root-a", null, null, "a"),
        Task("child-a", null, "root-a", "z"),
    });

    Equal("root-a,child-a,grandchild,root-b,child-b", string.Join(',', ordered.Select(item => item.Task.Id)));
    Equal("0,1,2,0,1", string.Join(',', ordered.Select(item => item.Depth)));
}

static void TaskSortModesPreserveHierarchy()
{
    var tasks = new[]
    {
        Task("root-z", new DateOnly(2026, 10, 1), null, "2") with { Title = "Zebra" },
        Task("child-z", null, "root-z", "1") with { Title = "Child" },
        Task("root-a", new DateOnly(2026, 10, 3), null, "1") with { Title = "Alpha" },
    };

    var manual = TaskOrdering.Flatten(tasks, TaskSortOrder.Manual);
    Equal("root-a,root-z,child-z", string.Join(',', manual.Select(item => item.Task.Id)));

    var byDueDate = TaskOrdering.Flatten(tasks, TaskSortOrder.DueDate);
    Equal("root-z,child-z,root-a", string.Join(',', byDueDate.Select(item => item.Task.Id)));
    Equal("0,1,0", string.Join(',', byDueDate.Select(item => item.Depth)));

    var byTitle = TaskOrdering.Flatten(tasks, TaskSortOrder.Title);
    Equal("root-a,root-z,child-z", string.Join(',', byTitle.Select(item => item.Task.Id)));
}

static void OrphanedTasksRemainVisible()
{
    var ordered = TaskOrdering.Flatten(new[] { Task("orphan", null, "missing-parent", "1") });
    Equal(1, ordered.Count);
    Equal("orphan", ordered[0].Task.Id);
}

static void PkceVerifierUsesS256()
{
    var pair = PkceGenerator.Create();
    Equal(43, pair.Verifier.Length);
    True(pair.Verifier.All(IsBase64UrlCharacter));
    Equal(43, pair.Challenge.Length);
    True(pair.Challenge.All(IsBase64UrlCharacter));
    var expected = PkceGenerator.Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(pair.Verifier)));
    Equal(expected, pair.Challenge);
}

static bool IsBase64UrlCharacter(char value) =>
    char.IsAsciiLetterOrDigit(value) || value is '-' or '_';

static void TransientRetriesBackOff()
{
    Equal(TimeSpan.FromSeconds(1), RetryPolicy.GetDelay(HttpStatusCode.TooManyRequests, 0));
    Equal(TimeSpan.FromSeconds(2), RetryPolicy.GetDelay(HttpStatusCode.ServiceUnavailable, 1));
    Equal(TimeSpan.FromSeconds(4), RetryPolicy.GetDelay(HttpStatusCode.InternalServerError, 2));
    Equal<TimeSpan?>(null, RetryPolicy.GetDelay(HttpStatusCode.BadGateway, 3));
    Equal<TimeSpan?>(null, RetryPolicy.GetDelay(HttpStatusCode.Forbidden, 0));
}

static void SecondUnauthorizedRequiresReauthorization()
{
    True(RetryPolicy.RequiresReauthorization(HttpStatusCode.Unauthorized, accessTokenWasRefreshed: true));
    False(RetryPolicy.RequiresReauthorization(HttpStatusCode.Unauthorized, accessTokenWasRefreshed: false));
    False(RetryPolicy.RequiresReauthorization(HttpStatusCode.Forbidden, accessTokenWasRefreshed: true));
}

static void OAuthDiagnosticsAllowlistCodes()
{
    const string response = """{"error":"invalid_grant","error_description":"do not log refresh_token or private task title"}""";
    Equal("invalid_grant", RemoteErrorDiagnostics.ParseOAuthErrorCode(response));
    Equal("other", RemoteErrorDiagnostics.ParseOAuthErrorCode("""{"error":"private-task-title"}"""));
    Equal<string?>(null, RemoteErrorDiagnostics.ParseOAuthErrorCode("not json"));
}

static void GoogleApiDiagnosticsAllowlistCodes()
{
    const string response = """{"error":{"code":403,"message":"private task title","errors":[{"reason":"accessNotConfigured"}]}}""";
    Equal("accessNotConfigured", RemoteErrorDiagnostics.ParseGoogleApiReason(response));
    Equal("other", RemoteErrorDiagnostics.ParseGoogleApiReason("""{"error":{"errors":[{"reason":"private-task-title"}]}}"""));
}

static void GoogleApiForbiddenReasonsHaveSpecificGuidance()
{
    Equal(GoogleApiErrorKind.TasksApiNotEnabled, GoogleApiErrorPolicy.ClassifyForbiddenReason("accessNotConfigured"));
    Equal(GoogleApiErrorKind.TasksApiNotEnabled, GoogleApiErrorPolicy.ClassifyForbiddenReason("serviceDisabled"));
    Equal(GoogleApiErrorKind.InsufficientPermissions, GoogleApiErrorPolicy.ClassifyForbiddenReason("insufficientPermissions"));
    Equal(GoogleApiErrorKind.DomainPolicy, GoogleApiErrorPolicy.ClassifyForbiddenReason("domainPolicy"));
    Equal(GoogleApiErrorKind.PermissionDenied, GoogleApiErrorPolicy.ClassifyForbiddenReason("other"));
    Equal(GoogleApiErrorKind.PermissionDenied, GoogleApiErrorPolicy.ClassifyForbiddenReason(null));
}

static void DesktopOAuthJsonImportsClientCredentials()
{
    var directory = CreateTestDirectory();
    try
    {
        var path = Path.Combine(directory, "desktop-client.json");
        File.WriteAllText(path, """{"installed":{"client_id":"123456789-test.apps.googleusercontent.com","client_secret":"test-secret-not-a-real-credential"}}""");
        var configuration = OAuthClientIdConfiguration.ReadDesktopClientConfiguration(path);
        Equal("123456789-test.apps.googleusercontent.com", configuration.ClientId);
        Equal("test-secret-not-a-real-credential", configuration.ClientSecret);

        var idOnlyPath = Path.Combine(directory, "id-only.json");
        File.WriteAllText(idOnlyPath, """{"installed":{"client_id":"123456789-test.apps.googleusercontent.com"}}""");
        Equal<string?>(null, OAuthClientIdConfiguration.ReadDesktopClientConfiguration(idOnlyPath).ClientSecret);

        var webPath = Path.Combine(directory, "web-client.json");
        File.WriteAllText(webPath, """{"web":{"client_id":"123456789-test.apps.googleusercontent.com"}}""");
        try
        {
            _ = OAuthClientIdConfiguration.ReadDesktopClientId(webPath);
            throw new InvalidOperationException("Expected web OAuth configuration to be rejected.");
        }
        catch (OAuthClientConfigurationException exception)
        {
            Equal(OAuthClientConfigurationError.NotDesktopClient, exception.Error);
        }
    }
    finally
    {
        Directory.Delete(directory, recursive: true);
    }
}

static void OAuthClientIdsAreValidated()
{
    True(OAuthClientIdConfiguration.IsValid("123456789-test.apps.googleusercontent.com"));
    False(OAuthClientIdConfiguration.IsValid("client-secret"));
    False(OAuthClientIdConfiguration.IsValid("123456789-test.web.app"));
}

static void OAuthSecretRequirementIsDetectedSafely()
{
    const string missingSecret = """{"error":"invalid_request","error_description":"Missing required parameter: client_secret"}""";
    True(RemoteErrorDiagnostics.RequiresOAuthClientSecret(missingSecret));
    Equal("invalid_request", RemoteErrorDiagnostics.ParseOAuthErrorCode(missingSecret));
    False(RemoteErrorDiagnostics.RequiresOAuthClientSecret("""{"error":"invalid_request","error_description":"invalid redirect_uri"}"""));
    False(RemoteErrorDiagnostics.RequiresOAuthClientSecret("""{"error":"invalid_grant","error_description":"client_secret is invalid"}"""));
    False(RemoteErrorDiagnostics.RequiresOAuthClientSecret("not json"));
}

static async Task CredentialStoreProtectsAndLoadsTokens()
{
    var directory = CreateTestDirectory();
    try
    {
        const string refreshToken = "test-refresh-token-not-a-real-credential";
        var path = Path.Combine(directory, "token.dat");
        var store = new CredentialStore(path);
        store.SaveRefreshToken(refreshToken);
        True(store.HasRefreshToken);
        False(Encoding.UTF8.GetString(await File.ReadAllBytesAsync(path)).Contains(refreshToken, StringComparison.Ordinal));
        Equal(refreshToken, store.ReadRefreshToken());
        store.Delete();
        False(store.HasRefreshToken);
        Equal<string?>(null, store.ReadRefreshToken());
    }
    finally
    {
        Directory.Delete(directory, recursive: true);
    }
}

static async Task ClientSecretStoreProtectsAndScopesSecretToClientId()
{
    var directory = CreateTestDirectory();
    try
    {
        const string clientId = "123456789-test.apps.googleusercontent.com";
        const string secret = "test-secret-not-a-real-credential";
        var path = Path.Combine(directory, "client-secret.dat");
        var store = new OAuthClientSecretStore(path);
        store.SaveClientSecret(clientId, secret);
        True(store.HasClientSecret);
        False(Encoding.UTF8.GetString(await File.ReadAllBytesAsync(path)).Contains(secret, StringComparison.Ordinal));
        Equal(secret, store.ReadClientSecret(clientId));
        Equal<string?>(null, store.ReadClientSecret("987654321-other.apps.googleusercontent.com"));
        store.Delete();
        False(store.HasClientSecret);
        Equal<string?>(null, store.ReadClientSecret(clientId));
    }
    finally
    {
        Directory.Delete(directory, recursive: true);
    }
}

static async Task EncryptedCacheStoreRoundTripsAndReplacesSnapshots()
{
    var directory = CreateTestDirectory();
    try
    {
        var path = Path.Combine(directory, "cache.dat");
        var store = new EncryptedCacheStore(path);
        var updated = new DateTimeOffset(2026, 10, 2, 12, 30, 0, TimeSpan.Zero);
        var task = new TaskRecord("task-id", "Task 🔐", "needsAction", new DateOnly(2026, 10, 3), null,
            "position", "list-id", new string('n', 20_000), updated, null, "https://tasks.google.com/task-id");
        var completedTask = task with { Status = "completed", Completed = updated.AddMinutes(1) };
        var snapshot = new CachedSnapshot(updated, [new TaskListRecord("list-id", "Tasks")],
            new Dictionary<string, List<TaskRecord>>(StringComparer.Ordinal) { ["list-id"] = [task] },
            "client-id",
            new Dictionary<string, List<TaskRecord>>(StringComparer.Ordinal) { ["list-id"] = [completedTask] },
            "credential-fingerprint");

        await store.SaveAsync(snapshot);
        var replacement = snapshot with { SavedAt = updated.AddMinutes(2) };
        await store.SaveAsync(replacement);

        False(File.Exists(path + ".tmp"));
        var loaded = await store.LoadAsync();
        True(loaded is not null);
        Equal(replacement.SavedAt, loaded!.SavedAt);
        Equal("client-id", loaded.OAuthClientId);
        Equal("credential-fingerprint", loaded.OAuthCredentialFingerprint);
        Equal(task, loaded.TasksByList["list-id"][0]);
        Equal(completedTask, loaded.CompletedTasksByList!["list-id"][0]);
        Equal(task.Notes, loaded.TasksByList["list-id"][0].Notes);
    }
    finally
    {
        Directory.Delete(directory, recursive: true);
    }
}

static void SettingsStorePersistsOAuthClientId()
{
    var directory = CreateTestDirectory();
    try
    {
        var path = Path.Combine(directory, "settings.json");
        var settings = new AppSettings
        {
            OAuthClientId = "123456789-test.apps.googleusercontent.com",
            IsCompletedSectionExpanded = false,
            WidthDip = 612,
            HeightDip = 777,
            SortOrder = TaskSortOrder.Title
        };
        new SettingsStore(path).Save(settings);
        var serialized = File.ReadAllText(path);
        False(serialized.Contains("client_secret", StringComparison.OrdinalIgnoreCase));
        False(serialized.Contains("must-not-be-used", StringComparison.Ordinal));
        var loaded = new SettingsStore(path).Load();
        Equal(settings.OAuthClientId, loaded.OAuthClientId);
        Equal(settings.IsCompletedSectionExpanded, loaded.IsCompletedSectionExpanded);
        Equal(settings.WidthDip, loaded.WidthDip);
        Equal(settings.HeightDip, loaded.HeightDip);
        Equal(settings.SortOrder, loaded.SortOrder);
    }
    finally
    {
        Directory.Delete(directory, recursive: true);
    }
}

static void SettingsStoreMigratesSystemThemeToDark()
{
    var directory = CreateTestDirectory();
    try
    {
        var path = Path.Combine(directory, "settings.json");
        File.WriteAllText(path, """
            {"schemaVersion":1,"theme":"System","widthDip":612,"heightDip":777}
            """);
        var store = new SettingsStore(path);
        var migrated = store.Load();
        Equal(WidgetTheme.Light, migrated.Theme);
        Equal(4, migrated.SchemaVersion);
        Equal(TaskSortOrder.DueDate, migrated.SortOrder);
        Equal(612d, migrated.WidthDip);
        Equal(777d, migrated.HeightDip);

        store.Save(migrated);
        Equal(WidgetTheme.Light, store.Load().Theme);

        File.WriteAllText(path, """
            {"schemaVersion":1,"theme":"Light"}
            """);
        Equal(WidgetTheme.Light, store.Load().Theme);

        File.WriteAllText(path, """
            {"schemaVersion":1,"theme":"Dark"}
            """);
        Equal(WidgetTheme.Light, store.Load().Theme);

        var newInstall = new SettingsStore(Path.Combine(directory, "new-install.json")).Load();
        Equal(WidgetTheme.Light, newInstall.Theme);

        File.WriteAllText(path, """
            {"schemaVersion":3,"theme":"Light","sortOrder":"Title"}
            """);
        var migratedV3 = store.Load();
        Equal(4, migratedV3.SchemaVersion);
        Equal(TaskSortOrder.Title, migratedV3.SortOrder);
    }
    finally
    {
        Directory.Delete(directory, recursive: true);
    }
}

static void PortablePathsKeepAllDataTogether()
{
    var directory = CreateTestDirectory();
    try
    {
        var executableDirectory = Path.Combine(directory, "Widget");
        var localAppDataDirectory = Path.Combine(directory, "LocalAppData");
        var paths = new AppDataPaths(executableDirectory, localAppDataDirectory);

        Equal(Path.Combine(executableDirectory, "Data"), paths.DataDirectory);
        Equal(Path.Combine(paths.DataDirectory, "settings.json"), paths.SettingsFilePath);
        Equal(Path.Combine(paths.DataDirectory, "token.dat"), paths.TokenFilePath);
        Equal(Path.Combine(paths.DataDirectory, "client-secret.dat"), paths.ClientSecretFilePath);
        Equal(Path.Combine(paths.DataDirectory, "cache.dat"), paths.CacheFilePath);
        Equal(Path.Combine(paths.DataDirectory, "logs"), paths.LogsDirectory);
        Equal(Path.Combine(paths.LogsDirectory, "widget.log"), paths.LogFilePath);
        Equal(Path.Combine(localAppDataDirectory, "GoogleTasksDesktopWidget"), paths.LegacyDataDirectory);
    }
    finally
    {
        Directory.Delete(directory, recursive: true);
    }
}

static void LegacyMigrationCopiesDataAndRetainsSource()
{
    var directory = CreateTestDirectory();
    try
    {
        var paths = new AppDataPaths(Path.Combine(directory, "Widget"), Path.Combine(directory, "LocalAppData"));
        var legacyLogs = paths.GetLegacyLogsDirectory();
        Directory.CreateDirectory(legacyLogs);
        var files = new Dictionary<string, byte[]>
        {
            ["settings.json"] = Encoding.UTF8.GetBytes("{\"schemaVersion\":4}"),
            ["token.dat"] = [1, 2, 3, 4],
            ["client-secret.dat"] = [5, 6, 7],
            ["cache.dat"] = [8, 9, 10, 11],
            [Path.Combine("logs", "widget.log")] = Encoding.UTF8.GetBytes("old log\n"),
            [Path.Combine("logs", "widget.log.1")] = Encoding.UTF8.GetBytes("older log\n")
        };

        foreach (var (relativePath, bytes) in files)
        {
            var sourcePath = Path.Combine(paths.LegacyDataDirectory, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(sourcePath)!);
            File.WriteAllBytes(sourcePath, bytes);
        }

        var result = new PortableDataMigrator(paths).Migrate();

        True(result.HasLegacyData);
        Equal(files.Count, result.CopiedFiles.Count);
        Equal(0, result.ConflictingFiles.Count);
        True(File.Exists(paths.MigrationMarkerFilePath));
        foreach (var (relativePath, bytes) in files)
        {
            var sourcePath = Path.Combine(paths.LegacyDataDirectory, relativePath);
            var destinationPath = Path.Combine(paths.DataDirectory, relativePath);
            True(File.Exists(sourcePath));
            True(File.ReadAllBytes(destinationPath).SequenceEqual(bytes));
        }

        Equal(0, Directory.GetFiles(paths.DataDirectory, "*.migration-*.tmp", SearchOption.AllDirectories).Length);
        Equal(0, Directory.GetFiles(paths.DataDirectory, ".write-check-*.tmp").Length);
    }
    finally
    {
        Directory.Delete(directory, recursive: true);
    }
}

static void ExistingPortableFilesWinMigrationConflicts()
{
    var directory = CreateTestDirectory();
    try
    {
        var paths = new AppDataPaths(Path.Combine(directory, "Widget"), Path.Combine(directory, "LocalAppData"));
        Directory.CreateDirectory(paths.LegacyDataDirectory);
        Directory.CreateDirectory(paths.DataDirectory);
        var legacySettings = Encoding.UTF8.GetBytes("legacy settings");
        var legacyToken = Encoding.UTF8.GetBytes("legacy token");
        var portableToken = Encoding.UTF8.GetBytes("portable token");
        File.WriteAllBytes(paths.GetLegacyFilePath("settings.json"), legacySettings);
        File.WriteAllBytes(paths.GetLegacyFilePath("token.dat"), legacyToken);
        File.WriteAllBytes(paths.TokenFilePath, portableToken);

        var firstRun = new PortableDataMigrator(paths).Migrate();
        True(firstRun.HasLegacyData);
        True(firstRun.ConflictingFiles.Contains("token.dat"));
        True(firstRun.CopiedFiles.Contains("settings.json"));
        True(File.ReadAllBytes(paths.TokenFilePath).SequenceEqual(portableToken));
        True(File.ReadAllBytes(paths.GetLegacyFilePath("token.dat")).SequenceEqual(legacyToken));

        var updatedPortableSettings = Encoding.UTF8.GetBytes("settings changed by the user");
        File.WriteAllBytes(paths.SettingsFilePath, updatedPortableSettings);
        var laterRun = new PortableDataMigrator(paths).Migrate();
        True(laterRun.HasLegacyData);
        Equal(0, laterRun.ConflictingFiles.Count);
        Equal(0, laterRun.CopiedFiles.Count);
        True(File.ReadAllBytes(paths.SettingsFilePath).SequenceEqual(updatedPortableSettings));
        True(File.ReadAllBytes(paths.GetLegacyFilePath("settings.json")).SequenceEqual(legacySettings));
    }
    finally
    {
        Directory.Delete(directory, recursive: true);
    }
}

static void PortableAutostartDefaultsAndLegacySettingsPersist()
{
    var directory = CreateTestDirectory();
    try
    {
        False(new AppSettings().StartWithWindows);
        var newInstallPath = Path.Combine(directory, "new", "settings.json");
        False(new SettingsStore(newInstallPath).Load().StartWithWindows);

        var settingsPath = Path.Combine(directory, "legacy", "settings.json");
        Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
        File.WriteAllText(settingsPath, "{\"schemaVersion\":4,\"theme\":\"Light\"}");
        True(new SettingsStore(settingsPath).Load().StartWithWindows);

        File.WriteAllText(settingsPath, "{\"schemaVersion\":4,\"startWithWindows\":false}");
        False(new SettingsStore(settingsPath).Load().StartWithWindows);
        File.WriteAllText(settingsPath, "{\"schemaVersion\":4,\"startWithWindows\":true}");
        True(new SettingsStore(settingsPath).Load().StartWithWindows);
    }
    finally
    {
        Directory.Delete(directory, recursive: true);
    }
}

static void LegacyDataAfterEmptyFirstRunMigrates()
{
    var directory = CreateTestDirectory();
    try
    {
        var paths = new AppDataPaths(Path.Combine(directory, "Widget"), Path.Combine(directory, "LocalAppData"));
        var firstRun = new PortableDataMigrator(paths).Migrate();
        False(firstRun.HasLegacyData);

        Directory.CreateDirectory(paths.LegacyDataDirectory);
        File.WriteAllText(paths.GetLegacyFilePath("settings.json"), "legacy settings");
        var secondRun = new PortableDataMigrator(paths).Migrate();
        True(secondRun.HasLegacyData);
        True(secondRun.CopiedFiles.Contains("settings.json"));
        Equal("legacy settings", File.ReadAllText(paths.SettingsFilePath));
        True(File.Exists(paths.GetLegacyFilePath("settings.json")));
    }
    finally
    {
        Directory.Delete(directory, recursive: true);
    }
}

static void InvalidSettingsRemainAvailableForRecovery()
{
    var directory = CreateTestDirectory();
    try
    {
        var path = Path.Combine(directory, "settings.json");
        const string corruptContents = "{ this is not valid JSON";
        File.WriteAllText(path, corruptContents);

        var store = new SettingsStore(path);
        var recovered = store.Load();
        False(recovered.StartWithWindows);
        True(store.LastRecoveryBackupPath is not null);
        True(File.ReadAllText(path).Contains("schemaVersion", StringComparison.OrdinalIgnoreCase));
        var backups = Directory.GetFiles(directory, "settings.json.corrupt-*");
        Equal(1, backups.Length);
        Equal(corruptContents, File.ReadAllText(backups[0]));
    }
    finally
    {
        Directory.Delete(directory, recursive: true);
    }
}

static async Task ClientIdSettingsFailurePreservesOldCredentialsAndCache()
{
    var directory = CreateTestDirectory();
    try
    {
        var settingsPath = Path.Combine(directory, "settings.json");
        Directory.CreateDirectory(settingsPath); // Make the final replace fail after the temp file is written.
        var settings = new AppSettings { OAuthClientId = "123456789-old.apps.googleusercontent.com" };
        var settingsStore = new SettingsStore(settingsPath);
        var tokenPath = Path.Combine(directory, "token.dat");
        var credentials = new CredentialStore(tokenPath);
        credentials.SaveRefreshToken("test-old-refresh-token");
        var cachePath = Path.Combine(directory, "cache.dat");
        var cache = new EncryptedCacheStore(cachePath);
        await cache.SaveAsync(new CachedSnapshot(DateTimeOffset.UtcNow, [], new(), settings.OAuthClientId));
        var clientSecretStore = new OAuthClientSecretStore(Path.Combine(directory, "client-secret.dat"));
        var oauth = new OAuthService(credentials, settings, settingsStore, clientSecretStore);
        var viewModel = new MainViewModel(settings, settingsStore, credentials, oauth,
            new GoogleTasksClient(oauth), cache, new AutoStartManager(), clientSecretStore);

        viewModel.ClientIdInput = "123456789-new.apps.googleusercontent.com";
        await viewModel.SaveClientIdCommand.ExecuteAsync();

        Equal("123456789-old.apps.googleusercontent.com", settings.OAuthClientId);
        True(credentials.HasRefreshToken);
        True(File.Exists(cachePath));
        True(oauth.HasCredentials);
    }
    finally
    {
        Directory.Delete(directory, recursive: true);
    }
}

static async Task ClientIdCleanupFailureRequiresReauthorization()
{
    var directory = CreateTestDirectory();
    try
    {
        var settingsPath = Path.Combine(directory, "settings.json");
        var settings = new AppSettings { OAuthClientId = "123456789-old.apps.googleusercontent.com" };
        var settingsStore = new SettingsStore(settingsPath);
        settingsStore.Save(settings);
        var tokenPath = Path.Combine(directory, "token.dat");
        var credentials = new CredentialStore(tokenPath);
        credentials.SaveRefreshToken("test-old-refresh-token");
        var cachePath = Path.Combine(directory, "cache.dat");
        var cache = new EncryptedCacheStore(cachePath);
        await cache.SaveAsync(new CachedSnapshot(DateTimeOffset.UtcNow, [], new(), settings.OAuthClientId));
        var clientSecretStore = new OAuthClientSecretStore(Path.Combine(directory, "client-secret.dat"));
        var oauth = new OAuthService(credentials, settings, settingsStore, clientSecretStore);
        var viewModel = new MainViewModel(settings, settingsStore, credentials, oauth,
            new GoogleTasksClient(oauth), cache, new AutoStartManager(), clientSecretStore);

        viewModel.ClientIdInput = "123456789-new.apps.googleusercontent.com";
        using (var tokenLock = new FileStream(tokenPath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            await viewModel.SaveClientIdCommand.ExecuteAsync();
            Equal("123456789-new.apps.googleusercontent.com", settings.OAuthClientId);
            Equal(settings.OAuthClientId, settingsStore.Load().OAuthClientId);
            True(settings.OAuthReauthorizationRequired);
            True(settingsStore.Load().OAuthReauthorizationRequired);
            True(credentials.HasRefreshToken);
            False(oauth.HasCredentials);
            False(File.Exists(cachePath));
            try
            {
                _ = await oauth.GetAccessTokenAsync();
                throw new InvalidOperationException("The old token must not be used after cleanup failure.");
            }
            catch (OAuthFlowException exception)
            {
                Equal("reauthorization_required", exception.Stage);
            }
        }

        var persistedSettings = settingsStore.Load();
        var restartedOAuth = new OAuthService(credentials, persistedSettings, settingsStore);
        False(restartedOAuth.HasCredentials);
        try
        {
            _ = await restartedOAuth.GetAccessTokenAsync();
            throw new InvalidOperationException("A restart must not make the old client token usable.");
        }
        catch (OAuthFlowException exception)
        {
            Equal("reauthorization_required", exception.Stage);
        }
    }
    finally
    {
        Directory.Delete(directory, recursive: true);
    }
}

static async Task ClientSecretSaveFailureRollsBackConfigurationAndKeepsTheOldConnection()
{
    var directory = CreateTestDirectory();
    try
    {
        var settingsPath = Path.Combine(directory, "settings.json");
        var settings = new AppSettings { OAuthClientId = "123456789-old.apps.googleusercontent.com" };
        var settingsStore = new SettingsStore(settingsPath);
        settingsStore.Save(settings);

        var credentials = new CredentialStore(Path.Combine(directory, "token.dat"));
        credentials.SaveRefreshToken("test-old-refresh-token");
        var cachePath = Path.Combine(directory, "cache.dat");
        var cache = new EncryptedCacheStore(cachePath);
        await cache.SaveAsync(new CachedSnapshot(DateTimeOffset.UtcNow, [], new(), settings.OAuthClientId));

        var secretPath = Path.Combine(directory, "client-secret.dat");
        Directory.CreateDirectory(secretPath); // Atomic replacement of this path will fail.
        var clientSecretStore = new OAuthClientSecretStore(secretPath);
        var oauth = new OAuthService(credentials, settings, settingsStore, clientSecretStore);
        var viewModel = new MainViewModel(settings, settingsStore, credentials, oauth,
            new GoogleTasksClient(oauth), cache, new AutoStartManager(), clientSecretStore);

        viewModel.ClientIdInput = "123456789-new.apps.googleusercontent.com";
        viewModel.ClientSecretInput = "test-new-client-secret";
        await viewModel.SaveClientIdCommand.ExecuteAsync();

        Equal("123456789-old.apps.googleusercontent.com", settings.OAuthClientId);
        Equal(settings.OAuthClientId, settingsStore.Load().OAuthClientId);
        False(settings.OAuthReauthorizationRequired);
        True(credentials.HasRefreshToken);
        True(File.Exists(cachePath));
        True(oauth.HasCredentials);
        True(viewModel.ShowClientIdSetupPanel);
        Equal(UiText.Get("OAuthClientSecretSaveFailed"), viewModel.ClientIdSetupMessage);
    }
    finally
    {
        Directory.Delete(directory, recursive: true);
    }
}

static string CreateTestDirectory()
{
    var directory = Path.Combine(Path.GetTempPath(), "GoogleTasksDesktopWidget.CoreTests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    return directory;
}

static async Task LoopbackCallbackIgnoresInvalidConnections()
{
    using var listener = new TcpListener(IPAddress.Loopback, 0);
    listener.Start();
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    var port = ((IPEndPoint)listener.LocalEndpoint).Port;
    var callbackTask = LoopbackOAuthCallbackListener.WaitForAuthorizationCodeAsync(listener, "expected-state", timeout.Token);

    var malformedResponse = await SendLoopbackRequestAsync(port, "not an HTTP request\r\n\r\n", timeout.Token);
    True(malformedResponse.StartsWith("HTTP/1.1 400 Bad Request", StringComparison.Ordinal));

    var wrongStateResponse = await SendLoopbackRequestAsync(port,
        "GET /?code=attacker&state=wrong-state HTTP/1.1\r\nHost: 127.0.0.1\r\n\r\n", timeout.Token);
    True(wrongStateResponse.StartsWith("HTTP/1.1 400 Bad Request", StringComparison.Ordinal));

    var validResponse = await SendLoopbackRequestAsync(port,
        "GET /?code=valid-code&state=expected-state HTTP/1.1\r\nHost: 127.0.0.1\r\n\r\n", timeout.Token);
    True(validResponse.StartsWith("HTTP/1.1 200 OK", StringComparison.Ordinal));
    True(validResponse.Contains("認証コードを受信しました", StringComparison.Ordinal));
    False(validResponse.Contains("認証が完了しました", StringComparison.Ordinal));
    Equal("valid-code", await callbackTask.WaitAsync(timeout.Token));
}

static async Task<string> SendLoopbackRequestAsync(int port, string request, CancellationToken cancellationToken)
{
    using var client = new TcpClient();
    await client.ConnectAsync(IPAddress.Loopback, port, cancellationToken);
    var stream = client.GetStream();
    await stream.WriteAsync(Encoding.ASCII.GetBytes(request), cancellationToken);
    using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
    return await reader.ReadToEndAsync(cancellationToken);
}

static TaskRecord Task(string id, DateOnly? due, string? parent = null, string position = "") =>
    new(id, id, "needsAction", due, parent, position);

static void Equal<T>(T expected, T actual)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
    {
        throw new InvalidOperationException($"Expected '{expected}', got '{actual}'.");
    }
}

static void True(bool value)
{
    if (!value) throw new InvalidOperationException("Expected true.");
}

static void False(bool value)
{
    if (value) throw new InvalidOperationException("Expected false.");
}
