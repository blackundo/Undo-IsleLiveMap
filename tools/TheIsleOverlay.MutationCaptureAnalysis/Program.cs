using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using TheIsleOverlay.LocalTelemetry;

const string CaptureHeader = "ISLETR01";

if (args.Length != 2)
{
    Console.Error.WriteLine("Usage: <capture.bin> <markers.tsv>");
    return 2;
}

var records = ReadRecords(Path.GetFullPath(args[0]));
var markers = ReadMarkers(Path.GetFullPath(args[1]));
var parser = new UnrealIrisPacketParser();
var analyzed = records.Select(record => Analyze(record, parser)).ToArray();

Console.WriteLine(
    $"records={records.Count:N0} duration={(records[^1].ObservedAt - records[0].ObservedAt).TotalSeconds:F3}s " +
    $"inbound={records.Count(record => record.Direction == TrafficDirection.Inbound):N0} " +
    $"outbound={records.Count(record => record.Direction == TrafficDirection.Outbound):N0} " +
    $"iris={analyzed.Count(record => record.Iris is not null):N0}");
Console.WriteLine($"capture={records[0].ObservedAt:o}..{records[^1].ObservedAt:o}");

PrintTopLengths(analyzed, "overall", analyzed);

foreach (var marker in markers)
{
    var before = analyzed.Where(record => InWindow(record, marker.ObservedAt, -5, 0)).ToArray();
    var after = analyzed.Where(record => InWindow(record, marker.ObservedAt, 0, 5)).ToArray();
    Console.WriteLine(
        $"marker={marker.Name,-30} utc={marker.ObservedAt:HH:mm:ss.fff} " +
        $"before={Counts(before)} after={Counts(after)}");
    PrintRareLengths(analyzed, after, marker.Name);
    PrintIrisEvents(after, marker.Name);
}

var phases = BuildPhases(markers, analyzed);
foreach (var phase in phases)
{
    Console.WriteLine(
        $"phase={phase.Name,-30} {phase.Start:HH:mm:ss.fff}..{phase.End:HH:mm:ss.fff} " +
        $"seconds={(phase.End - phase.Start).TotalSeconds:F3} {Counts(phase.Records)}");
    PrintTopLengths(phase.Records, phase.Name, phase.Records);
    PrintTopIrisShapes(phase.Records, phase.Name);
}

PrintOpenCloseDiscriminators(markers, analyzed);
PrintSpendCandidates(markers, analyzed);
return 0;

static void PrintTopLengths(
    IReadOnlyList<AnalyzedRecord> universe,
    string label,
    IReadOnlyList<AnalyzedRecord> selected)
{
    foreach (var direction in new[] { TrafficDirection.Inbound, TrafficDirection.Outbound })
    {
        var rows = selected
            .Where(record => record.Record.Direction == direction)
            .GroupBy(record => record.Record.Payload.Length)
            .OrderByDescending(group => group.Count())
            .ThenBy(group => group.Key)
            .Take(12)
            .Select(group => $"{group.Key}:{group.Count()}");
        Console.WriteLine($"  lengths[{label}][{direction}]={string.Join(',', rows)}");
    }
}

static void PrintRareLengths(
    IReadOnlyList<AnalyzedRecord> universe,
    IReadOnlyList<AnalyzedRecord> selected,
    string label)
{
    var global = universe
        .GroupBy(record => (record.Record.Direction, record.Record.Payload.Length))
        .ToDictionary(group => group.Key, group => group.Count());
    var rare = selected
        .GroupBy(record => (record.Record.Direction, record.Record.Payload.Length))
        .Select(group => new
        {
            group.Key.Direction,
            group.Key.Length,
            WindowCount = group.Count(),
            GlobalCount = global[group.Key]
        })
        .Where(row => row.GlobalCount <= 12)
        .OrderBy(row => row.GlobalCount)
        .ThenBy(row => row.Direction)
        .ThenBy(row => row.Length)
        .Take(20)
        .Select(row => $"{row.Direction}:{row.Length}:{row.WindowCount}/{row.GlobalCount}")
        .ToArray();
    if (rare.Length > 0)
    {
        Console.WriteLine($"  rare-lengths[{label}]={string.Join(',', rare)}");
    }
}

