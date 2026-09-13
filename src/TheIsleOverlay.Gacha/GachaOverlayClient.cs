using System.Net;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text;

namespace TheIsleOverlay.Gacha;

/// <summary>
/// Thin first-party client for player.isle.vn. It does not persist or
/// discover credentials. The host owns the credential lifecycle and can
/// dispose this client when the map closes.
/// </summary>
public sealed class GachaOverlayClient : IGachaOverlayClient
{
    private readonly HttpClient _httpClient;
    private GachaOverlayCredentials _credentials;
    private readonly GachaOverlayOptions _options;
    private readonly bool _ownsHttpClient;
    private readonly Func<GachaOverlayCredentials, CancellationToken, Task>? _credentialsRefreshed;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private string? _helloName;
    private int _disposed;

    public GachaOverlayClient(
        GachaOverlayCredentials credentials,
        GachaOverlayOptions? options = null,
        HttpClient? httpClient = null,
        Func<GachaOverlayCredentials, CancellationToken, Task>? credentialsRefreshed = null)
    {
        _credentials = credentials ?? throw new ArgumentNullException(nameof(credentials));
        _options = options ?? new GachaOverlayOptions();
        _options.Validate();
        _httpClient = httpClient ?? CreateOwnedHttpClient(_options.ApiTimeout);
        _ownsHttpClient = httpClient is null;
        _credentialsRefreshed = credentialsRefreshed;
    }

    public GachaOverlayCredentials Credentials => _credentials;

