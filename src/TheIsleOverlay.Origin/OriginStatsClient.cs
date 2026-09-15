using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using TheIsleOverlay.Core;

namespace TheIsleOverlay.Origin;

public enum OriginServerId
{
    Main,
    Voice
}

public sealed record OriginServer(OriginServerId Id, string ApiId, string DisplayName)
{
    public static IReadOnlyList<OriginServer> All { get; } =
    [
        new(OriginServerId.Main, "main", "Main Origin"),
        new(OriginServerId.Voice, "voice", "Voice Chat Server")
    ];
}

public sealed record OriginCommandResult(
    string Status,
    JsonElement? Result,
    string? Error,
    string? CommandId = null)
{
    public bool IsCompletedSuccessfully =>
        string.Equals(Status, "completed", StringComparison.OrdinalIgnoreCase)
        && Result is { } value
        && value.ValueKind == JsonValueKind.Object
        && (!value.TryGetProperty("success", out var success)
            || success.ValueKind != JsonValueKind.False);
}

/// <summary>
/// Small first-party client for the authenticated Origin dashboard command
/// API. It sends only the dashboard session cookie supplied by the host and
/// never reads another browser's storage.
/// </summary>
public sealed class OriginStatsClient : IDisposable
{
    public static Uri BaseUri { get; } = new("https://playorigin.gg/");

    private readonly HttpClient _httpClient;
    private readonly bool _ownsClient;
    private readonly string _cookieHeader;
    private int _disposed;

    public OriginStatsClient(string cookieHeader, HttpClient? httpClient = null)
    {
        if (string.IsNullOrWhiteSpace(cookieHeader)
            || cookieHeader.Length > 32_768
            || cookieHeader.Contains('\r')
            || cookieHeader.Contains('\n'))
        {
            throw new ArgumentException("Origin session cookie is invalid.", nameof(cookieHeader));
        }

        _cookieHeader = cookieHeader.Trim();
        _httpClient = httpClient ?? new HttpClient(new HttpClientHandler
        {
            AllowAutoRedirect = false
        })
        {
            Timeout = TimeSpan.FromSeconds(12)
        };
        _ownsClient = httpClient is null;
        PreferredServer = ResolvePreferredServer(_cookieHeader);
    }

    /// <summary>
    /// The dashboard persists its active server in the host-only
    /// <c>origin_server</c> cookie. It is only a hint: the session still
    /// probes the other server when the preferred one has no active dino.
    /// </summary>
    public OriginServer? PreferredServer { get; }

    public async Task<OriginCommandResult> ExecuteHealthAsync(
        OriginServer server,
        CancellationToken cancellationToken = default)
    {
        var command = await ExecuteCommandAsync("health", server, cancellationToken)
            .ConfigureAwait(false);
        return await PollCommandAsync(command, cancellationToken).ConfigureAwait(false);
    }

    public async Task<OriginCommandResult> ExecutePrimeAsync(
        OriginServer server,
        CancellationToken cancellationToken = default)
    {
        var command = await ExecuteCommandAsync("getprime", server, cancellationToken)
            .ConfigureAwait(false);
        return await PollCommandAsync(command, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Resolves whether this authenticated account currently has an active
    /// dinosaur on either Origin server. The dashboard's active-server cookie
    /// is checked first, with a parallel fallback for the remaining server.
    /// </summary>
    public async Task<OriginServer?> DetectActiveServerAsync(
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (PreferredServer is { } preferred)
        {
            var preferredResult = await ExecuteHealthAsync(preferred, cancellationToken)
                .ConfigureAwait(false);
            if (HasActiveDinosaur(preferredResult))
            {
                return preferred;
            }
        }

        var candidates = OriginServer.All
            .Where(server => PreferredServer is null || !Equals(server, PreferredServer))
            .ToArray();
        var probes = await Task.WhenAll(candidates.Select(async server =>
        {
            try
            {
                var result = await ExecuteHealthAsync(server, cancellationToken)
                    .ConfigureAwait(false);
                return (Server: server, Active: HasActiveDinosaur(result));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OriginAuthenticationException)
            {
                throw;
            }
            catch
            {
                return (Server: server, Active: false);
            }
        })).ConfigureAwait(false);
        return probes.FirstOrDefault(probe => probe.Active).Server;
    }

    public async Task<OriginCommandResult> ExecuteCommandAsync(
        string command,
        OriginServer server,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        using var request = CreateRequest(HttpMethod.Post, "api/commands/execute");
        request.Content = new StringContent(
            JsonSerializer.Serialize(new { command, server = server.ApiId }),
            Encoding.UTF8,
            "application/json");
        using var response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken)
            .ConfigureAwait(false);
        EnsureResponse(response);
        var payload = await response.Content.ReadAsByteArrayAsync(cancellationToken)
            .ConfigureAwait(false);
        using var document = JsonDocument.Parse(payload);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new OriginProtocolException("Origin command response is not an object.");
        }

        var id = ReadString(root, "id") ?? ReadString(root, "commandId");
        if (string.IsNullOrWhiteSpace(id))
        {
            var error = ReadString(root, "error") ?? "Origin did not return a command id.";
            throw new OriginProtocolException(error);
        }

        return new OriginCommandResult("queued", null, null, id);
    }

