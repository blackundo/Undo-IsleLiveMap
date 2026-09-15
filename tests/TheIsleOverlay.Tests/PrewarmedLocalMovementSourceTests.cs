using System.Threading.Channels;
using TheIsleOverlay.Core;
using TheIsleOverlay.LocalTelemetry;

namespace TheIsleOverlay.Tests;

public sealed class PrewarmedLocalMovementSourceTests
{
    [Fact]
    public async Task WatchAsync_ReplaysNewestObservationCapturedBeforeSubscription()
    {
        var inner = new FakeLocalMovementSource();
        await using var source = new PrewarmedLocalMovementSource(inner);
        source.Start();

        inner.Publish(Observation(1));
        inner.Publish(Observation(2));
        await inner.WaitUntilReadAsync(2);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await using var enumerator = source.WatchAsync(timeout.Token).GetAsyncEnumerator();

        Assert.True(await enumerator.MoveNextAsync());
        Assert.Equal(2d, enumerator.Current.Movement.X);
    }

    [Fact]
    public async Task WatchAsync_DropsOldUiObservationsButKeepsLatest()
    {
        var inner = new FakeLocalMovementSource();
        await using var source = new PrewarmedLocalMovementSource(inner);
        source.Start();

        inner.Publish(Observation(10));
        inner.Publish(Observation(20));
        inner.Publish(Observation(30));
        await inner.WaitUntilReadAsync(3);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await using var enumerator = source.WatchAsync(timeout.Token).GetAsyncEnumerator();
        Assert.True(await enumerator.MoveNextAsync());
        Assert.Equal(30d, enumerator.Current.Movement.X);
    }

    [Fact]
    public async Task WatchAsync_CoalescesVitalsWithoutRefreshingOrErasingMovement()
    {
        var inner = new FakeLocalMovementSource();
        await using var source = new PrewarmedLocalMovementSource(inner);
        source.Start();
        var movementAt = DateTimeOffset.Parse("2026-09-12T01:02:03Z");
        var vitalsAt = movementAt.AddSeconds(1);
        inner.Publish(Observation(30) with { ObservedAt = movementAt });
        inner.Publish(LocalMovementObservation.VitalsOnly(
            new LocalDinosaurVitalsObservation(
                vitalsAt,
                new ExactVitals { Health = 75, MaxHealth = 100 },
                42),
            "127.0.0.1:7777"));
        await inner.WaitUntilReadAsync(2);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await using var enumerator = source.WatchAsync(timeout.Token).GetAsyncEnumerator();
        Assert.True(await enumerator.MoveNextAsync());
        Assert.True(enumerator.Current.HasMovement);
        Assert.Equal(30d, enumerator.Current.Movement.X);
        Assert.Equal(movementAt, enumerator.Current.ObservedAt);
        Assert.Equal(vitalsAt, enumerator.Current.DinosaurVitals?.ObservedAt);
    }

    [Fact]
    public void Coalesce_DropsLateVitalsFromPreviousEndpoint()
    {
        var current = Observation(30) with { ServerEndpoint = "new:7777" };
        var late = LocalMovementObservation.VitalsOnly(
            new LocalDinosaurVitalsObservation(
                DateTimeOffset.UtcNow,
                new ExactVitals { Health = 50, MaxHealth = 100 },
                42),
            "old:7777");

        var combined = LocalMovementObservation.Coalesce(current, late);

        Assert.Equal(current, combined);
        Assert.Null(combined.DinosaurVitals);
    }

    private static LocalMovementObservation Observation(double x) => new(
        DateTimeOffset.UtcNow,
        new UnrealMovementCandidate(x, 2d, 3d, 4d, 5f, 6, 7, 8),
        "127.0.0.1:7777");

    private sealed class FakeLocalMovementSource : ILocalMovementSource
    {
        private readonly Channel<LocalMovementObservation> _channel = Channel.CreateUnbounded<LocalMovementObservation>();
        private int _readCount;

        public void Publish(LocalMovementObservation observation) => _channel.Writer.TryWrite(observation);

        public async Task WaitUntilReadAsync(int expected)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            while (Volatile.Read(ref _readCount) < expected)
            {
                await Task.Delay(5, timeout.Token);
            }
        }

        public async IAsyncEnumerable<LocalMovementObservation> WatchAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation]
            CancellationToken cancellationToken = default)
        {
            await foreach (var observation in _channel.Reader.ReadAllAsync(cancellationToken))
            {
                Interlocked.Increment(ref _readCount);
                yield return observation;
            }
        }

        public ValueTask DisposeAsync()
        {
            _channel.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }
    }
}
