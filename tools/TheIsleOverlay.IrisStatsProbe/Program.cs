using System.Globalization;
using System.Text;
using TheIsleOverlay.LocalTelemetry;

const string Header = "ISLETR01";

if (args.Length == 3 && string.Equals(args[0], "mutation", StringComparison.OrdinalIgnoreCase))
{
    MutationCaptureAnalyzer.Run(
        Path.GetFullPath(args[1]),
        Path.GetFullPath(args[2]));
    return 0;
}

if (args.Length == 4 && string.Equals(args[0], "timeline", StringComparison.OrdinalIgnoreCase))
{
    MutationCaptureAnalyzer.Timeline(
        Path.GetFullPath(args[1]),
        args[2],
        args[3]);
    return 0;
}

if (args.Length == 3 && string.Equals(args[0], "exports", StringComparison.OrdinalIgnoreCase))
{
    MutationCaptureAnalyzer.Exports(Path.GetFullPath(args[1]), int.Parse(args[2], CultureInfo.InvariantCulture));
    return 0;
}

if (args.Length == 4 && string.Equals(args[0], "floats", StringComparison.OrdinalIgnoreCase))
{
    PrintFloats(
        ReadRecords(Path.GetFullPath(args[1])),
        new UnrealIrisPacketParser(),
        args[2],
        float.Parse(args[3].Split(':')[0], CultureInfo.InvariantCulture),
        float.Parse(args[3].Split(':')[1], CultureInfo.InvariantCulture));
    return 0;
}

if (args.Length == 2 && string.Equals(args[0], "discover", StringComparison.OrdinalIgnoreCase))
{
    DiscoverVitals(ReadRecords(Path.GetFullPath(args[1])), new UnrealIrisPacketParser());
    return 0;
}

if (args.Length == 8 && string.Equals(args[0], "growth", StringComparison.OrdinalIgnoreCase))
{
    CompareGrowth(
        Path.GetFullPath(args[1]),
        int.Parse(args[2], CultureInfo.InvariantCulture),
        double.Parse(args[3], CultureInfo.InvariantCulture),
        Path.GetFullPath(args[4]),
        int.Parse(args[5], CultureInfo.InvariantCulture),
        double.Parse(args[6], CultureInfo.InvariantCulture),
        ulong.Parse(args[7], CultureInfo.InvariantCulture));
    return 0;
}

if (args.Length != 2)
{
    Console.Error.WriteLine(
        "Usage: mutation <capture.bin> <markers.tsv> | timeline <capture.bin> <start> <end> | exports <capture.bin> <record> | discover <capture.bin> | " +
        "floats <capture.bin> <sequence|HH:mm:ss.fff> <min:max> | " +
        "<capture.bin> <sequence|HH:mm:ss.fff|track:handle> | " +
        "growth <capture1> <sequence1> <value1> <capture2> <sequence2> <value2> <handle>");
    return 2;
}