static void PrintIrisEvents(IReadOnlyList<AnalyzedRecord> selected, string label)
{
    var rows = selected
        .Where(record => record.Iris is not null && record.Iris.Value.Batches.Count > 0)
        .GroupBy(record => record.IrisShape)
        .OrderByDescending(group => group.Count())
        .Take(12)
        .Select(group => $"{group.Count()}x {group.Key}")
        .ToArray();
    if (rows.Length > 0)
    {
        Console.WriteLine($"  iris[{label}]={string.Join(" | ", rows)}");
    }
}

static void PrintTopIrisShapes(IReadOnlyList<AnalyzedRecord> selected, string label)
{
    var rows = selected
        .Where(record => record.Iris is not null && record.Iris.Value.Batches.Count > 0)
        .GroupBy(record => record.IrisShape)
        .OrderByDescending(group => group.Count())
        .Take(10)
        .Select(group => $"{group.Count()}x {group.Key}")
        .ToArray();
    if (rows.Length > 0)
    {
        Console.WriteLine($"  iris-shapes[{label}]={string.Join(" | ", rows)}");
    }
}

static void PrintOpenCloseDiscriminators(
    IReadOnlyList<Marker> markers,
    IReadOnlyList<AnalyzedRecord> records)
{
    var openStarts = markers
        .Where(marker => marker.Name.StartsWith("OPEN_MUTATION_TAB", StringComparison.Ordinal))
        .Select(marker => marker.ObservedAt)
        .ToArray();
    var closeStarts = markers
        .Where(marker => marker.Name.StartsWith("CLOSE_MUTATION_TAB", StringComparison.Ordinal))
        .Select(marker => marker.ObservedAt)
        .ToArray();
    var controls = markers
        .Where(marker => marker.Name.StartsWith("NEGATIVE_", StringComparison.Ordinal)
                         && marker.Name.EndsWith("BEGIN", StringComparison.Ordinal))
        .Select(marker => marker.ObservedAt)
        .ToArray();

    Console.WriteLine(
        $"discriminator-windows opens={openStarts.Length} closes={closeStarts.Length} controls={controls.Length}");
    PrintDiscriminators("length", openStarts, closeStarts, controls, records, record =>
        $"{record.Record.Direction}:{record.Record.Payload.Length}");
    PrintDiscriminators("iris", openStarts, closeStarts, controls, records, record => record.IrisShape);
    PrintDiscriminators("prefix", openStarts, closeStarts, controls, records, record =>
        $"{record.Record.Direction}:{record.Record.Payload.Length}:{record.PrefixHash}");
}

static void PrintDiscriminators(
    string label,
    IReadOnlyList<DateTimeOffset> opens,
    IReadOnlyList<DateTimeOffset> closes,
    IReadOnlyList<DateTimeOffset> controls,
    IReadOnlyList<AnalyzedRecord> records,
    Func<AnalyzedRecord, string> selector)
{
    var openSets = opens.Select(start => records
        .Where(record => InWindow(record, start, 0, 20))
        .Select(selector)
        .ToHashSet(StringComparer.Ordinal)).ToArray();
    var closeSets = closes.Select(start => records
        .Where(record => InWindow(record, start, 0, 20))
        .Select(selector)
        .ToHashSet(StringComparer.Ordinal)).ToArray();
    var controlSet = controls.SelectMany(start => records
        .Where(record => InWindow(record, start, 0, 30))
        .Select(selector)).ToHashSet(StringComparer.Ordinal);
    var candidates = openSets
        .SelectMany(set => set)
        .Distinct(StringComparer.Ordinal)
        .Select(value => new
        {
            Value = value,
            OpenHits = openSets.Count(set => set.Contains(value)),
            CloseHits = closeSets.Count(set => set.Contains(value)),
            ControlHit = controlSet.Contains(value)
        })
        .Where(row => row.OpenHits >= Math.Max(2, openSets.Length - 1)
                      && row.CloseHits == 0
                      && !row.ControlHit)
        .OrderByDescending(row => row.OpenHits)
        .ThenBy(row => row.Value, StringComparer.Ordinal)
        .Take(30)
        .ToArray();
    Console.WriteLine($"  discriminators[{label}]={candidates.Length}");
    foreach (var candidate in candidates)
    {
        Console.WriteLine(
            $"    opens={candidate.OpenHits}/{opens.Count} closes={candidate.CloseHits}/{closes.Count} " +
            $"control={Convert.ToInt32(candidate.ControlHit)} {candidate.Value}");
    }
}

