using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace TheIsleOverlay.Gacha;

/// <summary>
/// Official Steam login and token-rotation contract used by the Gacha
/// overlay.  The service intentionally accepts credentials only from the
/// first-party callback; it never discovers cookies or tokens in another
/// process' files.
/// </summary>
public static class GachaOverlayAuthService
{
    public const string CallbackScheme = "isle-overlay";

    public static Uri LoginUri { get; } = new(
        GachaOverlayOptions.OfficialBaseUri,
        "api/overlay/auth/steam/login");

    public static async Task<GachaOverlayAuthValidationState> ValidateAsync(
        HttpClient httpClient,
        GachaOverlayCredentials credentials,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(credentials);
        try
        {
            // Validation must never rotate a refresh token.  The caller owns
            // persistence, so refreshing inside a short-lived validation
            // client could consume the stored refresh token and then discard
            // the newly rotated pair.  Send the current access token exactly
            // once; an Invalid result lets the host explicitly refresh and
            // durably save the replacement before it opens a live session.
            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                new Uri(GachaOverlayOptions.OfficialBaseUri, "api/overlay/me"));
            request.Headers.Authorization = new AuthenticationHeaderValue(
                "Bearer",
                credentials.AccessToken);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Headers.CacheControl = new CacheControlHeaderValue
            {
                NoCache = true,
                NoStore = true
            };
            request.Headers.TryAddWithoutValidation("X-Overlay-Version", "2");

            using var response = await httpClient.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken)
                .ConfigureAwait(false);
            EnsureTrustedResponse(response);
            if (response.StatusCode is HttpStatusCode.Unauthorized
                or HttpStatusCode.Forbidden)
            {
                return GachaOverlayAuthValidationState.Invalid;
            }

            response.EnsureSuccessStatusCode();
            var payload = await response.Content.ReadAsByteArrayAsync(cancellationToken)
                .ConfigureAwait(false);
            if (!GachaOverlayDtoParser.TryParseMe(payload, out _))
            {
                return GachaOverlayAuthValidationState.Unavailable;
            }