var records = ReadRecords(Path.GetFullPath(args[0]));
var parser = new UnrealIrisPacketParser();
if (args[1].StartsWith("track:", StringComparison.OrdinalIgnoreCase)
    && ulong.TryParse(args[1][6..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var trackedHandle))
{
    TrackHandle(records, parser, trackedHandle);
    return 0;
}

var selected = Select(records, args[1]);
foreach (var record in selected)
{
    Console.WriteLine(
        $"record={record.Sequence} utc={record.ObservedAt:HH:mm:ss.fff} " +
        $"direction={record.Direction} bytes={record.Payload.Length}");
    if (!parser.TryParse(record.Payload, out var packet))
    {
        Console.WriteLine("  not-an-iris-packet");
        continue;
    }

    Console.WriteLine(
        $"  iris sequence={packet.PacketSequence} stream={packet.HasDataStream} " +
        $"complete={packet.IsComplete} reason={packet.IncompleteReason ?? "-"} batches={packet.Batches.Count}");
    foreach (var batch in packet.Batches)
    {
        var pairs = FindPairs(record.Payload, batch.DataBitOffset, batch.DataBitCount);
        Console.WriteLine(
            $"  handle={batch.NetRefHandle} bits={batch.DataBitOffset}+{batch.DataBitCount} " +
            $"owner={batch.HasOwnerData} exports={batch.HasExports} pairs={pairs.Count}");
        foreach (var pair in pairs)
        {
            Console.WriteLine(
                $"    absolute={pair.AbsoluteBitOffset,5} relative={pair.RelativeBitOffset,5} " +
                $"separator={Convert.ToInt32(pair.SeparatorBit)} value={pair.Value,12:R}");
        }
    }
}

return 0;

static void PrintFloats(
    IReadOnlyList<TrafficRecord> records,
    UnrealIrisPacketParser parser,
    string selector,
    float minimum,
    float maximum)
{
    foreach (var record in Select(records, selector))
    {
        Console.WriteLine($"record={record.Sequence} utc={record.ObservedAt:HH:mm:ss.fff} direction={record.Direction}");
        if (!parser.TryParse(record.Payload, out var packet))
        {
            continue;
        }

        foreach (var batch in packet.Batches)
        {
            var values = new List<string>();
            for (var relative = 0; relative + 32 <= batch.DataBitCount; relative++)
            {
                var value = ReadFloat(record.Payload, batch.DataBitOffset + relative);
                if (float.IsFinite(value) && value >= minimum && value <= maximum)
                {
                    values.Add($"r{relative}={value:R}");
                }
            }

            if (values.Count > 0)
            {
                Console.WriteLine($"  handle={batch.NetRefHandle} bits={batch.DataBitCount} {string.Join(" | ", values)}");
            }
        }
    }
}

static void DiscoverVitals(
    IReadOnlyList<TrafficRecord> records,
    UnrealIrisPacketParser parser)
{
    foreach (var record in records.Where(record => record.Direction == TrafficDirection.Inbound))
    {
        if (!parser.TryParse(record.Payload, out var packet))
        {
            continue;
        }

        foreach (var batch in packet.Batches)
        {
            var pairs = FindPairs(record.Payload, batch.DataBitOffset, batch.DataBitCount)
                .Where(pair => pair.SeparatorBit)
                .ToArray();
            foreach (var first in pairs)
            {
                var second = pairs.FirstOrDefault(pair =>
                    pair.RelativeBitOffset == first.RelativeBitOffset + 66);
                var third = pairs.FirstOrDefault(pair =>
                    pair.RelativeBitOffset == first.RelativeBitOffset + 132);
                if (second is null
                    || third is null
                    || Math.Abs(first.Value - third.Value) > 0.01f
                    || second.Value < first.Value * 2f)
                {
                    continue;
                }

                Console.WriteLine(
                    $"utc={record.ObservedAt:HH:mm:ss.fff} record={record.Sequence} " +
                    $"handle={batch.NetRefHandle} bits={batch.DataBitCount} r={first.RelativeBitOffset} " +
                    $"first={first.Value:R} middle={second.Value:R} third={third.Value:R}");
            }
        }
    }
}

static void CompareGrowth(
    string firstCapture,
    int firstSequence,
    double firstGrowth,
    string secondCapture,
    int secondSequence,
    double secondGrowth,
    ulong handle)
{
    var parser = new UnrealIrisPacketParser();
    var first = FindBatch(ReadRecords(firstCapture), firstSequence, parser, handle);
    var second = FindBatch(ReadRecords(secondCapture), secondSequence, parser, handle);
    var sharedBits = Math.Min(first.Batch.DataBitCount, second.Batch.DataBitCount);
    var matches = new List<GrowthEncodingMatch>();

    for (var relative = 0; relative < sharedBits; relative++)
    {
        if (relative + 32 <= sharedBits)
        {
            var firstFloat = ReadFloat(first.Record.Payload, first.Batch.DataBitOffset + relative);
            var secondFloat = ReadFloat(second.Record.Payload, second.Batch.DataBitOffset + relative);
            if (float.IsFinite(firstFloat) && float.IsFinite(secondFloat))
            {
                var error = Math.Abs(firstFloat - firstGrowth) + Math.Abs(secondFloat - secondGrowth);
                if (error < 0.02)
                {
                    matches.Add(new GrowthEncodingMatch(relative, "float32", firstFloat, secondFloat, error));
                }
            }
        }

        for (var bitCount = 8; bitCount <= 24 && relative + bitCount <= sharedBits; bitCount++)
        {
            var maximum = (1UL << bitCount) - 1UL;
            var firstRaw = ReadBits(first.Record.Payload, first.Batch.DataBitOffset + relative, bitCount);
            var secondRaw = ReadBits(second.Record.Payload, second.Batch.DataBitOffset + relative, bitCount);
            var firstNormalized = (double)firstRaw / maximum;
            var secondNormalized = (double)secondRaw / maximum;
            var normalizedError = Math.Abs(firstNormalized - firstGrowth)
                                  + Math.Abs(secondNormalized - secondGrowth);
            if (normalizedError < 0.002)
            {
                matches.Add(new GrowthEncodingMatch(
                    relative,
                    $"uint{bitCount}/max",
                    firstNormalized,
                    secondNormalized,
                    normalizedError));
            }

            foreach (var scale in new[] { 1000d, 10_000d, 100_000d })
            {
                var firstScaled = firstRaw / scale;
                var secondScaled = secondRaw / scale;
                var scaledError = Math.Abs(firstScaled - firstGrowth)
                                  + Math.Abs(secondScaled - secondGrowth);
                if (scaledError < 0.002)
                {
                    matches.Add(new GrowthEncodingMatch(
                        relative,
                        $"uint{bitCount}/{scale:R}",
                        firstScaled,
                        secondScaled,
                        scaledError));
                }
            }
        }
    }

    Console.WriteLine(
        $"first={firstSequence}:{first.Batch.DataBitCount} growth={firstGrowth:R} " +
        $"second={secondSequence}:{second.Batch.DataBitCount} growth={secondGrowth:R} handle={handle}");
    foreach (var match in matches.OrderBy(match => match.Error).Take(100))
    {
        Console.WriteLine(
            $"r{match.RelativeBitOffset,-5} {match.Encoding,-15} " +
            $"first={match.FirstValue:R} second={match.SecondValue:R} error={match.Error:R}");
    }
}

static BatchRecord FindBatch(
    IReadOnlyList<TrafficRecord> records,
    int sequence,
    UnrealIrisPacketParser parser,
    ulong handle)
{
    var record = records.Single(record => record.Sequence == sequence);
    if (!parser.TryParse(record.Payload, out var packet))
    {
        throw new InvalidDataException($"Record {sequence} is not a complete Iris packet.");
    }

    var batch = packet.Batches.Single(batch => batch.NetRefHandle == handle);
    return new BatchRecord(record, batch);
}

static void TrackHandle(
    IReadOnlyList<TrafficRecord> records,
    UnrealIrisPacketParser parser,
    ulong trackedHandle)
{
    var exports = new ProbeIrisObjectExportTracker();
    var creations = new ProbeUnrealIrisActorCreationScanner();
    foreach (var record in records.Where(record => record.Direction == TrafficDirection.Inbound))
    {
        if (!parser.TryParse(record.Payload, out var packet))
        {
            continue;
        }

        foreach (var batch in packet.Batches)
        {
            foreach (var exported in exports.Observe(record.Payload, batch))
            {
                if (exported.NetRefHandle == trackedHandle
                    || exported.OuterNetRefHandle == trackedHandle
                    || exported.FullPath.Contains(trackedHandle.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal))
                {
                    Console.WriteLine(
                        $"export utc={record.ObservedAt:HH:mm:ss.fff} " +
                        $"handle={exported.NetRefHandle} outer={exported.OuterNetRefHandle} " +
                        $"path={exported.FullPath}");
                }
            }

            if (batch.NetRefHandle == trackedHandle)
            {
                var pairs = FindPairs(record.Payload, batch.DataBitOffset, batch.DataBitCount)
                    .Where(pair => pair.SeparatorBit)
                    .ToArray();
                var creationText = creations.TryRead(record.Payload, batch, out var creation)
                    ? $" creationProtocol={creation.ProtocolId} archetype={creation.ArchetypeNetRefHandle}"
                    : string.Empty;
                var objectText = exports.TryGetObject(trackedHandle, out var reference)
                    ? $" object={reference.FullPath}"
                    : string.Empty;
                if (pairs.Length > 0 || creationText.Length > 0 || objectText.Length > 0)
                {
                    Console.WriteLine(
                        $"batch utc={record.ObservedAt:HH:mm:ss.fff} seq={record.Sequence} " +
                        $"bits={batch.DataBitCount} owner={batch.HasOwnerData} exports={batch.HasExports}" +
                        creationText + objectText);
                    Console.WriteLine(
                        "  " + string.Join(
                            " | ",
                            pairs.Select(pair => $"r{pair.RelativeBitOffset}={pair.Value:R}")));
                }
            }
        }
    }

    if (exports.TryGetObject(trackedHandle, out var finalReference))
    {
        Console.WriteLine($"final handle={trackedHandle} object={finalReference.FullPath}");
    }
    else
    {
        Console.WriteLine($"final handle={trackedHandle} object=<unresolved>");
    }
}

static IReadOnlyList<TrafficRecord> Select(IReadOnlyList<TrafficRecord> records, string selector)
{
    if (int.TryParse(selector, NumberStyles.Integer, CultureInfo.InvariantCulture, out var sequence))
    {
        return records.Where(record => record.Sequence == sequence).ToArray();
    }

    if (!TimeSpan.TryParseExact(
            selector,
            ["hh\\:mm\\:ss\\.fff", "h\\:mm\\:ss\\.fff"],
            CultureInfo.InvariantCulture,
            out var target))
    {
        throw new ArgumentException($"Invalid selector '{selector}'.");
    }

    var nearest = records.MinBy(record =>
        Math.Abs(record.ObservedAt.TimeOfDay.TotalMilliseconds - target.TotalMilliseconds));
    return nearest is null ? [] : [nearest];
}

static IReadOnlyList<AttributePair> FindPairs(byte[] payload, int start, int count)
{
    var pairs = new List<AttributePair>();
    var end = Math.Min(payload.Length * 8, start + count);
    for (var absolute = start; absolute + 65 <= end; absolute++)
    {
        var first = ReadFloat(payload, absolute);
        var second = ReadFloat(payload, absolute + 33);
        if (!IsPlausible(first)
            || !IsPlausible(second)
            || Math.Abs(first - second) > Math.Max(0.0001f, Math.Abs(first) * 0.000001f))
        {
            continue;
        }

        pairs.Add(new AttributePair(
            absolute,
            absolute - start,
            first,
            IsBitSet(payload, absolute + 32)));
    }

    return pairs;
}

static IReadOnlyList<TrafficRecord> ReadRecords(string capturePath)
{
    using var stream = File.OpenRead(capturePath);
    using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: false);
    var header = Encoding.ASCII.GetString(reader.ReadBytes(8));
    if (!string.Equals(header, Header, StringComparison.Ordinal))
    {
        throw new InvalidDataException($"Unsupported capture header: {header}");
    }

    var records = new List<TrafficRecord>();
    while (stream.Position < stream.Length)
    {
        var observedAt = new DateTimeOffset(reader.ReadInt64(), TimeSpan.Zero);
        var direction = (TrafficDirection)reader.ReadByte();
        _ = reader.ReadString();
        _ = reader.ReadUInt16();
        _ = reader.ReadString();
        _ = reader.ReadUInt16();
        var length = reader.ReadInt32();
        var payload = reader.ReadBytes(length);
        records.Add(new TrafficRecord(records.Count + 1, observedAt, direction, payload));
    }

    return records;
}

