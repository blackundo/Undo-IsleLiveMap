using TheIsleOverlay.Core;

namespace TheIsleOverlay.Gacha;

public enum GachaStatsSource
{
    None = 0,
    OfficialApi = 1,
    OfficialWebSocket = 2
}

public enum GachaStatsConfidence
{
    None = 0,
    Partial = 1,
    Verified = 2
}

/// <summary>
/// Immutable, provenance-carrying Gacha stats state.  A snapshot is only
/// produced from the official API/WebSocket feed; it is never inferred from
/// pixels or from another overlay process.
/// </summary>
public sealed record GachaStatsSnapshot
{
    public static GachaStatsSnapshot Empty { get; } = new();

    public bool Online { get; init; }

    public bool HasDino { get; init; }

    public string? SteamId { get; init; }

    public string? PersonaName { get; init; }

    public string? ServerId { get; init; }

    public string? ServerName { get; init; }

    public string? Species { get; init; }

    public ExactVitals Vitals { get; init; } = new();

    public NutritionTelemetry? Nutrition { get; init; }

    public PrimeTelemetry? Prime { get; init; }

    public WorldLocation? Location { get; init; }

    public double? UnrealYaw { get; init; }

    public GachaStatsSource Source { get; init; }

    public GachaStatsConfidence Confidence { get; init; }

    public DateTimeOffset? ObservedAt { get; init; }

    public DateTimeOffset? ReceivedAt { get; init; }

    public long? Sequence { get; init; }

    public bool IsStale { get; init; }

    public string? StatusMessage { get; init; }

    public bool HasAnyStats =>
        Vitals.Growth is not null
        || Vitals.Health is not null
        || Vitals.MaxHealth is not null
        || Vitals.Stamina is not null
        || Vitals.MaxStamina is not null
        || Vitals.Hunger is not null
        || Vitals.MaxHunger is not null
        || Vitals.Thirst is not null
        || Vitals.MaxThirst is not null
        || Vitals.FoodValue is not null
        || Vitals.MaxFoodValue is not null;

    /// <summary>
    /// Indicates that the official feed has enough identity/state to keep a
    /// player row alive even when no vital has arrived yet.  Gacha sends
    /// species, position, nutrition, and prime in separate sparse frames.
    /// Treating those frames as "no stats" would make the row disappear while
    /// the feed is healthy.
    /// </summary>
    public bool HasAnyData =>
        HasAnyStats
        || !string.IsNullOrWhiteSpace(Species)
        || Nutrition is not null
        || Prime is not null
        || Location is not null;

    /// <summary>
    /// Maps the source state to the common overlay model.  Local GPS can
    /// subsequently replace <paramref name="location"/> without replacing
    /// the stats provenance.
    /// </summary>
    public PlayerTelemetry ToPlayerTelemetry(WorldLocation? location = null)
    {
        var resolvedLocation = location ?? Location;
        return new PlayerTelemetry
        {
            SteamId = SteamId,
            Name = PersonaName,
            Class = Species,
            Server = ServerName ?? ServerId,
            GrowthPercent = NormalizeGrowth(Vitals.Growth),
            HealthPercent = Percent(Vitals.Health, Vitals.MaxHealth),
            StaminaPercent = Percent(Vitals.Stamina, Vitals.MaxStamina),
            HungerPercent = Percent(Vitals.Hunger, Vitals.MaxHunger),
            ThirstPercent = Percent(Vitals.Thirst, Vitals.MaxThirst),
            ExactVitals = Vitals,
            ExactVitalsSource = Source switch
            {
                GachaStatsSource.OfficialApi => "GachaOfficialApi",
                GachaStatsSource.OfficialWebSocket => "GachaOfficialWebSocket",
                _ => null
            },
            Nutrition = Nutrition,
            Location = resolvedLocation,
            ExactMapHeadingDegrees = UnrealYaw is { } yaw
                ? MapHeading.FromUnrealYaw(yaw)
                : null,
            Prime = Prime
        };
    }

    private static double? NormalizeGrowth(double? value) =>
        value is not { } growth || !double.IsFinite(growth)
            ? null
            : Math.Clamp(growth <= 1d ? growth * 100d : growth, 0d, 100d);

    private static double? Percent(double? current, double? maximum) =>
        current is { } value
        && maximum is > 0d and var max
        && double.IsFinite(value)
        && double.IsFinite(max)
            ? Math.Clamp(value / max * 100d, 0d, 100d)
            : null;
}