    private async Task<OriginCommandResult> PollCommandAsync(
        OriginCommandResult queued,
        CancellationToken cancellationToken)
    {
        var commandId = queued.CommandId;
        if (string.IsNullOrWhiteSpace(commandId))
        {
            throw new OriginProtocolException("Origin did not return a usable command id.");
        }
        for (var attempt = 0; attempt < 25; attempt++)
        {
            await Task.Delay(attempt == 0 ? 500 : 750, cancellationToken)
                .ConfigureAwait(false);
            using var request = CreateRequest(HttpMethod.Get, $"api/commands/{Uri.EscapeDataString(commandId)}");
            using var response = await _httpClient.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken)
                .ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                continue;
            }

            EnsureResponse(response);
            var payload = await response.Content.ReadAsByteArrayAsync(cancellationToken)
                .ConfigureAwait(false);
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;
            var status = ReadString(root, "status") ?? ReadString(root, "state") ?? "failed";
            var result = root.TryGetProperty("result", out var resultValue)
                ? resultValue.Clone()
                : (JsonElement?)null;
            var error = result is { } resultObject
                ? ReadString(resultObject, "error") ?? ReadString(resultObject, "reason")
                : ReadString(root, "error");
            if (string.Equals(status, "completed", StringComparison.OrdinalIgnoreCase)
                || string.Equals(status, "failed", StringComparison.OrdinalIgnoreCase))
            {
                return new OriginCommandResult(status, result, error);
            }
        }

        return new OriginCommandResult(
            "failed",
            JsonSerializer.SerializeToElement(new { success = false }),
            "Origin command timed out.");
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, new Uri(BaseUri, path));
        request.Headers.TryAddWithoutValidation("Cookie", _cookieHeader);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true, NoStore = true };
        return request;
    }

    private static bool HasActiveDinosaur(OriginCommandResult command)
    {
        if (!command.IsCompletedSuccessfully || command.Result is not { } result)
        {
            return false;
        }

        return !string.IsNullOrWhiteSpace(ReadString(result, "species"))
               || !string.IsNullOrWhiteSpace(ReadString(result, "dino"));
    }

    internal static bool IsTrustedUri(Uri? uri) =>
        uri is not null
        && string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
        && string.Equals(uri.Host, BaseUri.Host, StringComparison.OrdinalIgnoreCase);

    private static OriginServer? ResolvePreferredServer(string cookieHeader)
    {
        foreach (var segment in cookieHeader.Split(';'))
        {
            var pair = segment.Trim();
            var separator = pair.IndexOf('=');
            if (separator <= 0)
            {
                continue;
            }

            var name = pair[..separator].Trim();
            if (!string.Equals(name, "origin_server", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var value = pair[(separator + 1)..].Trim();
            try
            {
                value = Uri.UnescapeDataString(value);
            }
            catch (UriFormatException)
            {
                return null;
            }

            return OriginServer.All.FirstOrDefault(server =>
                string.Equals(server.ApiId, value, StringComparison.OrdinalIgnoreCase));
        }

        return null;
    }

    private static void EnsureResponse(HttpResponseMessage response)
    {
        var uri = response.RequestMessage?.RequestUri;
        if (!IsTrustedUri(uri))
        {
            throw new OriginProtocolException("Origin response came from an untrusted host.");
        }

        // The API is same-origin and should never redirect an authenticated
        // command. Reject redirects explicitly so a proxy or a compromised
        // endpoint cannot move the session cookie to another host.
        if ((int)response.StatusCode is >= 300 and <= 399)
        {
            var location = response.Headers.Location;
            var redirect = location is null
                ? null
                : location.IsAbsoluteUri ? location : new Uri(uri!, location);
            if (!IsTrustedUri(redirect))
            {
                throw new OriginProtocolException("Origin redirected to an untrusted host.");
            }

            throw new OriginProtocolException("Origin command returned an unexpected redirect.");
        }

        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            throw new OriginAuthenticationException("Origin session has expired.");
        }

        response.EnsureSuccessStatusCode();
    }

    private static string? ReadString(JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.Object
        && value.TryGetProperty(name, out var property)
        && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        if (_ownsClient)
        {
            _httpClient.Dispose();
        }
    }
}

public sealed class OriginAuthenticationException(string message) : Exception(message);

public sealed class OriginProtocolException(string message) : Exception(message);