static float ReadFloat(ReadOnlySpan<byte> payload, int bitOffset) =>
    BitConverter.Int32BitsToSingle((int)(uint)ReadBits(payload, bitOffset, 32));

static ulong ReadBits(ReadOnlySpan<byte> payload, int bitOffset, int bitCount)
{
    ulong result = 0;
    for (var bit = 0; bit < bitCount; bit++)
    {
        var sourceBit = bitOffset + bit;
        if ((payload[sourceBit >> 3] & (1 << (sourceBit & 7))) != 0)
        {
            result |= 1UL << bit;
        }
    }

    return result;
}

static bool IsBitSet(ReadOnlySpan<byte> payload, int bitOffset) =>
    (payload[bitOffset >> 3] & (1 << (bitOffset & 7))) != 0;

static bool IsPlausible(float value) =>
    float.IsFinite(value) && value >= 0.01f && value <= 100_000f;

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
    byte[] Payload);

sealed record AttributePair(
    int AbsoluteBitOffset,
    int RelativeBitOffset,
    float Value,
    bool SeparatorBit);

sealed record BatchRecord(TrafficRecord Record, UnrealIrisReplicationBatch Batch);

sealed record GrowthEncodingMatch(
    int RelativeBitOffset,
    string Encoding,
    double FirstValue,
    double SecondValue,
    double Error);
