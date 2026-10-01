using System.Text.Json;

namespace GoogleTasksDesktopWidget.Core;

/// <summary>
/// Extracts only machine-readable error identifiers from remote service responses.
/// Response messages and payloads can contain user data and must never be logged.
/// </summary>
public static class RemoteErrorDiagnostics
{
    private static readonly HashSet<string> OAuthErrorCodes = new(StringComparer.Ordinal)
    {
        "access_denied", "invalid_client", "invalid_grant", "invalid_request", "invalid_scope",
        "unauthorized_client", "unsupported_grant_type", "unsupported_response_type"
    };

    private static readonly HashSet<string> GoogleApiReasonCodes = new(StringComparer.Ordinal)
    {
        "accessNotConfigured", "authError", "backendError", "dailyLimitExceeded", "dailyLimitExceededUnreg",
        "domainPolicy", "failedPrecondition", "forbidden", "insufficientPermissions", "internalError",
        "invalid", "invalidParameter", "keyInvalid", "notFound", "quotaExceeded", "rateLimitExceeded",
        "serviceDisabled", "userRateLimitExceeded", "PERMISSION_DENIED", "UNAUTHENTICATED",
        "RESOURCE_EXHAUSTED", "INVALID_ARGUMENT", "NOT_FOUND", "FAILED_PRECONDITION", "INTERNAL",
        "UNAVAILABLE", "DEADLINE_EXCEEDED"
    };

    public static string? ParseOAuthErrorCode(string? responseBody)
    {
        if (string.IsNullOrWhiteSpace(responseBody)) return null;
        try
        {
            using var document = JsonDocument.Parse(responseBody);
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("error", out var error) || error.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            return Classify(error.GetString(), OAuthErrorCodes);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Detects Google's missing-client-secret response without retaining or exposing its description.
    /// The response description is never returned or written to logs.
    /// </summary>
    public static bool RequiresOAuthClientSecret(string? responseBody)
    {
        if (string.IsNullOrWhiteSpace(responseBody)) return false;
        try
        {
            using var document = JsonDocument.Parse(responseBody);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("error", out var error) || error.ValueKind != JsonValueKind.String ||
                !string.Equals(error.GetString(), "invalid_request", StringComparison.Ordinal) ||
                !root.TryGetProperty("error_description", out var description) || description.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            return description.GetString()?.Contains("client_secret", StringComparison.OrdinalIgnoreCase) == true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public static string? ParseGoogleApiReason(string? responseBody)
    {
        if (string.IsNullOrWhiteSpace(responseBody)) return null;
        try
        {
            using var document = JsonDocument.Parse(responseBody);
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("error", out var error) || error.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            if (error.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in errors.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.Object && item.TryGetProperty("reason", out var reason) && reason.ValueKind == JsonValueKind.String)
                    {
                        var safeReason = Classify(reason.GetString(), GoogleApiReasonCodes);
                        if (safeReason is not null) return safeReason;
                    }
                }
            }

            if (error.TryGetProperty("status", out var status) && status.ValueKind == JsonValueKind.String)
            {
                return Classify(status.GetString(), GoogleApiReasonCodes);
            }
        }
        catch (JsonException)
        {
            // Diagnostics are best effort; never replace the actual request error.
        }

        return null;
    }

    private static string? Classify(string? value, HashSet<string> allowedCodes)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        return allowedCodes.Contains(value) ? value : "other";
    }
}
