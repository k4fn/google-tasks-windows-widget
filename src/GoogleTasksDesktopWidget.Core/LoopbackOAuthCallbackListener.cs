using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace GoogleTasksDesktopWidget.Core;

/// <summary>
/// Reads OAuth callbacks from an already-started loopback listener. Unrelated
/// connections are rejected and ignored so they cannot cancel an active login.
/// </summary>
public static class LoopbackOAuthCallbackListener
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ResponseTimeout = TimeSpan.FromSeconds(1);

    public static async Task<string> WaitForAuthorizationCodeAsync(
        TcpListener listener,
        string expectedState,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException(cancellationToken);
            }

            using (client)
            using (var requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                requestTimeout.CancelAfter(RequestTimeout);
                (string? Code, string? State, string? Error) callback;
                try
                {
                    callback = await ReadCallbackAsync(client, requestTimeout.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    await TryWriteResponseAsync(client, success: false, cancellationToken).ConfigureAwait(false);
                    continue;
                }
                catch (Exception exception) when (exception is IOException or SocketException or FormatException or ArgumentException)
                {
                    await TryWriteResponseAsync(client, success: false, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                if (string.IsNullOrWhiteSpace(callback.State) || !StatesMatch(callback.State, expectedState))
                {
                    await TryWriteResponseAsync(client, success: false, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(callback.Error))
                {
                    await TryWriteResponseAsync(client, success: false, cancellationToken).ConfigureAwait(false);
                    throw new OAuthCallbackRejectedException();
                }

                if (string.IsNullOrWhiteSpace(callback.Code))
                {
                    await TryWriteResponseAsync(client, success: false, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                // A disconnected browser must not discard a valid callback.
                await TryWriteResponseAsync(client, success: true, cancellationToken).ConfigureAwait(false);
                return callback.Code;
            }
        }
    }

    private static async Task<(string? Code, string? State, string? Error)> ReadCallbackAsync(
        TcpClient client,
        CancellationToken cancellationToken)
    {
        var stream = client.GetStream();
        using var reader = new StreamReader(stream, Encoding.ASCII, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
        var requestLine = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
        var headersLength = 0;
        string? header;
        while (!string.IsNullOrEmpty(header = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false)))
        {
            headersLength += header.Length;
            if (headersLength > 16 * 1024) return (null, null, "invalid_request");
        }

        if (string.IsNullOrWhiteSpace(requestLine)) return (null, null, "invalid_request");
        var parts = requestLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 3 || !string.Equals(parts[0], "GET", StringComparison.Ordinal) ||
            !parts[1].StartsWith("/", StringComparison.Ordinal) || parts[1].StartsWith("//", StringComparison.Ordinal) ||
            !parts[2].StartsWith("HTTP/", StringComparison.Ordinal) || parts[1].Length > 8192 ||
            !Uri.TryCreate("http://127.0.0.1" + parts[1], UriKind.Absolute, out var requestUri))
        {
            return (null, null, "invalid_request");
        }

        var query = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var component in requestUri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = component.Split('=', 2);
            var key = Uri.UnescapeDataString(pair[0].Replace('+', ' '));
            var value = pair.Length > 1 ? Uri.UnescapeDataString(pair[1].Replace('+', ' ')) : string.Empty;
            if (!query.TryAdd(key, value)) return (null, null, "invalid_request");
        }

        query.TryGetValue("code", out var code);
        query.TryGetValue("state", out var state);
        query.TryGetValue("error", out var error);
        return (code, state, error);
    }

    private static bool StatesMatch(string actual, string expected)
    {
        var actualBytes = Encoding.UTF8.GetBytes(actual);
        var expectedBytes = Encoding.UTF8.GetBytes(expected);
        return actualBytes.Length == expectedBytes.Length && CryptographicOperations.FixedTimeEquals(actualBytes, expectedBytes);
    }

    private static async Task TryWriteResponseAsync(TcpClient client, bool success, CancellationToken flowToken)
    {
        var body = success
            ? "<!doctype html><meta charset=utf-8><title>Google Tasks</title><p>認証コードを受信しました。アプリで確認しています。このタブは閉じてかまいません。</p>"
            : "<!doctype html><meta charset=utf-8><title>Google Tasks</title><p>認証を確認できませんでした。アプリに戻り、もう一度お試しください。</p>";
        var bytes = Encoding.UTF8.GetBytes(body);
        using var responseTimeout = CancellationTokenSource.CreateLinkedTokenSource(flowToken);
        responseTimeout.CancelAfter(ResponseTimeout);
        try
        {
            await using var stream = client.GetStream();
            var header = Encoding.ASCII.GetBytes(
                $"HTTP/1.1 {(success ? "200 OK" : "400 Bad Request")}\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(header, responseTimeout.Token).ConfigureAwait(false);
            await stream.WriteAsync(bytes, responseTimeout.Token).ConfigureAwait(false);
            await stream.FlushAsync(responseTimeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!flowToken.IsCancellationRequested)
        {
            // A stalled or disconnected local client should not block the login flow.
        }
        catch (Exception exception) when (exception is IOException or SocketException or ObjectDisposedException)
        {
            // A stalled or disconnected local client should not block the login flow.
        }
    }
}

public sealed class OAuthCallbackRejectedException : Exception { }
