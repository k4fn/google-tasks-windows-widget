using System.Text.Json;
using System.Text.RegularExpressions;

namespace GoogleTasksDesktopWidget.Infrastructure;

public enum OAuthClientConfigurationError
{
    InvalidJson,
    NotDesktopClient,
    MissingClientId,
    InvalidClientId,
    InvalidClientSecret
}

public sealed class OAuthClientConfigurationException(OAuthClientConfigurationError error)
    : Exception("The OAuth client configuration is invalid.")
{
    public OAuthClientConfigurationError Error { get; } = error;
}

public static partial class OAuthClientIdConfiguration
{
    private const int MaximumClientSecretLength = 4096;

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]*\\.apps\\.googleusercontent\\.com$", RegexOptions.CultureInvariant)]
    private static partial Regex ClientIdPattern();

    public static bool IsValid(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 256 && ClientIdPattern().IsMatch(value);

    public static bool IsValidClientSecret(string? value) =>
        string.IsNullOrEmpty(value) ||
        value.Length <= MaximumClientSecretLength && !value.Any(char.IsControl);

    public static OAuthClientConfiguration ReadDesktopClientConfiguration(string path)
    {
        JsonDocument document;
        try
        {
            using var stream = File.OpenRead(path);
            document = JsonDocument.Parse(stream);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            throw new OAuthClientConfigurationException(OAuthClientConfigurationError.InvalidJson);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("installed", out var installed) ||
                installed.ValueKind != JsonValueKind.Object)
            {
                var error = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("web", out _)
                    ? OAuthClientConfigurationError.NotDesktopClient
                    : OAuthClientConfigurationError.MissingClientId;
                throw new OAuthClientConfigurationException(error);
            }

            if (!installed.TryGetProperty("client_id", out var clientIdElement) || clientIdElement.ValueKind != JsonValueKind.String)
            {
                throw new OAuthClientConfigurationException(OAuthClientConfigurationError.MissingClientId);
            }

            var clientId = clientIdElement.GetString();
            if (!IsValid(clientId)) throw new OAuthClientConfigurationException(OAuthClientConfigurationError.InvalidClientId);

            string? clientSecret = null;
            if (installed.TryGetProperty("client_secret", out var clientSecretElement))
            {
                if (clientSecretElement.ValueKind != JsonValueKind.String)
                    throw new OAuthClientConfigurationException(OAuthClientConfigurationError.InvalidClientSecret);
                clientSecret = clientSecretElement.GetString();
                if (string.IsNullOrWhiteSpace(clientSecret)) clientSecret = null;
                if (!IsValidClientSecret(clientSecret))
                    throw new OAuthClientConfigurationException(OAuthClientConfigurationError.InvalidClientSecret);
            }

            return new OAuthClientConfiguration(clientId!, clientSecret);
        }
    }

    public static string ReadDesktopClientId(string path)
    {
        return ReadDesktopClientConfiguration(path).ClientId;
    }
}

/// <summary>OAuth client credentials held in memory only; never serialize or log this object.</summary>
public sealed class OAuthClientConfiguration(string clientId, string? clientSecret)
{
    public string ClientId { get; } = clientId;
    public string? ClientSecret { get; } = clientSecret;
}