            return GachaOverlayAuthValidationState.Valid;
        }
        catch (Exception exception) when (
            exception is HttpRequestException
            or IOException
            or InvalidDataException
            or GachaOverlayProtocolException
            || exception is OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return GachaOverlayAuthValidationState.Unavailable;
        }
    }

    /// <summary>
    /// Exchanges a refresh token for the rotated access/refresh pair.  The
    /// caller owns persistence of the returned credentials.  The request is
    /// pinned to <c>player.isle.vn</c> and redirects are rejected before any
    /// response is accepted.
    /// </summary>
    public static async Task<GachaOverlayCredentials> RefreshAsync(
        HttpClient httpClient,
        GachaOverlayCredentials credentials,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(credentials);
        if (!credentials.CanRefresh)
        {
            throw new GachaOverlayAuthenticationException(
                "Phiên Gacha không có refresh token còn hiệu lực.");
        }

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            new Uri(GachaOverlayOptions.OfficialBaseUri, "api/overlay/auth/refresh"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Content = new StringContent(
            JsonSerializer.Serialize(
                new
                {
                    refreshToken = credentials.RefreshToken,
                    deviceId = credentials.DeviceId
                },
                GachaOverlayJson.Options),
            Encoding.UTF8,
            "application/json");

        using var response = await httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken)
            .ConfigureAwait(false);
        EnsureTrustedResponse(response);
        if (response.StatusCode is HttpStatusCode.Unauthorized
            or HttpStatusCode.Forbidden
            or HttpStatusCode.Conflict)
        {
            throw new GachaOverlayAuthenticationException(
                "Phiên Gacha đã hết hạn; hãy đăng nhập Steam lại.");
        }

        response.EnsureSuccessStatusCode();
        var payload = await response.Content.ReadAsByteArrayAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!TryParseRefreshResponse(payload, out var tokenSet))
        {
            throw new GachaOverlayProtocolException(
                "Gacha refresh trả về token không hợp lệ.");
        }

        return credentials.WithTokenSet(
            tokenSet.AccessToken,
            tokenSet.RefreshToken,
            tokenSet.DeviceId,
            tokenSet.AccessExpiresAt,
            tokenSet.RefreshExpiresAt);
    }

    public static bool TryParseCallback(
        string? callback,
        out GachaOverlayAuthResult? result) =>
        TryParseCallback(callback, out result, out _);

    public static bool TryParseCallback(
        string? callback,
        out GachaOverlayAuthResult? result,
        out string? errorMessage)
    {
        result = null;
        errorMessage = null;
        if (!Uri.TryCreate(callback, UriKind.Absolute, out var uri)
            || !string.Equals(uri.Scheme, CallbackScheme, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var query = ParseQuery(uri.Query, out var parseError);
        if (query is null)
        {
            errorMessage = parseError ?? "Callback không hợp lệ.";
            return false;
        }

        if (query.TryGetValue("error", out var callbackError)
            && !string.IsNullOrWhiteSpace(callbackError))
        {
            errorMessage = callbackError;
            return false;
        }

        var steamId = First(query, "steamId", "steamid", "steamId64");
        var accessToken = First(query, "accessToken", "access_token");
        var refreshToken = First(query, "refreshToken", "refresh_token");
        var deviceId = First(query, "deviceId", "device_id");
        if (!IsSteamId(steamId)
            || !IsToken(accessToken)
            || !IsToken(refreshToken)
            || !IsOptionalToken(deviceId))
        {
            errorMessage = "Callback Gacha thiếu SteamID64 hoặc token hợp lệ.";
            return false;
        }

        var expiresIn = ParsePositiveSeconds(First(query, "expiresIn", "expires_in"));
        var accessExpiresAt = expiresIn is { } seconds
            ? DateTimeOffset.UtcNow.AddSeconds(seconds)
            : DateTimeOffset.UtcNow.AddMinutes(15);
        var refreshExpiresAt = ParseTimestamp(First(query, "refreshExpiresAt", "refresh_expires_at"));
        var personaName = First(query, "personaName", "name");
        try
        {
            result = new GachaOverlayAuthResult(
                steamId!,
                accessToken!,
                refreshToken!,
                deviceId!,
                accessExpiresAt,
                refreshExpiresAt,
                personaName);
            return true;
        }
        catch (ArgumentException)
        {
            errorMessage = "Callback Gacha chứa dữ liệu không hợp lệ.";
            return false;
        }
    }

    internal static bool IsSteamId(string? value) =>
        value is { Length: 17 } && value.All(character => character is >= '0' and <= '9');

    internal static bool IsToken(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Length <= 16_384
        && !value.Any(char.IsControl);

    internal static bool IsOptionalToken(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Length <= 256
        && !value.Any(char.IsControl);

    private static Dictionary<string, string>? ParseQuery(
        string queryString,
        out string? errorMessage)
    {
        errorMessage = null;
        var query = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var field in queryString.TrimStart('?')
                     .Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = field.IndexOf('=');
            if (separator <= 0)
            {
                errorMessage = "Callback query không hợp lệ.";
                return null;
            }

            string name;
            string value;
            try
            {
                name = Uri.UnescapeDataString(field[..separator]);
                value = Uri.UnescapeDataString(field[(separator + 1)..]);
            }
            catch (UriFormatException)
            {
                errorMessage = "Callback query chứa mã hóa URL không hợp lệ.";
                return null;
            }

            if (string.IsNullOrWhiteSpace(name)
                || name.Any(char.IsControl)
                || !query.TryAdd(name, value))
            {
                errorMessage = "Callback query bị trùng hoặc không hợp lệ.";
                return null;
            }
        }

        return query;
    }

    private static string? First(
        IReadOnlyDictionary<string, string> query,
        params string[] names) =>
        names.Select(name => query.TryGetValue(name, out var value) ? value : null)
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim();

    private static int? ParsePositiveSeconds(string? value) =>
        int.TryParse(
            value,
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out var seconds)
        && seconds > 0
            ? seconds
            : null;

    private static DateTimeOffset? ParseTimestamp(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var epoch))
        {
            try
            {
                return epoch > 10_000_000_000
                    ? DateTimeOffset.FromUnixTimeMilliseconds(epoch)
                    : DateTimeOffset.FromUnixTimeSeconds(epoch);
            }
            catch (ArgumentOutOfRangeException)
            {
                return null;
            }
        }

        return DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var parsed)
            ? parsed
            : null;
    }

    private static bool TryParseRefreshResponse(
        ReadOnlySpan<byte> payload,
        out RefreshTokenSet tokenSet)
    {
        tokenSet = default;
        try
        {
            using var document = JsonDocument.Parse(payload.ToArray());
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            var data = TryGetObject(root, "data") ?? root;
            var accessToken = ReadString(data, "accessToken") ?? ReadString(data, "access_token");
            var refreshToken = ReadString(data, "refreshToken") ?? ReadString(data, "refresh_token");
            var deviceId = ReadString(data, "deviceId") ?? ReadString(data, "device_id");
            if (!IsToken(accessToken) || !IsToken(refreshToken))
            {
                return false;
            }

            var expiresIn = ReadNumber(data, "expiresIn") ?? ReadNumber(data, "expires_in");
            var accessExpiresAt = expiresIn is > 0d and var seconds
                ? DateTimeOffset.UtcNow.AddSeconds(seconds)
                : DateTimeOffset.UtcNow.AddMinutes(15);
            var refreshExpiresAt = ParseTimestamp(
                ReadString(data, "refreshExpiresAt")
                ?? ReadString(data, "refresh_expires_at"));
            tokenSet = new RefreshTokenSet(
                accessToken!,
                refreshToken!,
                IsOptionalToken(deviceId) ? deviceId : null,
                accessExpiresAt,
                refreshExpiresAt);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static JsonElement? TryGetObject(JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.Object
        && value.EnumerateObject().FirstOrDefault(property =>
            string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)) is { } property
        && property.Value.ValueKind == JsonValueKind.Object
            ? property.Value
            : null;

    private static string? ReadString(JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.Object
        && value.EnumerateObject().FirstOrDefault(property =>
            string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)) is { } property
        && property.Value.ValueKind == JsonValueKind.String
            ? property.Value.GetString()
            : null;

    private static double? ReadNumber(JsonElement value, string name)
    {
        var text = ReadString(value, name);
        if (text is not null
            && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedText)
            && double.IsFinite(parsedText))
        {
            return parsedText;
        }

        if (value.ValueKind == JsonValueKind.Object
            && value.EnumerateObject().FirstOrDefault(property =>
                string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)) is { } property
            && property.Value.ValueKind == JsonValueKind.Number
            && property.Value.TryGetDouble(out var parsed)
            && double.IsFinite(parsed))
        {
            return parsed;
        }

        return null;
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

    private readonly record struct RefreshTokenSet(
        string AccessToken,
        string RefreshToken,
        string? DeviceId,
        DateTimeOffset AccessExpiresAt,
        DateTimeOffset? RefreshExpiresAt);
}

public enum GachaOverlayAuthValidationState
{
    Valid,
    Invalid,
    Unavailable
}

public sealed record GachaOverlayAuthResult(
    string SteamId,
    string AccessToken,
    string RefreshToken,
    string DeviceId,
    DateTimeOffset? AccessExpiresAt,
    DateTimeOffset? RefreshExpiresAt,
    string? PersonaName = null)
{
    public GachaOverlayCredentials ToCredentials() => new(
        AccessToken,
        SteamId,
        DeviceId,
        PersonaName,
        RefreshToken,
        AccessExpiresAt,
        RefreshExpiresAt);
}