static void PrintSpendCandidates(
    IReadOnlyList<Marker> markers,
    IReadOnlyList<AnalyzedRecord> records)
{
    var before = markers.Single(marker => marker.Name == "BEFORE_SPEND").ObservedAt;
    var after = markers.Single(marker => marker.Name == "AFTER_SPEND").ObservedAt;
    var interval = records.Where(record =>
        record.Record.ObservedAt >= before && record.Record.ObservedAt <= after).ToArray();
    var baselineStart = markers.Single(marker => marker.Name == "BASELINE_START").ObservedAt;
    var baseline = records.Where(record =>
        record.Record.ObservedAt >= baselineStart
        && record.Record.ObservedAt < baselineStart + TimeSpan.FromSeconds(60)).ToArray();
    var baselineLengths = baseline
        .Select(record => (record.Record.Direction, record.Record.Payload.Length))
        .ToHashSet();
    Console.WriteLine(
        $"spend-window={before:HH:mm:ss.fff}..{after:HH:mm:ss.fff} " +
        $"seconds={(after - before).TotalSeconds:F3} {Counts(interval)}");

    var novelLengths = interval
        .Where(record => !baselineLengths.Contains((record.Record.Direction, record.Record.Payload.Length)))
        .GroupBy(record => (record.Record.Direction, record.Record.Payload.Length))
        .OrderBy(group => group.Min(record => record.Record.ObservedAt))
        .Select(group => new
        {
            group.Key.Direction,
            group.Key.Length,
            Count = group.Count(),
            First = group.Min(record => record.Record.ObservedAt),
            Sequences = string.Join(',', group.Take(5).Select(record => record.Record.Sequence))
        })
        .Take(50)
        .ToArray();
    foreach (var row in novelLengths)
    {
        Console.WriteLine(
            $"  spend-novel-length {row.First:HH:mm:ss.fff} {row.Direction} bytes={row.Length} " +
            $"count={row.Count} records={row.Sequences}");
    }

    var globalShapeCounts = records
        .Where(record => record.Iris is not null)
        .GroupBy(record => record.IrisShape)
        .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
    var rareShapes = interval
        .Where(record => record.Iris is not null)
        .GroupBy(record => record.IrisShape)
        .Select(group => new
        {
            Shape = group.Key,
            WindowCount = group.Count(),
            GlobalCount = globalShapeCounts[group.Key],
            First = group.Min(record => record.Record.ObservedAt),
            Records = string.Join(',', group.Take(5).Select(record => record.Record.Sequence))
        })
        .Where(row => row.GlobalCount <= 10)
        .OrderBy(row => row.First)
        .Take(80)
        .ToArray();
    foreach (var row in rareShapes)
    {
        Console.WriteLine(
            $"  spend-rare-iris {row.First:HH:mm:ss.fff} window={row.WindowCount} global={row.GlobalCount} " +
            $"records={row.Records} {row.Shape}");
    }
}

static IReadOnlyList<Phase> BuildPhases(
    IReadOnlyList<Marker> markers,
    IReadOnlyList<AnalyzedRecord> records)
{
    var phases = new List<Phase>();
    for (var index = 0; index + 1 < markers.Count; index++)
    {
        var start = markers[index];
        var end = markers[index + 1];
        if (end.ObservedAt <= start.ObservedAt)
        {
            continue;
        }

        phases.Add(new Phase(
            start.Name,
            start.ObservedAt,
            end.ObservedAt,
            records.Where(record => record.Record.ObservedAt >= start.ObservedAt
                                    && record.Record.ObservedAt < end.ObservedAt).ToArray()));
    }

    return phases;
}

