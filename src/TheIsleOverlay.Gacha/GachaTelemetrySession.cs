using TheIsleOverlay.Core;

namespace TheIsleOverlay.Gacha;

/// <summary>
/// Adapts the official Gacha state stream to the common telemetry session
/// consumed by the WPF host.  It contributes identity/vitals only; the outer
/// local telemetry session remains authoritative for GPS and Pro map data.
/// </summary>
public sealed class GachaTelemetrySession : ITelemetrySession
{
    private readonly GachaStatsSession _session;
    private readonly bool _ownsSession;
    private TelemetrySnapshot? _current;
    private int _disposed;
    private int _watchStarted;

    public GachaTelemetrySession(
        GachaStatsSession session,
        bool ownsSession = true)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _ownsSession = ownsSession;
    }

    public async IAsyncEnumerable<TelemetrySnapshot> WatchAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation]
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (Interlocked.Exchange(ref _watchStarted, 1) != 0)
        {
            throw new InvalidOperationException("A telemetry session can only be watched once.");
        }

        await foreach (var stats in _session.WatchWithReconnectAsync(
                           cancellationToken: cancellationToken)
                           .ConfigureAwait(false))
        {
            _current = GachaStatsSnapshotMerger.Merge(
                _current,
                stats,
                DateTimeOffset.UtcNow);
            yield return _current;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        if (_ownsSession)
        {
            await _session.DisposeAsync().ConfigureAwait(false);
        }
    }
}
