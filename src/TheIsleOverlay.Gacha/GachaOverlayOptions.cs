namespace TheIsleOverlay.Gacha;

public sealed record GachaOverlayOptions
{
    public static Uri OfficialBaseUri { get; } = new("https://player.isle.vn/");

    public Uri BaseUri { get; init; } = OfficialBaseUri;

    public TimeSpan ApiTimeout { get; init; } = TimeSpan.FromSeconds(8);

    /// <summary>How early an expiring access token should be rotated.</summary>
    public TimeSpan AccessTokenRefreshSkew { get; init; } = TimeSpan.FromSeconds(30);

    // Gacha sends sparse heartbeat/stat frames; tolerate a normal 7–8 second
    // gap without making the HUD flicker stale.  A clean socket close still
    // publishes a stale/reconnecting snapshot immediately.
    public TimeSpan LiveDataLifetime { get; init; } = TimeSpan.FromSeconds(15);

    public TimeSpan ApiDataLifetime { get; init; } = TimeSpan.FromMinutes(2);

    public void Validate()
    {
        if (!BaseUri.IsAbsoluteUri
            || !string.Equals(BaseUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(BaseUri.Host, OfficialBaseUri.Host, StringComparison.OrdinalIgnoreCase)
            || BaseUri.Port is not (-1 or 443))
        {
            throw new ArgumentException(
                "Only the official HTTPS Gacha API host is trusted.",
                nameof(BaseUri));
        }

        if (ApiTimeout <= TimeSpan.Zero
            || AccessTokenRefreshSkew < TimeSpan.Zero
            || LiveDataLifetime <= TimeSpan.Zero
            || ApiDataLifetime <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(ApiTimeout), "Gacha timeouts and lifetimes must be positive.");
        }
    }
}
