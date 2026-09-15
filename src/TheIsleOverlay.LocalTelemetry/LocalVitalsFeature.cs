namespace TheIsleOverlay.LocalTelemetry;

/// <summary>
/// Production gate for passive inbound Iris vitals.
///
/// Release 2.1 promotes the validated Iris decoder to the default authority:
/// the overlay reads the player's current status directly from the game
/// traffic on every server, without waiting for a website/API poll. The
/// disable switch is deliberately opt-out and is retained only as an
/// emergency rollback for support diagnostics.
/// </summary>
public static class LocalVitalsFeature
{
    public const string EnvironmentVariable = "ISLELIVEMAP_LOCAL_VITALS_CANARY";
    public const string DisableEnvironmentVariable =
        "ISLELIVEMAP_DISABLE_INBOUND_VITALS";
    // Kept as a compatibility alias for diagnostics from the validation build.
    // Inbound-only is now the production default; the old opt-in variable no
    // longer controls whether status capture is enabled.
    public const string InboundOnlyEnvironmentVariable =
        "ISLELIVEMAP_INBOUND_VITALS_ONLY";
    public const string SourceName = "LocalIris";

    public static bool IsEnabled() => IsProductionEnabled(
        Environment.GetEnvironmentVariable(DisableEnvironmentVariable));

    public static bool IsInboundOnly() => IsEnabled();

    internal static bool IsProductionEnabled(string? disableValue) =>
        !IsEnabled(disableValue);

    internal static bool IsEnabled(string? value) =>
        value is not null
        && (string.Equals(value.Trim(), "1", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value.Trim(), "true", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value.Trim(), "yes", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value.Trim(), "on", StringComparison.OrdinalIgnoreCase));
}

public readonly record struct LocalVitalsCaptureDiagnostics(
    bool Enabled,
    string Source,
    DateTimeOffset? LastObservationAt,
    long PublishedObservations,
    long SessionResets);
