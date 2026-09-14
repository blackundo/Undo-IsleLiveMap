using System.Runtime.CompilerServices;

namespace TheIsleOverlay.Core;

public sealed class AuthenticationInvalidatingTelemetrySession :
    ITelemetrySession,
    IRealtimeConnectionControl
{
    private readonly ITelemetrySession _inner;
    private readonly Action _invalidateCredentials;
    private int _invalidated;

    public AuthenticationInvalidatingTelemetrySession(
        ITelemetrySession inner,
        Action invalidateCredentials)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _invalidateCredentials = invalidateCredentials
            ?? throw new ArgumentNullException(nameof(invalidateCredentials));
    }

    public async IAsyncEnumerable<TelemetrySnapshot> WatchAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var snapshot in _inner
                           .WatchAsync(cancellationToken)
                           .ConfigureAwait(false))
        {
            if (snapshot.SessionState == TelemetrySessionState.AuthenticationRequired
                && Interlocked.Exchange(ref _invalidated, 1) == 0)
            {
                _invalidateCredentials();
            }

            yield return snapshot;
        }
    }

    public ValueTask DisposeAsync() => _inner.DisposeAsync();

    public Task PauseRealtimeAsync(CancellationToken cancellationToken = default) =>
        _inner is IRealtimeConnectionControl control
            ? control.PauseRealtimeAsync(cancellationToken)
            : Task.CompletedTask;

    public void ResumeRealtime()
    {
        if (_inner is IRealtimeConnectionControl control)
        {
            control.ResumeRealtime();
        }
    }
}