static AnalyzedRecord Analyze(TrafficRecord record, UnrealIrisPacketParser parser)
{
    UnrealIrisPacket? iris = parser.TryParse(record.Payload, out var parsed) ? parsed : null;
    var shape = iris is null
        ? "not-iris"
        : $"{record.Direction}:bytes={record.Payload.Length}:stream={Convert.ToInt32(iris.Value.HasDataStream)}:" +
          $"complete={Convert.ToInt32(iris.Value.IsComplete)}:batches={iris.Value.Batches.Count}:" +
          string.Join(',', iris.Value.Batches.Select(batch =>
              $"{batch.NetRefHandle}/{batch.DataBitCount}/{Convert.ToInt32(batch.HasOwnerData)}/{Convert.ToInt32(batch.HasExports)}"));
    var prefixLength = Math.Min(16, record.Payload.Length);
    var prefixHash = Convert.ToHexString(SHA256.HashData(record.Payload.AsSpan(0, prefixLength)))[..12];
    return new AnalyzedRecord(record, iris, shape, prefixHash);
}

static string Counts(IReadOnlyList<AnalyzedRecord> records) =>
    $"total={records.Count} in={records.Count(record => record.Record.Direction == TrafficDirection.Inbound)} " +
    $"out={records.Count(record => record.Record.Direction == TrafficDirection.Outbound)} " +
    $"iris={records.Count(record => record.Iris is not null)}";

static bool InWindow(AnalyzedRecord record, DateTimeOffset center, double startSeconds, double endSeconds) =>
    record.Record.ObservedAt >= center + TimeSpan.FromSeconds(startSeconds)
    && record.Record.ObservedAt < center + TimeSpan.FromSeconds(endSeconds);

static IReadOnlyList<Marker> ReadMarkers(string markerPath)
{
    var markers = new List<Marker>();
    foreach (var line in File.ReadLines(markerPath))
    {
        if (string.IsNullOrWhiteSpace(line) || line.StartsWith('#'))
        {
            continue;
        }

        var fields = line.Split('\t');
        if (fields.Length >= 2
            && DateTimeOffset.TryParse(
                fields[0],
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var observedAt))
        {
            markers.Add(new Marker(observedAt, fields[1], fields.Length > 2 ? fields[2] : string.Empty));
        }
    }

    return markers.OrderBy(marker => marker.ObservedAt).ToArray();
}

static IReadOnlyList<TrafficRecord> ReadRecords(string capturePath)
{
    using var stream = File.OpenRead(capturePath);
    using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: false);
    var header = Encoding.ASCII.GetString(reader.ReadBytes(8));
    if (!string.Equals(header, CaptureHeader, StringComparison.Ordinal))
    {
        throw new InvalidDataException($"Unsupported capture header: {header}");
    }

    var records = new List<TrafficRecord>();
    while (stream.Position < stream.Length)
    {
        var observedAt = new DateTimeOffset(reader.ReadInt64(), TimeSpan.Zero);
        var direction = (TrafficDirection)reader.ReadByte();
        var sourceAddress = reader.ReadString();
        var sourcePort = reader.ReadUInt16();
        var destinationAddress = reader.ReadString();
        var destinationPort = reader.ReadUInt16();
        var length = reader.ReadInt32();
        if (length is <= 0 or > 65_535)
        {
            throw new InvalidDataException($"Invalid payload length: {length}");
        }

        var payload = reader.ReadBytes(length);
        if (payload.Length != length)
        {
            throw new EndOfStreamException("Incomplete final traffic record.");
        }

        records.Add(new TrafficRecord(
            records.Count + 1,
            observedAt,
            direction,
            sourceAddress,
            sourcePort,
            destinationAddress,
            destinationPort,
            payload));
    }

    if (records.Count == 0)
    {
        throw new InvalidDataException("Capture is empty.");
    }

    return records;
}

enum TrafficDirection : byte
{
    Unknown,
    Inbound,
    Outbound
}

sealed record TrafficRecord(
    int Sequence,
    DateTimeOffset ObservedAt,
    TrafficDirection Direction,
    string SourceAddress,
    int SourcePort,
    string DestinationAddress,
    int DestinationPort,
    byte[] Payload);

sealed record AnalyzedRecord(
    TrafficRecord Record,
    UnrealIrisPacket? Iris,
    string IrisShape,
    string PrefixHash);

sealed record Marker(DateTimeOffset ObservedAt, string Name, string Note);
sealed record Phase(
    string Name,
    DateTimeOffset Start,
    DateTimeOffset End,
    IReadOnlyList<AnalyzedRecord> Records);
