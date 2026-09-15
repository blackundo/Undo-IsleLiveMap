using System.Threading.Channels;
using TheIsleOverlay.Core;
using TheIsleOverlay.LocalTelemetry;
using TheIsleOverlay.ProClient;

var durationSeconds = args.Length > 0 && int.TryParse(args[0], out var requestedDuration)
    ? Math.Clamp(requestedDuration, 5, 900)
    : 180;

using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(durationSeconds));
using var accessService = new ProAccessService();
var access = await accessService.InitializeAsync("1.4.0", timeout.Token);
var proSource = accessService.CreateRemotePlayerSource();
if (proSource is null)
{
    Console.WriteLine(
        $"Pro source unavailable: authenticated={access.IsAuthenticated} pro={access.IsPro} " +
        $"agentReady={access.AgentReady} status={access.StatusCode}");
    return;
}

await using var freeSource = new NpcapLocalMovementSource();
await using (proSource)
{
    var events = Channel.CreateUnbounded<PositionEvent>(new UnboundedChannelOptions
    {
        SingleReader = true,
        SingleWriter = false
    });

    var freeTask = Task.Run(async () =>
    {
        try
        {
            await foreach (var item in freeSource.WatchAsync(timeout.Token))
            {
                if (!item.HasMovement)
                {
                    continue;
                }

                await events.Writer.WriteAsync(new FreeEvent(item), timeout.Token);
            }
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
        }
    }, timeout.Token);

    var proTask = Task.Run(async () =>
    {
        try
        {
            await foreach (var item in proSource.WatchAsync(timeout.Token))
            {
                await events.Writer.WriteAsync(new ProEvent(item), timeout.Token);
            }
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            Console.WriteLine($"PRO ERROR {exception.GetType().Name}: {exception.Message}");
        }
    }, timeout.Token);

    _ = Task.WhenAll(freeTask, proTask).ContinueWith(
        _ => events.Writer.TryComplete(),
        CancellationToken.None,
        TaskContinuationOptions.ExecuteSynchronously,
        TaskScheduler.Default);

    LocalMovementObservation? free = null;
    RemotePlayerTelemetryFrame? pro = null;
    var lastLineAt = DateTimeOffset.MinValue;
    var mismatchCount = 0;
    try
    {
        await foreach (var item in events.Reader.ReadAllAsync(timeout.Token))
        {
            switch (item)
            {
                case FreeEvent freeEvent:
                    free = freeEvent.Value;
                    break;
                case ProEvent proEvent:
                    pro = proEvent.Value;
                    break;
            }

            if (free is not { } freeValue || pro is null)
            {
                continue;
            }

            var now = DateTimeOffset.UtcNow;
            var dx = freeValue.Movement.X - pro.LocalLocation.X;
            var dy = freeValue.Movement.Y - pro.LocalLocation.Y;
            var dz = freeValue.Movement.Z - (pro.LocalLocation.Z ?? 0d);
            var distance = Math.Sqrt(dx * dx + dy * dy + dz * dz);
            var mismatch = distance >= 2_500d;
            if (mismatch)
            {
                mismatchCount++;
            }

            if (mismatch || now - lastLineAt >= TimeSpan.FromSeconds(1))
            {
                Console.WriteLine(
                    $"{now:HH:mm:ss.fff} {(mismatch ? "MISMATCH" : "MATCH"),-8} " +
                    $"free=({freeValue.Movement.X:F1},{freeValue.Movement.Y:F1},{freeValue.Movement.Z:F1}) " +
                    $"pro=({pro.LocalLocation.X:F1},{pro.LocalLocation.Y:F1},{pro.LocalLocation.Z:F1}) " +
                    $"delta={distance:F1} freeTs={freeValue.Movement.ClientTimestamp:F3} " +
                    $"freeAge={(now - freeValue.ObservedAt).TotalMilliseconds:F0}ms " +
                    $"proAge={(now - pro.ObservedAt).TotalMilliseconds:F0}ms");
                lastLineAt = now;
            }
        }
    }
    catch (OperationCanceledException) when (timeout.IsCancellationRequested)
    {
    }

    Console.WriteLine($"SUMMARY proFreeMismatches={mismatchCount}");
}

abstract record PositionEvent;
sealed record FreeEvent(LocalMovementObservation Value) : PositionEvent;
sealed record ProEvent(RemotePlayerTelemetryFrame Value) : PositionEvent;
