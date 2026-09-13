using System.Text;
using TheIsleOverlay.LocalTelemetry;

if (args.Length is 1 or 2 && string.Equals(args[0], "live", StringComparison.OrdinalIgnoreCase))
{
    var seconds = args.Length == 2 ? int.Parse(args[1]) : 20;
    using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
    await using var source = new NpcapLocalMovementSource(enableLocalVitals: true);
    try
    {
        await foreach (var observation in source.WatchAsync(cancellation.Token))
        {
            var value = observation.DinosaurVitals?.Vitals;
            var movement = observation.HasMovement
                ? $"{observation.Movement.X:0.00},{observation.Movement.Y:0.00},{observation.Movement.Z:0.00}"
                : "—";
            Console.WriteLine(
                $"movementAt={(observation.HasMovement ? observation.ObservedAt.ToString("HH:mm:ss.fff") : "—")} " +
                $"vitalsAt={observation.DinosaurVitals?.ObservedAt:HH:mm:ss.fff} " +
                $"xyz={movement} " +
                $"growth={value?.Growth:R} " +
                $"hp={value?.Health:R}/{value?.MaxHealth:R} " +
                $"stamina={value?.Stamina:R}/{value?.MaxStamina:R} " +
                $"hunger={value?.Hunger:R}/{value?.MaxHunger:R} " +
                $"thirst={value?.Thirst:R}/{value?.MaxThirst:R}");
        }
    }
    catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
    {
    }

    var diagnostics = source.GetPipelineDiagnostics();
    var lanes = source.GetLaneDiagnostics();
    var vitalsDiagnostics = source.GetLocalVitalsDiagnostics();
    Console.Error.WriteLine(
        $"pipeline captured={diagnostics.CapturedPackets} " +
        $"processed={diagnostics.ProcessedPackets} " +
        $"queueDrops={diagnostics.QueueDroppedPackets} " +
        $"queueHigh={diagnostics.QueueHighWatermark} " +
        $"iris={diagnostics.IrisPackets} " +
        $"incomplete={diagnostics.IncompleteIrisPackets} " +
        $"sequenceGaps={diagnostics.SequenceGapPackets} " +
        $"reordered={diagnostics.ReorderedPackets} " +
        $"duplicates={diagnostics.DuplicatePackets}");
    Console.Error.WriteLine(
        $"lanes outboundCaptured={lanes.Outbound.CapturedPackets} " +
        $"outboundDrops={lanes.Outbound.QueueDroppedPackets} " +
        $"inboundCaptured={lanes.Inbound.CapturedPackets} " +
        $"inboundDrops={lanes.Inbound.QueueDroppedPackets} " +
        $"localVitals={vitalsDiagnostics.Enabled} " +
        $"publishedVitals={vitalsDiagnostics.PublishedObservations}");

    return 0;
}

if (args.Length == 4 && string.Equals(args[0], "cache", StringComparison.OrdinalIgnoreCase))
{
    SeedVerifiedCache(args[1], args[2], args[3]);
    return 0;
}

if (args.Length != 1)
{
    Console.Error.WriteLine("Usage: <ISLETR01 capture> | live [seconds]");
    return 2;
}

using var stream = File.OpenRead(Path.GetFullPath(args[0]));
using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: false);
if (Encoding.ASCII.GetString(reader.ReadBytes(8)) != "ISLETR01")
{
    throw new InvalidDataException("Unsupported capture.");
}

var tracker = new UnrealDinosaurVitalsTracker();
var sequence = 0;
while (stream.Position < stream.Length)
{
    var at = new DateTimeOffset(reader.ReadInt64(), TimeSpan.Zero);
    var direction = reader.ReadByte();
    _ = reader.ReadString();
    _ = reader.ReadUInt16();
    _ = reader.ReadString();
    _ = reader.ReadUInt16();
    var payload = reader.ReadBytes(reader.ReadInt32());
    sequence++;
    if (direction != 1 || !tracker.TryTrack(payload, at, out var observation))
    {
        continue;
    }

    var value = observation.Vitals;
    Console.WriteLine(
        $"{sequence,5} {at:HH:mm:ss.fff} handle={observation.NetRefHandle} " +
        $"growth={value.Growth:R} " +
        $"hp={value.Health:R}/{value.MaxHealth:R} " +
        $"stamina={value.Stamina:R}/{value.MaxStamina:R} " +
        $"hunger={value.Hunger:R}/{value.MaxHunger:R} " +
        $"thirst={value.Thirst:R}/{value.MaxThirst:R}");
}

return 0;

static void SeedVerifiedCache(
    string capturePath,
    string gameSessionId,
    string serverEndpoint)
{
    using var stream = File.OpenRead(Path.GetFullPath(capturePath));
    using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: false);
    if (Encoding.ASCII.GetString(reader.ReadBytes(8)) != "ISLETR01")
    {
        throw new InvalidDataException("Unsupported capture.");
    }

    var tracker = new UnrealDinosaurVitalsTracker();
    var cache = new LocalVitalsSessionCache();
    var saved = 0;
    while (stream.Position < stream.Length)
    {
        var at = new DateTimeOffset(reader.ReadInt64(), TimeSpan.Zero);
        var direction = reader.ReadByte();
        _ = reader.ReadString();
        _ = reader.ReadUInt16();
        _ = reader.ReadString();
        _ = reader.ReadUInt16();
        var payload = reader.ReadBytes(reader.ReadInt32());
        if (direction != 1
            || !tracker.TryTrack(payload, at, out var observation)
            || observation.Vitals.MaxHealth is not > 0d
            || observation.Vitals.MaxStamina is not > 0d
            || observation.Vitals.MaxHunger is not > 0d)
        {
            continue;
        }

        cache.Enrich(gameSessionId, serverEndpoint, observation);
        saved++;
    }

    Console.WriteLine($"Cached {saved} verified observations for {gameSessionId} {serverEndpoint}.");
}