    public async Task<GachaOverlayMeDto> GetMeAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await EnsureAccessTokenFreshAsync(cancellationToken).ConfigureAwait(false);
        using var request = CreateRequest(HttpMethod.Get, new Uri(_options.BaseUri, "api/overlay/me"));
        using var response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken)
            .ConfigureAwait(false);
        EnsureTrustedResponse(response);
        ThrowIfAuthenticationFailed(response);
        response.EnsureSuccessStatusCode();
        var payload = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        if (!GachaOverlayDtoParser.TryParseMe(payload, out var me))
        {
            throw new GachaOverlayProtocolException("Gacha /me returned an invalid stats payload.");
        }

        _helloName = FirstNonEmpty(me.PersonaName, me.Name, _credentials.PersonaName);
        return me;
    }

    public async IAsyncEnumerable<GachaOverlayFrameDto> ReadLiveAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation]
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await EnsureAccessTokenFreshAsync(cancellationToken).ConfigureAwait(false);
        // A host may use the live feed without first calling /me. Resolve the
        // hello name through the same official endpoint instead of sending a
        // SteamID as a persona name or relying on another process' state.
        if (_helloName is null)
        {
            GachaOverlayMeDto me;
            try
            {
                me = await GetMeAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (GachaOverlayAuthenticationException)
            {
                throw;
            }
            catch
            {
                // The WebSocket can still be useful if /me is temporarily
                // unavailable; the optional credential persona is the safe
                // fallback for the hello message.
                me = new GachaOverlayMeDto();
            }

            _helloName ??= FirstNonEmpty(me.PersonaName, me.Name, _credentials.PersonaName);
        }

        using var socket = new ClientWebSocket();
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
        socket.Options.SetRequestHeader("Authorization", $"Bearer {_credentials.AccessToken}");
        var websocketUri = new UriBuilder(_options.BaseUri)
        {
            Scheme = Uri.UriSchemeHttps.Equals(_options.BaseUri.Scheme, StringComparison.OrdinalIgnoreCase)
                ? "wss"
                : "ws",
            Path = CombinePath(_options.BaseUri.AbsolutePath, "overlay")
        }.Uri;

        await socket.ConnectAsync(websocketUri, cancellationToken).ConfigureAwait(false);
        var hello = Encoding.UTF8.GetBytes(
            $"{{\"t\":\"hello\",\"name\":{JsonEscape(_helloName ?? _credentials.PersonaName ?? string.Empty)}}}");
        await socket.SendAsync(
                hello,
                WebSocketMessageType.Text,
                true,
                cancellationToken)
            .ConfigureAwait(false);

        var buffer = new byte[64 * 1024];
        using var message = new MemoryStream();
        while (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
        {
            message.SetLength(0);
            WebSocketReceiveResult result;
            do
            {
                result = await socket.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    yield break;
                }

                if (result.MessageType != WebSocketMessageType.Text)
                {
                    continue;
                }

                if (message.Length + result.Count > 4 * 1024 * 1024)
                {
                    throw new GachaOverlayProtocolException("Gacha WebSocket frame exceeds the safety limit.");
                }

                message.Write(buffer, 0, result.Count);
            }
            while (!result.EndOfMessage);

            if (message.Length == 0
                || !GachaOverlayDtoParser.TryParseFrame(message.GetBuffer().AsSpan(0, checked((int)message.Length)), out var frame))
            {
                continue;
            }

            yield return frame;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }

        _refreshGate.Dispose();

        await Task.CompletedTask.ConfigureAwait(false);
    }

    /// <summary>
    /// Rotates the official access/refresh token pair once.  A single gate is
    /// shared by /me and WebSocket startup so concurrent refresh attempts do
    /// not invalidate one another's refresh token.
    /// </summary>
    public async Task<bool> TryRefreshAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (!_credentials.CanRefresh)
        {
            return false;
        }

        await _refreshGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Another caller may have completed the rotation while this call
            // was waiting on the gate.
            if (_credentials.AccessExpiresAt is { } expiresAt
                && expiresAt - DateTimeOffset.UtcNow > _options.AccessTokenRefreshSkew)
            {
                return true;
            }

            var refreshed = await GachaOverlayAuthService.RefreshAsync(
                    _httpClient,
                    _credentials,
                    cancellationToken)
                .ConfigureAwait(false);
            _credentials = refreshed;
            if (_credentialsRefreshed is not null)
            {
                try
                {
                    await _credentialsRefreshed(refreshed, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch
                {
                    // Persistence is owned by the host.  A transient disk
                    // failure must not tear down an otherwise valid socket;
                    // the host can persist the current value on shutdown.
                }
            }

            return true;
        }
        catch (GachaOverlayAuthenticationException)
        {
            return false;
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    private async Task EnsureAccessTokenFreshAsync(CancellationToken cancellationToken)
    {
        if (_credentials.AccessExpiresAt is not { } expiresAt
            || expiresAt - DateTimeOffset.UtcNow > _options.AccessTokenRefreshSkew)
        {
            return;
        }

        _ = await TryRefreshAsync(cancellationToken).ConfigureAwait(false);
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, Uri uri)
    {
        var request = new HttpRequestMessage(method, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _credentials.AccessToken);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true, NoStore = true };
        request.Headers.TryAddWithoutValidation("X-Overlay-Version", "2");
        return request;
    }

    private static string CombinePath(string basePath, string path)
    {
        var prefix = string.IsNullOrWhiteSpace(basePath) ? "/" : basePath.TrimEnd('/') + "/";
        return prefix + path.TrimStart('/');
    }

    private static string JsonEscape(string value) =>
        System.Text.Json.JsonSerializer.Serialize(value);

    private static string? FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim();

    private static HttpClient CreateOwnedHttpClient(TimeSpan timeout)
    {
        // Do not forward a bearer header through an HTTP redirect. The
        // adapter is pinned to the official host and intentionally refuses
        // redirects rather than trusting a server-controlled Location value.
        var handler = new HttpClientHandler { AllowAutoRedirect = false };
        return new HttpClient(handler) { Timeout = timeout };
    }

    private static void EnsureTrustedResponse(HttpResponseMessage response)
    {
        var uri = response.RequestMessage?.RequestUri;
        if (uri is null
            || !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(uri.Host, GachaOverlayOptions.OfficialBaseUri.Host, StringComparison.OrdinalIgnoreCase))
        {
            throw new GachaOverlayProtocolException("Gacha response came from an untrusted host.");
        }
    }

    private static void ThrowIfAuthenticationFailed(HttpResponseMessage response)
    {
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            throw new GachaOverlayAuthenticationException(
                "Phiên đăng nhập Gacha đã hết hạn. Hãy đăng nhập lại bằng luồng chính thức.");
        }
    }

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
}

public sealed class GachaOverlayAuthenticationException(string message) : Exception(message);

public sealed class GachaOverlayProtocolException(string message) : Exception(message);
