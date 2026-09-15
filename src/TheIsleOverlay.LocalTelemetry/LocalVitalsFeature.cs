namespace TheIsleOverlay.LocalTelemetry;

/// <summary>
/// Production gate for passive inbound Iris vitals. Capture/probe code can opt
/// in explicitly while the default remains disabled until live fixtures have
/// validated each supported dinosaur and lifecycle transition.
/// </summary>
public static class LocalVitalsFeature
{
    public const string EnvironmentVariable = "ISLELIVEMAP_LOCAL_VITALS_CANARY";
    // Temporary validation switch: keep Origin/Gacha/IslePilot sessions alive
    // for non-stat data, but never accept their vitals while this is enabled.
    // This lets us measure inbound-only stability without deleting the rollback
    // path before the canary has passed all reconnect/species tests.
    public const string InboundOnlyEnvironmentVariable =
        "ISLELIVEMAP_INBOUND_VITALS_ONLY";
    public const string SourceName = "LocalIris";

    public static bool IsEnabled() => IsEnabled(
        Environment.GetEnvironmentVariable(EnvironmentVariable));

    public static bool IsInboundOnly() => IsEnabled(
        Environment.GetEnvironmentVariable(InboundOnlyEnvironmentVariable));

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
