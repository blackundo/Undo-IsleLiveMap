using TheIsleOverlay.Core;
using TheIsleOverlay.ProClient;

var durationSeconds = args.Length > 0 && int.TryParse(args[0], out var requested)
    ? Math.Clamp(requested, 5, 1_800)
    : 30;
var hostVersion = args.Length > 1 && !string.IsNullOrWhiteSpace(args[1])
    ? args[1]
    : "1.4.6";
var outputPath = args.Length > 2 && !string.IsNullOrWhiteSpace(args[2])
    ? Path.GetFullPath(args[2])
    : null;
using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(durationSeconds));
using var accessService = new ProAccessService();
var access = await accessService.InitializeAsync(hostVersion, timeout.Token);
Console.WriteLine(
    $"ACCESS auth={access.IsAuthenticated} pro={access.IsPro} agent={access.AgentReady} "
    + $"agentVersion={access.AgentVersion ?? "-"} status={access.StatusCode ?? "ok"} host={hostVersion}");
await using var source = accessService.CreateRemotePlayerSource();
if (source is null)
{
    Console.WriteLine(
        $"UNAVAILABLE auth={access.IsAuthenticated} pro={access.IsPro} "
        + $"agent={access.AgentReady} status={access.StatusCode}");
    return;
}

var startedAt = DateTimeOffset.UtcNow;
var lastPrintedAt = DateTimeOffset.MinValue;
var frames = 0;
var playerCounts = new List<int>();
var aiCounts = new List<int>();
StreamWriter? output = null;
if (outputPath is not null)
{
    Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
    output = new StreamWriter(
        new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.Read),
        new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
    {
        AutoFlush = true
    };
}

try
{
    await foreach (var frame in source.WatchAsync(timeout.Token))
    {
        frames++;
        var players = frame.RemoteEntities
            .Where(item => item.Kind == RemoteEntityKind.Player)
            .OrderBy(item => item.DistanceFromLocal)
            .ToArray();
        var ai = frame.RemoteEntities
            .Where(item => item.Kind == RemoteEntityKind.Ai)
            .OrderBy(item => item.DistanceFromLocal)
            .ToArray();
        playerCounts.Add(players.Length);
        aiCounts.Add(ai.Length);
        if (output is not null)
        {
            var entry = new
            {
                ReceivedAt = DateTimeOffset.UtcNow,
                frame.Sequence,
                frame.ObservedAt,
                frame.ServerEndpoint,
                frame.LocalLocation,
                frame.MapHeadingDegrees,
                frame.LocalSpeciesId,
                frame.LocalSpeciesShortName,
                frame.PlayerSync,
                Entities = frame.RemoteEntities.Select(item => new
                {
                    item.TrackId,
                    Kind = item.Kind.ToString(),
                    item.PlayerProofName,
                    item.SpeciesId,
                    item.SpeciesShortName,
                    Diet = item.Diet.ToString(),
                    item.MassKg,
                    item.Location,
                    item.DistanceFromLocal,
                    item.ConfirmationHits,
                    item.ObservedAt,
                    item.IsProvisional
                })
            };
            await output.WriteLineAsync(System.Text.Json.JsonSerializer.Serialize(entry));
        }

        var now = DateTimeOffset.UtcNow;
        if (now - lastPrintedAt < TimeSpan.FromSeconds(1))
        {
            continue;
        }

        var species = players
            .GroupBy(item => string.IsNullOrWhiteSpace(item.SpeciesShortName)
                ? "Player?"
                : item.SpeciesShortName)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => $"{group.Key}:{group.Count()}");
        var nearest = players.Take(12).Select(item =>
            $"#{item.TrackId}:"
            + $"{(string.IsNullOrWhiteSpace(item.SpeciesShortName) ? "Player?" : item.SpeciesShortName)}"
            + $"@{item.DistanceFromLocal / 100:F0}m"
            + $"/{(item.IsProvisional ? "P" : "V")}{item.ConfirmationHits}");
        var sync = frame.PlayerSync is null
            ? "sync=-"
            : $"sync={(frame.PlayerSync.IsSynchronizing ? 1 : 0)}"
              + $" verified={frame.PlayerSync.VerifiedPlayers} provisional={frame.PlayerSync.ProvisionalPlayers}"
              + $" candidates={frame.PlayerSync.CandidateActors} speciesEvidence={frame.PlayerSync.SpeciesEvidenceActors}"
              + $" located={frame.PlayerSync.LocatedActors} dropped={frame.PlayerSync.QueueDroppedPackets}"
              + $" depth={frame.PlayerSync.QueueDepth}";
        Console.WriteLine(
            $"{now:HH:mm:ss.fff} endpoint={frame.ServerEndpoint ?? "—"} "
            + $"local=({frame.LocalLocation.X:F0},{frame.LocalLocation.Y:F0},{frame.LocalLocation.Z:F0}) "
            + $"players={players.Length} ai={ai.Length} "
            + $"{sync} species=[{string.Join(',', species)}] nearest=[{string.Join(',', nearest)}]");
        lastPrintedAt = now;
    }
}
catch (OperationCanceledException) when (timeout.IsCancellationRequested)
{
}
catch (Exception exception)
{
    Console.WriteLine($"ERROR {exception}");
}
finally
{
    if (output is not null)
    {
        await output.DisposeAsync();
    }
}

Console.WriteLine(
    $"SUMMARY duration={(DateTimeOffset.UtcNow - startedAt).TotalSeconds:F1}s frames={frames} "
    + $"playerMin={(playerCounts.Count == 0 ? 0 : playerCounts.Min())} "
    + $"playerMax={(playerCounts.Count == 0 ? 0 : playerCounts.Max())} "
    + $"playerLast={(playerCounts.Count == 0 ? 0 : playerCounts[^1])} "
    + $"aiMin={(aiCounts.Count == 0 ? 0 : aiCounts.Min())} "
    + $"aiMax={(aiCounts.Count == 0 ? 0 : aiCounts.Max())}");
