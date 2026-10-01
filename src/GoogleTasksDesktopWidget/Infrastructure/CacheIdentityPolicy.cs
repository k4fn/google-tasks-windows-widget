using System.Security.Cryptography;
using System.Text;

namespace GoogleTasksDesktopWidget.Infrastructure;

public static class CacheIdentityPolicy
{
    public static string? FingerprintRefreshToken(string? refreshToken)
    {
        if (string.IsNullOrWhiteSpace(refreshToken)) return null;
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(refreshToken)));
    }

    public static bool MatchesOwner(
        string? cachedOAuthClientId,
        string? currentOAuthClientId,
        string? cachedCredentialFingerprint,
        string? currentCredentialFingerprint) =>
        !string.IsNullOrWhiteSpace(currentOAuthClientId) &&
        !string.IsNullOrWhiteSpace(currentCredentialFingerprint) &&
        string.Equals(cachedOAuthClientId, currentOAuthClientId, StringComparison.Ordinal) &&
        string.Equals(cachedCredentialFingerprint, currentCredentialFingerprint, StringComparison.Ordinal);
}
