using System.Diagnostics;
using TheIsleOverlay.LocalTelemetry;

var durationSeconds = args.Length > 0 && int.TryParse(args[0], out var requestedDuration)
    ? Math.Clamp(requestedDuration, 5, 900)
    : 180;

using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(durationSeconds));
await using var source = new NpcapLocalMovementSource();

LocalMovementObservation? previous = null;
var stopwatch = Stopwatch.StartNew();
var lastPeriodicOutput = TimeSpan.MinValue;
var observations = 0;
var layoutChanges = 0;
var largeSpatialJumps = 0;
var conflictingDuplicateTimestamps = 0;
var zeroTimestamps = 0;
var lowBitCandidates = 0;
var falseClusterHits = 0;
var minimumComponentBits = int.MaxValue;
var maximumComponentBits = int.MinValue;
double maximumDistance = 0;
double maximumSpeed = 0;

Console.WriteLine("GPS diagnostic started. Columns: elapsed/event xyz distance speed timestamp payload offset bits endpoint");

try
{
    await foreach (var observation in source.WatchAsync(timeout.Token))
    {
        if (!observation.HasMovement)
        {
            continue;
        }

        observations++;
        var movement = observation.Movement;
        var distance = 0d;
        var speed = 0d;
        var layoutChanged = false;

        if (previous is { } prior)
        {
            var dx = movement.X - prior.Movement.X;
            var dy = movement.Y - prior.Movement.Y;
            var dz = movement.Z - prior.Movement.Z;
            distance = Math.Sqrt(dx * dx + dy * dy + dz * dz);
            var elapsedSeconds = Math.Max(
                0.000_001d,
                (observation.ObservedAt - prior.ObservedAt).TotalSeconds);
            speed = distance / elapsedSeconds;
            layoutChanged = movement.PayloadLength != prior.Movement.PayloadLength
                            || movement.LocationBitOffset != prior.Movement.LocationBitOffset
                            || movement.ComponentBitCount != prior.Movement.ComponentBitCount;
        }

        maximumDistance = Math.Max(maximumDistance, distance);
        maximumSpeed = Math.Max(maximumSpeed, speed);
        if (layoutChanged)
        {
            layoutChanges++;
        }

        var largeSpatialJump = distance >= 10_000d;
        var conflictingDuplicateTimestamp = previous is { } priorSample
                                            && Math.Abs(
                                                movement.ClientTimestamp
                                                - priorSample.Movement.ClientTimestamp) <= 0.002f
                                            && distance > 1d;
        var falseClusterHit = Math.Abs(movement.X) < 10_000d
                              && Math.Abs(movement.Y) < 10_000d;
        if (largeSpatialJump)
        {
            largeSpatialJumps++;
        }
        if (conflictingDuplicateTimestamp)
        {
            conflictingDuplicateTimestamps++;
        }
        if (movement.ClientTimestamp <= 0f)
        {
            zeroTimestamps++;
        }
        if (movement.ComponentBitCount < 23)
        {
            lowBitCandidates++;
        }
        if (falseClusterHit)
        {
            falseClusterHits++;
        }
        minimumComponentBits = Math.Min(minimumComponentBits, movement.ComponentBitCount);
        maximumComponentBits = Math.Max(maximumComponentBits, movement.ComponentBitCount);

        var now = stopwatch.Elapsed;
        var periodic = lastPeriodicOutput == TimeSpan.MinValue
                       || now - lastPeriodicOutput >= TimeSpan.FromSeconds(1);
        var critical = largeSpatialJump
                       || conflictingDuplicateTimestamp
                       || movement.ClientTimestamp <= 0f
                       || movement.ComponentBitCount < 23
                       || falseClusterHit;
        if (critical || periodic)
        {
            var eventName = largeSpatialJump
                ? "TELEPORT"
                : conflictingDuplicateTimestamp
                    ? "DUP-TS"
                    : critical
                        ? "INVALID"
                        : "SAMPLE";
            Console.WriteLine(
                $"{now.TotalSeconds,8:F3} {eventName,-6} " +
                $"({movement.X,11:F1},{movement.Y,11:F1},{movement.Z,9:F1}) " +
                $"d={distance,10:F1} v={speed,11:F1}/s " +
                $"ts={movement.ClientTimestamp,11:F3} " +
                $"len={movement.PayloadLength,4} off={movement.LocationBitOffset,4} bits={movement.ComponentBitCount,2} " +
                $"server={observation.ServerEndpoint}");
            lastPeriodicOutput = now;
        }

        previous = observation;
    }
}
catch (OperationCanceledException) when (timeout.IsCancellationRequested)
{
}

Console.WriteLine(
    $"SUMMARY observations={observations} layoutChanges={layoutChanges} " +
    $"largeSpatialJumps={largeSpatialJumps} duplicateTimestampMoves={conflictingDuplicateTimestamps} " +
    $"zeroTimestamps={zeroTimestamps} lowBitCandidates={lowBitCandidates} " +
    $"falseClusterHits={falseClusterHits} componentBits={minimumComponentBits}..{maximumComponentBits} " +
    $"maxDistance={maximumDistance:F1} maxObservedReadSpeed={maximumSpeed:F1}/s");
