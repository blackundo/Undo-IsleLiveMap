namespace TheIsleOverlay.LocalTelemetry;

public interface ILocalMovementSource : IAsyncDisposable
{
    IAsyncEnumerable<LocalMovementObservation> WatchAsync(
        CancellationToken cancellationToken = default);
}

public readonly record struct LocalMovementObservation(
    DateTimeOffset ObservedAt,
    UnrealMovementCandidate Movement,
    string? ServerEndpoint = null,
    LocalDinosaurVitalsObservation? DinosaurVitals = null)
{
    /// <summary>
    /// False for an inbound vitals-only delta. Kept as a non-positional
    /// property so the legacy four-field constructor/deconstruction remains
    /// source-compatible for GPS consumers.
    /// </summary>
    public bool HasMovement { get; init; } = true;

    /// <summary>
    /// Creates an update that carries only independently timestamped inbound
    /// vitals. The default movement value must never be consumed when
    /// <see cref="HasMovement"/> is false.
    /// </summary>
    public static LocalMovementObservation VitalsOnly(
        LocalDinosaurVitalsObservation vitals,
        string? serverEndpoint = null) => new LocalMovementObservation(
        vitals.ObservedAt,
        default,
        serverEndpoint,
        vitals) with { HasMovement = false };

    /// <summary>
    /// Coalesces independent movement and vitals updates without allowing a
    /// vitals-only event to refresh the movement clock. Endpoint changes drop
    /// state from the previous connection.
    /// </summary>
    public static LocalMovementObservation Coalesce(
        LocalMovementObservation? current,
        LocalMovementObservation update)
    {
        if (current is not { } previous)
        {
            return update;
        }

        if (update.HasMovement)
        {
            var sameEndpoint = EndpointsCompatible(
                previous.ServerEndpoint,
                update.ServerEndpoint);
            return update with
            {
                DinosaurVitals = update.DinosaurVitals
                                   ?? (sameEndpoint ? previous.DinosaurVitals : null)
            };
        }

        if (!EndpointsCompatible(previous.ServerEndpoint, update.ServerEndpoint))
        {
            // A late inbound datagram from the old server must not attach its
            // actor state to the active movement session. Preserve a current
            // GPS sample when one exists; if both samples are vitals-only,
            // expose the newest endpoint so the next movement sample can begin
            // a clean session.
            return previous.HasMovement ? previous : update;
        }

        return new LocalMovementObservation(
            previous.HasMovement ? previous.ObservedAt : update.ObservedAt,
            previous.HasMovement ? previous.Movement : default,
            previous.ServerEndpoint ?? update.ServerEndpoint,
            update.DinosaurVitals ?? previous.DinosaurVitals)
        {
            HasMovement = previous.HasMovement
        };
    }

    private static bool SameEndpoint(string? left, string? right) =>
        string.IsNullOrWhiteSpace(left)
            ? string.IsNullOrWhiteSpace(right)
            : !string.IsNullOrWhiteSpace(right)
              && string.Equals(
                  left.Trim(),
                  right.Trim(),
                  StringComparison.OrdinalIgnoreCase);

    private static bool EndpointsCompatible(string? left, string? right) =>
        string.IsNullOrWhiteSpace(left)
        || string.IsNullOrWhiteSpace(right)
        || SameEndpoint(left, right);
}

public interface ILocalVitalsFeatureSource
{
    bool LocalVitalsEnabled { get; }
}
