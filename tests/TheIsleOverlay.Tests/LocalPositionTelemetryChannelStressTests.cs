using System.Runtime.CompilerServices;
using TheIsleOverlay.Core;
using TheIsleOverlay.LocalTelemetry;

namespace TheIsleOverlay.Tests;

/// <summary>
/// Protects the fan-in channel from silently evicting a low-rate authoritative
/// snapshot when the local movement lane emits a burst.
/// </summary>
public sealed class LocalPositionTelemetryChannelStressTests
{
    [Fact]
    public async Task WatchAsync_PreservesRemoteSnapshotDuringLocalBurst()
    {
        var remote = new SentinelRemoteSession();
        var capacityReached = NewSignal();
        var releaseCapacity = NewSignal();
        var item32Written = NewSignal();
        var local = new BurstLocalSource(
            remote.SentinelWritten.Task,
            capacityReached,
            releaseCapacity,
            item32Written,
            512);
        await using var session = new LocalPositionTelemetrySession(
            remoteSession: remote,
            localSource: local,
            sourceName: "TEST");

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var watch = session.WatchAsync(timeout.Token)
            .GetAsyncEnumerator(timeout.Token);

        // The first item is emitted before the fan-in reader starts. Keep the
        // reader paused while the remote sentinel and local burst are queued.
        Assert.True(await watch.MoveNextAsync());
        Assert.Equal("TEST", watch.Current.Source);
        await remote.SentinelWritten.Task.WaitAsync(timeout.Token);

        // Wait until the queue contains the sentinel plus 31 local samples.
        // The next sample is item #32 (zero-based index 31), so the queue is
        // full immediately before it is yielded. This barrier prevents the
        // test from reading the sentinel before the burst reaches capacity.
        await capacityReached.Task.WaitAsync(timeout.Token);
        releaseCapacity.TrySetResult(true);

        // With the old DropOldest mode item #32 is written immediately and
        // evicts the sentinel. With Wait, its writer blocks at the full queue;
        // give the old-mode write a short chance to complete before reading.
        await Task.WhenAny(item32Written.Task, Task.Delay(100));

        // The local producer fills the bounded queue and waits. The sentinel
        // must remain at the head and be delivered before any local sample.
        Assert.True(await watch.MoveNextAsync());
        Assert.Equal("GACHA-SENTINEL", watch.Current.Source);
        Assert.True(watch.Current.Success);
    }

    private static TaskCompletionSource<bool> NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class SentinelRemoteSession : ITelemetrySession
    {
        public TaskCompletionSource<bool> SentinelWritten { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async IAsyncEnumerable<TelemetrySnapshot> WatchAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return new TelemetrySnapshot
            {
                Source = "GACHA-SENTINEL",
                Success = true,
                ServerOnline = true,
                PlayerOnline = true,
                Player = new PlayerTelemetry { Class = "Triceratops" }
            };

            // Code after yield runs only after PumpRemoteAsync has completed
            // its WriteAsync, making this a deterministic queue-entry barrier.
            SentinelWritten.TrySetResult(true);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class BurstLocalSource(
        Task start,
        TaskCompletionSource<bool> capacityReached,
        TaskCompletionSource<bool> releaseCapacity,
        TaskCompletionSource<bool> item32Written,
        int count) : ILocalMovementSource
    {
        public async IAsyncEnumerable<LocalMovementObservation> WatchAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await start.WaitAsync(cancellationToken);
            for (var index = 0; index < count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // Item #32 is deliberately held until the test has observed
                // that the preceding sentinel + 31 local samples fill the
                // capacity-32 fan-in queue.
                if (index == 31)
                {
                    capacityReached.TrySetResult(true);
                    await releaseCapacity.Task.WaitAsync(cancellationToken);
                }

                yield return new LocalMovementObservation(
                    DateTimeOffset.UtcNow,
                    new UnrealMovementCandidate(
                        index,
                        200,
                        30,
                        90,
                        index,
                        64,
                        380,
                        26),
                    "127.0.0.1:7777");

                if (index == 31)
                {
                    // Reaching the next iteration means PumpLocalAsync has
                    // completed its WriteAsync for item #32. In Wait mode it
                    // remains blocked here until the test consumes the
                    // sentinel; in DropOldest mode this completes promptly.
                    item32Written.TrySetResult(true);
                }
            }

            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
