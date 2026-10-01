using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using GoogleTasksDesktopWidget.Core;

namespace GoogleTasksDesktopWidget.Infrastructure;

public sealed class OAuthService(
    CredentialStore credentialStore,
    AppSettings settings,
    SettingsStore? settingsStore = null,
    OAuthClientSecretStore? clientSecretStore = null)
{
    private const string Scope = "https://www.googleapis.com/auth/tasks";
    private const string TokenEndpoint = "https://oauth2.googleapis.com/token";
    private static readonly HttpClient Http = new();
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly OAuthClientSecretStore _clientSecretStore = clientSecretStore ?? new();
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private string? _accessToken;
    private DateTimeOffset _accessTokenExpiry;
    private bool _reauthorizationRequired = settings.OAuthReauthorizationRequired;

    public bool IsConfigured => OAuthClientIdConfiguration.IsValid(ClientId);
    public bool HasCredentials => IsConfigured && !IsReauthorizationRequired && credentialStore.HasRefreshToken;

    private bool IsReauthorizationRequired => _reauthorizationRequired || settings.OAuthReauthorizationRequired;

    private string? ClientId => settings.OAuthClientId;

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        var clientId = ClientId;
        if (string.IsNullOrWhiteSpace(clientId))
        {
            throw new OAuthConfigurationException();
        }

        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var redirectUri = $"http://127.0.0.1:{port}/";
        var state = PkceGenerator.Base64Url(RandomNumberGenerator.GetBytes(32));
        var pkce = PkceGenerator.Create();
        var parameters = new Dictionary<string, string>
        {
            ["client_id"] = clientId,
            ["redirect_uri"] = redirectUri,
            ["response_type"] = "code",
            ["scope"] = Scope,
            ["access_type"] = "offline",
            ["prompt"] = "consent",
            ["state"] = state,
            ["code_challenge"] = pkce.Challenge,
            ["code_challenge_method"] = "S256"
        };
        var authorizationUrl = "https://accounts.google.com/o/oauth2/v2/auth?" + FormUrlEncode(parameters);

        try
        {
            Process.Start(new ProcessStartInfo(authorizationUrl) { UseShellExecute = true });
        }
        catch
        {
            listener.Stop();
            throw new OAuthFlowException("browser_start");
        }

        string code;
        using var flowTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        flowTimeout.CancelAfter(TimeSpan.FromMinutes(5));
        try
        {
            code = await LoopbackOAuthCallbackListener.WaitForAuthorizationCodeAsync(listener, state, flowTimeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            AppLogger.WriteEvent("OAuth.callback_timeout");
            throw;
        }
        catch (OAuthCallbackRejectedException)
        {
            throw new OAuthFlowException("callback_denied");
        }
        catch (Exception exception)
        {
            AppLogger.WriteError("OAuth.callback", exception);
            throw new OAuthFlowException("callback_listener");
        }
        listener.Stop();
        AppLogger.WriteEvent("OAuth.callback_received");

        var response = await ExchangeCodeAsync(clientId, code, pkce.Verifier, redirectUri, cancellationToken).ConfigureAwait(false);
        SetAccessToken(response, "token_exchange_response");
        if (string.IsNullOrWhiteSpace(response.RefreshToken))
        {
            // Keep a previously stored token when Google does not return a second one.
            if (_reauthorizationRequired || !credentialStore.HasRefreshToken) throw new OAuthFlowException("refresh_token_missing");
        }
        else
        {
            try
            {
                credentialStore.SaveRefreshToken(response.RefreshToken);
                AppLogger.WriteEvent("OAuth.refresh_token_stored");
            }
            catch (Exception exception)
            {
                AppLogger.WriteError("OAuth.token_store", exception);
                throw new OAuthFlowException("token_store");
            }
        }

        var clearPersistedRequirement = settings.OAuthReauthorizationRequired;
        _reauthorizationRequired = false;
        settings.OAuthReauthorizationRequired = false;
        if (clearPersistedRequirement && settingsStore is not null)
        {
            try
            {
                settingsStore.Save(settings);
            }
            catch (Exception exception)
            {
                settings.OAuthReauthorizationRequired = true;
                RequireReauthorization();
                AppLogger.WriteError("OAuth.reauthorization_state_save", exception);
                throw new OAuthFlowException("reauthorization_state_save");
            }
        }
        AppLogger.WriteEvent("OAuth.connected");
    }

    public async Task<string> GetAccessTokenAsync(CancellationToken cancellationToken = default)
    {
        if (IsReauthorizationRequired) throw new OAuthFlowException("reauthorization_required");
        if (!string.IsNullOrWhiteSpace(_accessToken) && _accessTokenExpiry > DateTimeOffset.UtcNow.AddSeconds(60))
        {
            return _accessToken;
        }

        await RefreshAsync(cancellationToken).ConfigureAwait(false);
        return _accessToken!;
    }

    public async Task RefreshAfterUnauthorizedAsync(string rejectedAccessToken, CancellationToken cancellationToken = default)
    {
        await _refreshGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!string.Equals(_accessToken, rejectedAccessToken, StringComparison.Ordinal)) return;
            await RefreshUnderLockAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    public void SignOut()
    {
        try
        {
            credentialStore.Delete();
            _reauthorizationRequired = false;
            settings.OAuthReauthorizationRequired = false;
            PersistReauthorizationState();
        }
        catch
        {
            _reauthorizationRequired = true;
            settings.OAuthReauthorizationRequired = true;
            PersistReauthorizationState();
            throw;
        }
        finally
        {
            _accessToken = null;
            _accessTokenExpiry = DateTimeOffset.MinValue;
        }
    }

    public void ClearAccessTokenForClientChange()
    {
        _accessToken = null;
        _accessTokenExpiry = DateTimeOffset.MinValue;
        _reauthorizationRequired = true;
        settings.OAuthReauthorizationRequired = true;
    }

    public void RequireReauthorization()
    {
        _reauthorizationRequired = true;
        settings.OAuthReauthorizationRequired = true;
        _accessToken = null;
        _accessTokenExpiry = DateTimeOffset.MinValue;
        PersistReauthorizationState();
        try { credentialStore.Delete(); }
        catch (Exception exception) { AppLogger.WriteError("OAuth.reauthorization_cleanup", exception); }
    }

    public async Task RevokeAsync(CancellationToken cancellationToken = default)
    {
        var refreshToken = credentialStore.ReadRefreshToken();
        if (string.IsNullOrWhiteSpace(refreshToken)) return;

        using var response = await Http.PostAsync("https://oauth2.googleapis.com/revoke",
            new FormUrlEncodedContent(new Dictionary<string, string> { ["token"] = refreshToken }),
            cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode && response.StatusCode != HttpStatusCode.BadRequest)
        {
            throw new OAuthFlowException("token_revoke", response.StatusCode);
        }
    }

    private async Task RefreshAsync(CancellationToken cancellationToken)
    {
        await _refreshGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!string.IsNullOrWhiteSpace(_accessToken) && _accessTokenExpiry > DateTimeOffset.UtcNow.AddSeconds(60)) return;
            await RefreshUnderLockAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    private async Task RefreshUnderLockAsync(CancellationToken cancellationToken)
    {
        if (_reauthorizationRequired) throw new OAuthFlowException("reauthorization_required");
        var refreshToken = credentialStore.ReadRefreshToken();
        var clientId = ClientId;
        if (string.IsNullOrWhiteSpace(refreshToken) || string.IsNullOrWhiteSpace(clientId))
        {
            throw new OAuthFlowException("refresh_token_unavailable");
        }

        var values = new Dictionary<string, string>
        {
            ["client_id"] = clientId,
            ["refresh_token"] = refreshToken,
            ["grant_type"] = "refresh_token"
        };
        AddClientSecretIfAvailable(values);
        using var response = await PostTokenRequestAsync(values, "token_refresh", cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var errorCode = await ReadOAuthErrorCodeAsync(response, cancellationToken).ConfigureAwait(false);
            AppLogger.WriteRemoteFailure("OAuth.token_refresh", (int)response.StatusCode, errorCode);
            if (response.StatusCode == HttpStatusCode.Unauthorized || response.StatusCode == HttpStatusCode.BadRequest)
            {
                SignOut();
            }
            throw new OAuthFlowException("token_refresh", response.StatusCode, errorCode);
        }

        var token = await ReadTokenResponseAsync(response, "token_refresh_response", cancellationToken).ConfigureAwait(false);
        SetAccessToken(token, "token_refresh_response");
        if (!string.IsNullOrWhiteSpace(token.RefreshToken))
        {
            try { credentialStore.SaveRefreshToken(token.RefreshToken); }
            catch (Exception exception)
            {
                AppLogger.WriteError("OAuth.refresh_token_store", exception);
                throw new OAuthFlowException("refresh_token_store");
            }
        }
        AppLogger.WriteEvent("OAuth.token_refresh_succeeded");
    }

    private void PersistReauthorizationState()
    {
        if (settingsStore is null) return;
        try { settingsStore.Save(settings); }
        catch (Exception exception) { AppLogger.WriteError("OAuth.reauthorization_state", exception); }
    }

    private async Task<TokenResponse> ExchangeCodeAsync(
        string clientId,
        string code,
        string verifier,
        string redirectUri,
        CancellationToken cancellationToken)
    {
        var values = new Dictionary<string, string>
        {
            ["client_id"] = clientId,
            ["code"] = code,
            ["code_verifier"] = verifier,
            ["redirect_uri"] = redirectUri,
            ["grant_type"] = "authorization_code"
        };
        AddClientSecretIfAvailable(values);
        using var response = await PostTokenRequestAsync(values, "token_exchange", cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var errorCode = await ReadOAuthErrorCodeAsync(response, cancellationToken).ConfigureAwait(false);
            AppLogger.WriteRemoteFailure("OAuth.token_exchange", (int)response.StatusCode, errorCode);
            throw new OAuthFlowException("token_exchange", response.StatusCode, errorCode);
        }

        var token = await ReadTokenResponseAsync(response, "token_exchange_response", cancellationToken).ConfigureAwait(false);
        AppLogger.WriteEvent("OAuth.token_exchange_succeeded");
        return token;
    }

    private async Task<HttpResponseMessage> PostTokenRequestAsync(
        Dictionary<string, string> values,
        string stage,
        CancellationToken cancellationToken)
    {
        try
        {
            return await Http.PostAsync(TokenEndpoint, new FormUrlEncodedContent(values), cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException)
        {
            AppLogger.WriteEvent($"OAuth.{stage}.network");
            throw new OAuthFlowException($"{stage}_network");
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            AppLogger.WriteEvent($"OAuth.{stage}.timeout");
            throw new OAuthFlowException($"{stage}_timeout");
        }
    }

    private static async Task<TokenResponse> ReadTokenResponseAsync(HttpResponseMessage response, string stage, CancellationToken cancellationToken)
    {
        try
        {
            return await response.Content.ReadFromJsonAsync<TokenResponse>(Json, cancellationToken).ConfigureAwait(false)
                ?? throw new OAuthFlowException(stage);
        }
        catch (JsonException)
        {
            AppLogger.WriteRemoteFailure($"OAuth.{stage}", (int)response.StatusCode, "invalid_json");
            throw new OAuthFlowException(stage, response.StatusCode, "invalid_json");
        }
    }

    private static async Task<string?> ReadOAuthErrorCodeAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (RemoteErrorDiagnostics.RequiresOAuthClientSecret(body)) return "client_secret_required";
        return RemoteErrorDiagnostics.ParseOAuthErrorCode(body);
    }

    private void AddClientSecretIfAvailable(IDictionary<string, string> values)
    {
        var clientSecret = _clientSecretStore.ReadClientSecret(ClientId);
        if (!string.IsNullOrWhiteSpace(clientSecret)) values["client_secret"] = clientSecret;
    }

    private void SetAccessToken(TokenResponse response, string stage)
    {
        if (string.IsNullOrWhiteSpace(response.AccessToken)) throw new OAuthFlowException(stage, errorCode: "access_token_missing");
        _accessToken = response.AccessToken;
        _accessTokenExpiry = DateTimeOffset.UtcNow.AddSeconds(Math.Max(60, response.ExpiresIn));
    }

    private static string FormUrlEncode(IEnumerable<KeyValuePair<string, string>> values) =>
        string.Join('&', values.Select(pair => $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}"));

    private sealed class TokenResponse
    {
        [JsonPropertyName("access_token")] public string? AccessToken { get; set; }
        [JsonPropertyName("refresh_token")] public string? RefreshToken { get; set; }
        [JsonPropertyName("expires_in")] public int ExpiresIn { get; set; }
    }
}

public sealed class OAuthConfigurationException : Exception { }
public sealed class OAuthFlowException(string stage, HttpStatusCode? statusCode = null, string? errorCode = null)
    : Exception("Google OAuth request failed.")
{
    public string Stage { get; } = stage;
    public HttpStatusCode? StatusCode { get; } = statusCode;
    public string? ErrorCode { get; } = errorCode;
}
