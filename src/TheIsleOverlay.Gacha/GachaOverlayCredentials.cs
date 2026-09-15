namespace TheIsleOverlay.Gacha;

/// <summary>
/// Credentials supplied by an explicit, first-party Gacha login flow.
///
/// The desktop client never discovers these values from another process and
/// never persists them.  Keep this type deliberately small so a future host
/// can pass a token from its own authenticated callback without coupling the
/// map to Gacha's local storage format.
/// </summary>
public sealed class GachaOverlayCredentials
{
    public GachaOverlayCredentials(
        string accessToken,
        string? steamId = null,
        string? deviceId = null,
        string? personaName = null,
        string? refreshToken = null,
        DateTimeOffset? accessExpiresAt = null,
        DateTimeOffset? refreshExpiresAt = null)
    {
        AccessToken = ValidateToken(accessToken);
        RefreshToken = ValidateOptionalToken(refreshToken, nameof(refreshToken));
        SteamId = NormalizeSteamId(steamId);
        DeviceId = NormalizeOptional(deviceId, 256);
        PersonaName = NormalizeOptional(personaName, 128);
        AccessExpiresAt = NormalizeTimestamp(accessExpiresAt);
        RefreshExpiresAt = NormalizeTimestamp(refreshExpiresAt);
    }

    public string AccessToken { get; }

    /// <summary>
    /// Refresh token returned by the official Steam login flow.  It is kept
    /// separate from <see cref="AccessToken"/> because the service rotates
    /// both values on refresh.
    /// </summary>
    public string? RefreshToken { get; }

    public string? SteamId { get; }

    public string? DeviceId { get; }

    public DateTimeOffset? AccessExpiresAt { get; }

    public DateTimeOffset? RefreshExpiresAt { get; }

    /// <summary>
    /// Optional display name used for the official WebSocket hello message.
    /// Hosts should populate this from the authenticated /me response when
    /// available; it is not required for token validation.
    /// </summary>
    public string? PersonaName { get; }

    public bool CanRefresh =>
        !string.IsNullOrWhiteSpace(RefreshToken)
        && !string.IsNullOrWhiteSpace(DeviceId)
        && (RefreshExpiresAt is null || RefreshExpiresAt > DateTimeOffset.UtcNow);

    /// <summary>
    /// Creates a credential value with the token pair returned by the
    /// official refresh endpoint.  The original instance is immutable so a
    /// concurrent request can never observe a partially rotated pair.
    /// </summary>
    public GachaOverlayCredentials WithTokenSet(
        string accessToken,
        string refreshToken,
        string? deviceId = null,
        DateTimeOffset? accessExpiresAt = null,
        DateTimeOffset? refreshExpiresAt = null) =>
        new(
            accessToken,
            SteamId,
            deviceId ?? DeviceId,
            PersonaName,
            refreshToken,
            accessExpiresAt,
            refreshExpiresAt ?? RefreshExpiresAt);

    // Do not let an accidental log/exception include bearer credentials.
    public override string ToString() => "Gacha overlay credentials (redacted)";

    private static string ValidateToken(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var token = value.Trim();
        if (token.Length > 16_384 || token.Any(char.IsControl))
        {
            throw new ArgumentException("The Gacha access token is invalid.", nameof(value));
        }

        return token;
    }

    private static string? ValidateOptionalToken(string? value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var token = value.Trim();
        if (token.Length > 16_384 || token.Any(char.IsControl))
        {
            throw new ArgumentException("The Gacha refresh token is invalid.", parameterName);
        }

        return token;
    }

    private static string? NormalizeSteamId(string? value)
    {
        var normalized = NormalizeOptional(value, 32);
        return normalized is not null && normalized.All(char.IsDigit) && normalized.Length == 17
            ? normalized
            : null;
    }

    private static string? NormalizeOptional(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = value.Trim();
        return normalized.Length <= maxLength && !normalized.Any(char.IsControl)
            ? normalized
            : null;
    }

    private static DateTimeOffset? NormalizeTimestamp(DateTimeOffset? value) =>
        value is { } timestamp && timestamp != default
            ? timestamp.ToUniversalTime()
            : null;
}
