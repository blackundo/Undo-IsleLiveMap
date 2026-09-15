using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using TheIsleOverlay.LocalTelemetry;

internal static class MutationCaptureAnalyzer
{
    private const string CaptureHeader = "ISLETR01";
    private const int SequenceModulus = 1 << 14;
    private const int HalfSequenceRange = SequenceModulus / 2;

    public static void Run(string capturePath, string markerPath)
    {
        var records = ReadCapture(capturePath);
        var markers = ReadMarkers(markerPath);
        var parser = new UnrealIrisPacketParser();
        var parsed = records.Select(record => Parse(record, parser)).ToArray();

        PrintCaptureSummary(records, parsed);
        PrintSequenceSummary(parsed);
        PrintStateIntervals(parsed, markers);
        PrintActionWindows(parsed, markers, "OPEN", marker =>
            marker.Name.StartsWith("OPEN_MUTATION_TAB", StringComparison.Ordinal)
            || marker.Name == "REOPEN_NO_SPEND");
        PrintActionWindows(parsed, markers, "CLOSE", marker =>
            marker.Name.StartsWith("CLOSE_MUTATION_TAB", StringComparison.Ordinal)
            || marker.Name == "CLOSE_AFTER_SPEND");
        PrintRepeatedActionCandidates(parsed, markers);
        PrintSpendAnalysis(parsed, markers);
        PrintExportEvidence(parsed);
        PrintTextEvidence(records, markers);
    }

    public static void Timeline(string capturePath, string startText, string endText)
    {
        var records = ReadCapture(capturePath);
        var start = ParseTime(startText, records[0].ObservedAt.Date);
        var end = ParseTime(endText, records[0].ObservedAt.Date);
        var parser = new UnrealIrisPacketParser();
        Console.WriteLine("utc\trecord\tdirection\tsrc\tdst\tbytes\tseq\tstream\tcomplete\tbatches\tshapes\thash");
        foreach (var record in records.Where(item => item.ObservedAt >= start && item.ObservedAt <= end))
        {
            var isIris = parser.TryParse(record.Payload, out var packet);
            var shapes = isIris
                ? string.Join(",", packet.Batches.Select(batch =>
                    $"h{batch.NetRefHandle}/b{batch.DataBitCount}/o{Convert.ToInt32(batch.HasOwnerData)}/e{Convert.ToInt32(batch.HasExports)}"))
                : string.Empty;
            var hash = Convert.ToHexString(SHA256.HashData(record.Payload)).Substring(0, 12);
            Console.WriteLine(
                $"{record.ObservedAt:O}\t{record.Index}\t{record.Direction}\t" +
                $"{record.SourceAddress}:{record.SourcePort}\t{record.DestinationAddress}:{record.DestinationPort}\t" +
                $"{record.Payload.Length}\t{(isIris ? packet.PacketSequence : -1)}\t" +
                $"{(isIris && packet.HasDataStream ? 1 : 0)}\t{(isIris && packet.IsComplete ? 1 : 0)}\t" +
                $"{(isIris ? packet.Batches.Count : 0)}\t{shapes}\t{hash}");
        }
    }

    public static void Exports(string capturePath, int recordIndex)
    {
        var records = ReadCapture(capturePath);
        var record = records.Single(item => item.Index == recordIndex);
        var parser = new UnrealIrisPacketParser();
        if (!parser.TryParse(record.Payload, out var packet))
        {
            Console.WriteLine($"record={recordIndex} not-iris");
            return;
        }

        var tracker = new ProbeIrisObjectExportTracker();
        Console.WriteLine($"record={record.Index} utc={record.ObservedAt:O} direction={record.Direction} bytes={record.Payload.Length} seq={packet.PacketSequence}");
        foreach (var batch in packet.Batches)
        {
            Console.WriteLine($"batch handle={batch.NetRefHandle} bits={batch.DataBitCount} owner={batch.HasOwnerData} exports={batch.HasExports}");
            foreach (var export in tracker.Observe(record.Payload, batch))
            {
                Console.WriteLine($"  export handle={export.NetRefHandle} outer={export.OuterNetRefHandle} relative={export.RelativePath} full={export.FullPath}");
            }
        }
    }

    private static ParsedCaptureRecord Parse(CaptureRecord record, UnrealIrisPacketParser parser) =>
        parser.TryParse(record.Payload, out var packet)
            ? new ParsedCaptureRecord(record, true, packet)
            : new ParsedCaptureRecord(record, false, default);

    private static void PrintCaptureSummary(
        IReadOnlyList<CaptureRecord> records,
        IReadOnlyList<ParsedCaptureRecord> parsed)
    {
        Console.WriteLine("=== CAPTURE ===");
        Console.WriteLine(
            $"records={records.Count} start={records[0].ObservedAt:O} end={records[^1].ObservedAt:O} " +
            $"duration={(records[^1].ObservedAt - records[0].ObservedAt).TotalSeconds:F3}s");
        foreach (var direction in Enum.GetValues<CaptureDirection>())
        {
            if (direction == CaptureDirection.Unknown)
            {
                continue;
            }

            var raw = records.Where(record => record.Direction == direction).ToArray();
            var iris = parsed.Where(record => record.Record.Direction == direction && record.IsIris).ToArray();
            Console.WriteLine(
                $"{direction}: raw={raw.Length} bytes={raw.Sum(item => (long)item.Payload.Length)} " +
                $"iris={iris.Length} datastream={iris.Count(item => item.Packet.HasDataStream)} " +
                $"complete={iris.Count(item => item.Packet.IsComplete)} " +
                $"batches={iris.Sum(item => item.Packet.Batches.Count)}");
        }

        var reasons = parsed
            .Where(record => record.IsIris && !record.Packet.IsComplete)
            .GroupBy(record => record.Packet.IncompleteReason ?? "unknown")
            .OrderByDescending(group => group.Count());
        Console.WriteLine("incomplete=" + string.Join(", ", reasons.Select(group => $"{group.Key}:{group.Count()}")));
        Console.WriteLine();
    }

    private static void PrintSequenceSummary(IReadOnlyList<ParsedCaptureRecord> parsed)
    {
        Console.WriteLine("=== IRIS SEQUENCE QUALITY ===");
        foreach (var flowGroup in parsed
                     .Where(record => record.IsIris)
                     .GroupBy(record => new FlowKey(
                         record.Record.Direction,
                         record.Record.SourceAddress,
                         record.Record.SourcePort,
                         record.Record.DestinationAddress,
                         record.Record.DestinationPort)))
        {
            var ordered = flowGroup.OrderBy(record => record.Record.ObservedAt).ThenBy(record => record.Record.Index);
            var recentQueue = new Queue<int>();
            var recent = new HashSet<int>();
            int? latest = null;
            long inOrder = 0;
            long gaps = 0;
            long missing = 0;
            long reordered = 0;
            long duplicates = 0;
            foreach (var record in ordered)
            {
                var sequence = record.Packet.PacketSequence;
                if (latest is null)
                {
                    latest = sequence;
                    Remember(sequence, recentQueue, recent);
                    continue;
                }

                var forward = (sequence - latest.Value + SequenceModulus) % SequenceModulus;
                if (forward == 0 || recent.Contains(sequence))
                {
                    duplicates++;
                    continue;
                }

                if (forward < HalfSequenceRange)
                {
                    latest = sequence;
                    Remember(sequence, recentQueue, recent);
                    if (forward == 1)
                    {
                        inOrder++;
                    }
                    else
                    {
                        gaps++;
                        missing += forward - 1;
                    }

                    continue;
                }

                reordered++;
                Remember(sequence, recentQueue, recent);
            }

            Console.WriteLine(
                $"{flowGroup.Key.Direction} {flowGroup.Key.SourceAddress}:{flowGroup.Key.SourcePort}" +
                $"->{flowGroup.Key.DestinationAddress}:{flowGroup.Key.DestinationPort} iris={flowGroup.Count()} " +
                $"inOrder={inOrder} gapEvents={gaps} missingSeq={missing} " +
                $"reordered={reordered} duplicate={duplicates}");
        }

        Console.WriteLine();
    }

    private static void Remember(int sequence, Queue<int> queue, HashSet<int> recent)
    {
        if (!recent.Add(sequence))
        {
            return;
        }

        queue.Enqueue(sequence);
        while (queue.Count > 256)
        {
            recent.Remove(queue.Dequeue());
        }
    }

    private static void PrintStateIntervals(
        IReadOnlyList<ParsedCaptureRecord> records,
        IReadOnlyList<Marker> markers)
    {
        Console.WriteLine("=== CONTROLLED STATE INTERVALS ===");
        var intervals = new[]
        {
            Interval(markers, "BASELINE", "BASELINE_START", "OPEN_MUTATION_TAB"),
            Interval(markers, "OPEN_1", "OPEN_MUTATION_TAB", "CLOSE_MUTATION_TAB"),
            Interval(markers, "CLOSED_1", "CLOSE_MUTATION_TAB", "OPEN_MUTATION_TAB_2"),
            Interval(markers, "OPEN_2", "OPEN_MUTATION_TAB_2", "CLOSE_MUTATION_TAB_2"),
            Interval(markers, "CLOSED_2", "CLOSE_MUTATION_TAB_2", "OPEN_MUTATION_TAB_3"),
            Interval(markers, "OPEN_3", "OPEN_MUTATION_TAB_3", "CLOSE_MUTATION_TAB_3"),
            Interval(markers, "CLOSED_3", "CLOSE_MUTATION_TAB_3", "OPEN_MUTATION_TAB_4"),
            Interval(markers, "OPEN_4", "OPEN_MUTATION_TAB_4", "CLOSE_MUTATION_TAB_4"),
            Interval(markers, "CLOSED_4", "CLOSE_MUTATION_TAB_4", "OPEN_MUTATION_TAB_5"),
            Interval(markers, "OPEN_5_PRE_SELECT", "OPEN_MUTATION_TAB_5", "BEFORE_SELECTION"),
            Interval(markers, "SELECT_INTERVAL", "BEFORE_SELECTION", "SELECTION_STABLE"),
            Interval(markers, "SPEND_INTERVAL", "BEFORE_SPEND", "AFTER_SPEND"),
            Interval(markers, "POST_SPEND_STABLE", "AFTER_SPEND", "POST_SPEND_STABLE"),
            Interval(markers, "CLOSED_AFTER_SPEND", "CLOSE_AFTER_SPEND", "REOPEN_NO_SPEND"),
            Interval(markers, "REOPEN_EQUIPPED", "REOPEN_NO_SPEND", "NEGATIVE_INVENTORY_BEGIN"),
            Interval(markers, "INVENTORY_CONTROL", "NEGATIVE_INVENTORY_BEGIN", "NEGATIVE_INVENTORY_END"),
            Interval(markers, "MOVEMENT_CONTROL", "NEGATIVE_MOVEMENT_BEGIN", "NEGATIVE_MOVEMENT_END")
        };

        foreach (var interval in intervals)
        {
            PrintInterval(records, interval);
        }

        Console.WriteLine();
    }

    private static void PrintInterval(IReadOnlyList<ParsedCaptureRecord> records, TimeInterval interval)
    {
        var selected = records
            .Where(record => record.Record.ObservedAt >= interval.Start && record.Record.ObservedAt < interval.End)
            .ToArray();
        var seconds = Math.Max(0.001, (interval.End - interval.Start).TotalSeconds);
        var inbound = selected.Where(record => record.Record.Direction == CaptureDirection.Inbound).ToArray();
        var outbound = selected.Where(record => record.Record.Direction == CaptureDirection.Outbound).ToArray();
        var iris = selected.Where(record => record.IsIris).ToArray();
        var batches = iris.SelectMany(record => record.Packet.Batches).ToArray();
        Console.WriteLine(
            $"{interval.Name,-20} {seconds,7:F3}s raw(in/out)={inbound.Length}/{outbound.Length} " +
            $"rate={selected.Length / seconds,6:F2}/s iris={iris.Length}({iris.Length / seconds:F2}/s) " +
            $"data={iris.Count(item => item.Packet.HasDataStream)} batches={batches.Length}({batches.Length / seconds:F2}/s) " +
            $"handles={batches.Select(item => item.NetRefHandle).Distinct().Count()} " +
            $"owner={batches.Count(item => item.HasOwnerData)} exports={batches.Count(item => item.HasExports)}");

        var signatures = selected.SelectMany(ToSignatures)
            .GroupBy(signature => signature)
            .OrderByDescending(group => group.Count())
            .Take(6)
            .Select(group => $"{FormatSignature(group.Key)}x{group.Count()}");
        Console.WriteLine("  top=" + string.Join(" | ", signatures));
    }

    private static void PrintActionWindows(
        IReadOnlyList<ParsedCaptureRecord> records,
        IReadOnlyList<Marker> markers,
        string title,
        Func<Marker, bool> predicate)
    {
        Console.WriteLine($"=== {title} EVENT WINDOWS (5s BEFORE/AFTER) ===");
        foreach (var marker in markers.Where(predicate))
        {
            var before = Slice(records, marker.At.AddSeconds(-5), marker.At);
            var after = Slice(records, marker.At, marker.At.AddSeconds(5));
            var beforeBatches = before.Where(item => item.IsIris).Sum(item => item.Packet.Batches.Count);
            var afterBatches = after.Where(item => item.IsIris).Sum(item => item.Packet.Batches.Count);
            Console.WriteLine(
                $"{marker.Name,-24} {marker.At:HH:mm:ss.fff} " +
                $"raw {before.Length}->{after.Length} iris {before.Count(item => item.IsIris)}->{after.Count(item => item.IsIris)} " +
                $"batches {beforeBatches}->{afterBatches}");

            var deltas = SignatureDeltas(before, after)
                .Where(item => item.After > 0 || item.Before > 0)
                .OrderByDescending(item => Math.Abs(item.After - item.Before))
                .ThenByDescending(item => item.After)
                .Take(8);
            Console.WriteLine(
                "  delta=" + string.Join(
                    " | ",
                    deltas.Select(item => $"{FormatSignature(item.Signature)}:{item.Before}->{item.After}")));
        }

        Console.WriteLine();
    }

    private static void PrintRepeatedActionCandidates(
        IReadOnlyList<ParsedCaptureRecord> records,
        IReadOnlyList<Marker> markers)
    {
        Console.WriteLine("=== REPEATED OPEN/CLOSE SIGNATURE CANDIDATES ===");
        var openMarkers = markers.Where(marker =>
                marker.Name.StartsWith("OPEN_MUTATION_TAB", StringComparison.Ordinal))
            .ToArray();
        var closeMarkers = markers.Where(marker =>
                marker.Name.StartsWith("CLOSE_MUTATION_TAB", StringComparison.Ordinal))
            .ToArray();
        PrintCrossEventCandidates(records, "OPEN x5", openMarkers);
        PrintCrossEventCandidates(records, "CLOSE x4", closeMarkers);
        Console.WriteLine();
    }

    private static void PrintCrossEventCandidates(
        IReadOnlyList<ParsedCaptureRecord> records,
        string label,
        IReadOnlyList<Marker> events)
    {
        var perEvent = events.Select(marker => new
        {
            Before = CountSignatures(Slice(records, marker.At.AddSeconds(-5), marker.At)),
            After = CountSignatures(Slice(records, marker.At, marker.At.AddSeconds(5)))
        }).ToArray();
        var allSignatures = perEvent
            .SelectMany(item => item.Before.Keys.Concat(item.After.Keys))
            .Distinct()
            .ToArray();
        var candidates = allSignatures
            .Select(signature => new CrossEventCandidate(
                signature,
                perEvent.Count(item => item.After.GetValueOrDefault(signature) > 0),
                perEvent.Count(item =>
                    item.After.GetValueOrDefault(signature) > item.Before.GetValueOrDefault(signature)),
                perEvent.Sum(item => item.After.GetValueOrDefault(signature)),
                perEvent.Sum(item => item.Before.GetValueOrDefault(signature))))
            .Where(item => item.EventsPresentAfter >= Math.Max(2, events.Count - 1))
            .OrderByDescending(item => item.EventsWithPositiveDelta)
            .ThenByDescending(item => item.AfterTotal - item.BeforeTotal)
            .Take(20)
            .ToArray();
        Console.WriteLine(label + ":");
        if (candidates.Length == 0)
        {
            Console.WriteLine("  <none>");
            return;
        }

        foreach (var candidate in candidates)
        {
            Console.WriteLine(
                $"  {FormatSignature(candidate.Signature)} presentAfter={candidate.EventsPresentAfter}/{events.Count} " +
                $"positiveDelta={candidate.EventsWithPositiveDelta}/{events.Count} " +
                $"total={candidate.BeforeTotal}->{candidate.AfterTotal}");
        }
    }

    private static void PrintSpendAnalysis(
        IReadOnlyList<ParsedCaptureRecord> records,
        IReadOnlyList<Marker> markers)
    {
        Console.WriteLine("=== EFFICIENT DIGESTION SELECTION/SPEND ===");
        var selection = Interval(markers, "selection", "BEFORE_SELECTION", "SELECTION_STABLE");
        var spend = Interval(markers, "spend", "BEFORE_SPEND", "AFTER_SPEND");
        var post = Interval(markers, "post", "AFTER_SPEND", "POST_SPEND_STABLE");
        var inventory = Interval(markers, "inventory", "NEGATIVE_INVENTORY_BEGIN", "NEGATIVE_INVENTORY_END");
        var movement = Interval(markers, "movement", "NEGATIVE_MOVEMENT_BEGIN", "NEGATIVE_MOVEMENT_END");
        var periods = new[] { selection, spend, post, inventory, movement };

        var periodSignatures = periods.ToDictionary(
            interval => interval.Name,
            interval => CountSignatures(Slice(records, interval.Start, interval.End)));
        var periodDurations = periods.ToDictionary(
            interval => interval.Name,
            interval => (interval.End - interval.Start).TotalSeconds);
        var all = periodSignatures.Values.SelectMany(item => item.Keys).Distinct();
        var ranked = all.Select(signature =>
            {
                var spendCount = periodSignatures["spend"].GetValueOrDefault(signature);
                var spendRate = spendCount / periodDurations["spend"];
                var controlCount = periodSignatures["selection"].GetValueOrDefault(signature)
                                   + periodSignatures["inventory"].GetValueOrDefault(signature)
                                   + periodSignatures["movement"].GetValueOrDefault(signature);
                var controlDuration = periodDurations["selection"]
                                      + periodDurations["inventory"]
                                      + periodDurations["movement"];
                var controlRate = controlCount / controlDuration;
                return new SpendCandidate(signature, spendCount, spendRate, controlCount, controlRate);
            })
            .Where(item => item.SpendCount >= 2 && item.SpendRate > item.ControlRate * 1.5 + 0.01)
            .OrderByDescending(item => item.SpendRate / Math.Max(0.0001, item.ControlRate))
            .ThenByDescending(item => item.SpendCount)
            .Take(40)
            .ToArray();
        Console.WriteLine("spend-enriched batch signatures:");
        foreach (var candidate in ranked)
        {
            Console.WriteLine(
                $"  {FormatSignature(candidate.Signature)} spend={candidate.SpendCount}({candidate.SpendRate:F3}/s) " +
                $"controls={candidate.ControlCount}({candidate.ControlRate:F3}/s) " +
                $"ratio={candidate.SpendRate / Math.Max(0.0001, candidate.ControlRate):F2}");
        }

        var spendRecords = Slice(records, spend.Start, spend.End);
        var beforeSpend = Slice(records, selection.Start, spend.Start);
        var afterSpend = Slice(records, spend.End, post.End);
        var outsideHandles = records
            .Where(item => item.Record.ObservedAt < spend.Start || item.Record.ObservedAt >= spend.End)
            .SelectMany(ToSignatures)
            .Select(signature => signature.Handle)
            .ToHashSet();
        var handleFirst = spendRecords
            .SelectMany(record => ToSignatures(record).Select(signature => new { record.Record.ObservedAt, Signature = signature }))
            .Where(item => !outsideHandles.Contains(item.Signature.Handle))
            .GroupBy(item => item.Signature.Handle)
            .Select(group => new
            {
                Handle = group.Key,
                First = group.Min(item => item.ObservedAt),
                Count = group.Count(),
                Signatures = group.Select(item => item.Signature).Distinct().ToArray()
            })
            .OrderBy(item => item.First)
            .ToArray();
        Console.WriteLine("handles seen only inside spend bound:");
        if (handleFirst.Length == 0)
        {
            Console.WriteLine("  <none>");
        }
        foreach (var item in handleFirst)
        {
            Console.WriteLine(
                $"  handle={item.Handle} first={item.First:HH:mm:ss.fff} count={item.Count} " +
                $"signatures={string.Join(',', item.Signatures.Select(FormatSignature))}");
        }

        var persistent = spendRecords.SelectMany(ToSignatures).Select(item => item.Handle).Distinct()
            .Where(handle => !beforeSpend.SelectMany(ToSignatures).Any(item => item.Handle == handle)
                             && afterSpend.SelectMany(ToSignatures).Any(item => item.Handle == handle))
            .ToArray();
        Console.WriteLine("handles first in spend bound and present afterward=" +
                          (persistent.Length == 0 ? "<none>" : string.Join(',', persistent)));

        PrintPacketLengthDiff(records, selection, spend, post, inventory, movement);
        Console.WriteLine();
    }

    private static void PrintPacketLengthDiff(
        IReadOnlyList<ParsedCaptureRecord> records,
        params TimeInterval[] periods)
    {
        Console.WriteLine("packet-length rates by interval (top spend-enriched):");
        var data = periods.ToDictionary(
            interval => interval.Name,
            interval => Slice(records, interval.Start, interval.End)
                .GroupBy(item => new PacketShape(
                    item.Record.Direction,
                    item.Record.Payload.Length,
                    item.IsIris,
                    item.IsIris && item.Packet.HasDataStream))
                .ToDictionary(group => group.Key, group => group.Count()));
        var durations = periods.ToDictionary(
            interval => interval.Name,
            interval => (interval.End - interval.Start).TotalSeconds);
        var shapes = data.Values.SelectMany(item => item.Keys).Distinct();
        var ranked = shapes.Select(shape =>
            {
                var spendCount = data["spend"].GetValueOrDefault(shape);
                var controlCount = data["selection"].GetValueOrDefault(shape)
                                   + data["inventory"].GetValueOrDefault(shape)
                                   + data["movement"].GetValueOrDefault(shape);
                var spendRate = spendCount / durations["spend"];
                var controlRate = controlCount /
                                  (durations["selection"] + durations["inventory"] + durations["movement"]);
                return new { Shape = shape, SpendCount = spendCount, SpendRate = spendRate, ControlCount = controlCount, ControlRate = controlRate };
            })
            .Where(item => item.SpendCount >= 2 && item.SpendRate > item.ControlRate * 1.5 + 0.01)
            .OrderByDescending(item => item.SpendRate / Math.Max(0.0001, item.ControlRate))
            .ThenByDescending(item => item.SpendCount)
            .Take(30);
        foreach (var item in ranked)
        {
            Console.WriteLine(
                $"  {item.Shape.Direction} bytes={item.Shape.Bytes} iris={item.Shape.IsIris} data={item.Shape.HasDataStream} " +
                $"spend={item.SpendCount}({item.SpendRate:F3}/s) controls={item.ControlCount}({item.ControlRate:F3}/s) " +
                $"ratio={item.SpendRate / Math.Max(0.0001, item.ControlRate):F2}");
        }
    }

    private static void PrintExportEvidence(IReadOnlyList<ParsedCaptureRecord> parsed)
    {
        Console.WriteLine("=== OBJECT EXPORT PATH EVIDENCE ===");
        var tracker = new ProbeIrisObjectExportTracker();
        var exports = new List<(DateTimeOffset At, int Record, CaptureDirection Direction, ProbeIrisObjectReferenceExport Export)>();
        foreach (var record in parsed.Where(item => item.IsIris).OrderBy(item => item.Record.Index))
        {
            foreach (var batch in record.Packet.Batches)
            {
                foreach (var exported in tracker.Observe(record.Record.Payload, batch))
                {
                    exports.Add((record.Record.ObservedAt, record.Record.Index, record.Record.Direction, exported));
                }
            }
        }

        Console.WriteLine($"parsedExportObjects={exports.Count} distinct={exports.Select(item => item.Export.NetRefHandle).Distinct().Count()}");
        var relevant = exports.Where(item => ContainsMutationTerm(item.Export.FullPath)).ToArray();
        Console.WriteLine($"mutationTermMatches={relevant.Length}");
        foreach (var item in relevant.Take(200))
        {
            Console.WriteLine(
                $"  utc={item.At:HH:mm:ss.fff} record={item.Record} {item.Direction} " +
                $"handle={item.Export.NetRefHandle} outer={item.Export.OuterNetRefHandle} path={item.Export.FullPath}");
        }

        Console.WriteLine();
    }

    private static void PrintTextEvidence(
        IReadOnlyList<CaptureRecord> records,
        IReadOnlyList<Marker> markers)
    {
        Console.WriteLine("=== RAW TEXT EVIDENCE ===");
        var terms = new[] { "mutation", "perk", "efficient", "digestion", "food", "drain", "elder" };
        var matches = new List<string>();
        foreach (var record in records)
        {
            var ascii = Encoding.UTF8.GetString(record.Payload);
            var unicode = record.Payload.Length % 2 == 0 ? Encoding.Unicode.GetString(record.Payload) : string.Empty;
            foreach (var term in terms)
            {
                if (ascii.Contains(term, StringComparison.OrdinalIgnoreCase)
                    || unicode.Contains(term, StringComparison.OrdinalIgnoreCase))
                {
                    matches.Add($"utc={record.ObservedAt:HH:mm:ss.fff} record={record.Index} {record.Direction} term={term}");
                }
            }
        }

        Console.WriteLine($"termMatches={matches.Count}");
        foreach (var match in matches.Take(200))
        {
            Console.WriteLine("  " + match);
        }

        var spend = Interval(markers, "spend", "BEFORE_SPEND", "AFTER_SPEND");
        var hashes = records.Where(record => record.ObservedAt >= spend.Start && record.ObservedAt < spend.End)
            .GroupBy(record => new
            {
                record.Direction,
                record.Payload.Length,
                Hash = Convert.ToHexString(SHA256.HashData(record.Payload)).Substring(0, 16)
            })
            .Where(group => group.Count() > 1)
            .OrderByDescending(group => group.Count())
            .Take(20)
            .ToArray();
        Console.WriteLine("exact repeated payloads inside spend bound:");
        if (hashes.Length == 0)
        {
            Console.WriteLine("  <none>");
        }
        foreach (var group in hashes)
        {
            Console.WriteLine(
                $"  {group.Key.Direction} bytes={group.Key.Length} hash={group.Key.Hash} count={group.Count()}");
        }
    }

    private static bool ContainsMutationTerm(string value) =>
        value.Contains("mutation", StringComparison.OrdinalIgnoreCase)
        || value.Contains("perk", StringComparison.OrdinalIgnoreCase)
        || value.Contains("efficient", StringComparison.OrdinalIgnoreCase)
        || value.Contains("digestion", StringComparison.OrdinalIgnoreCase)
        || value.Contains("elder", StringComparison.OrdinalIgnoreCase)
        || value.Contains("food", StringComparison.OrdinalIgnoreCase);

    private static SignatureDelta[] SignatureDeltas(
        IReadOnlyList<ParsedCaptureRecord> before,
        IReadOnlyList<ParsedCaptureRecord> after)
    {
        var beforeCounts = CountSignatures(before);
        var afterCounts = CountSignatures(after);
        return beforeCounts.Keys.Concat(afterCounts.Keys).Distinct()
            .Select(signature => new SignatureDelta(
                signature,
                beforeCounts.GetValueOrDefault(signature),
                afterCounts.GetValueOrDefault(signature)))
            .ToArray();
    }

    private static Dictionary<BatchSignature, int> CountSignatures(
        IReadOnlyList<ParsedCaptureRecord> records) =>
        records.SelectMany(ToSignatures)
            .GroupBy(signature => signature)
            .ToDictionary(group => group.Key, group => group.Count());

    private static IEnumerable<BatchSignature> ToSignatures(ParsedCaptureRecord record)
    {
        if (!record.IsIris)
        {
            yield break;
        }

        foreach (var batch in record.Packet.Batches)
        {
            yield return new BatchSignature(
                record.Record.Direction,
                batch.NetRefHandle,
                batch.DataBitCount,
                batch.HasOwnerData,
                batch.HasExports);
        }
    }

    private static string FormatSignature(BatchSignature signature) =>
        $"{(signature.Direction == CaptureDirection.Inbound ? 'I' : 'O')}:h{signature.Handle}/b{signature.Bits}" +
        $"/o{Convert.ToInt32(signature.Owner)}/e{Convert.ToInt32(signature.Exports)}";

    private static ParsedCaptureRecord[] Slice(
        IReadOnlyList<ParsedCaptureRecord> records,
        DateTimeOffset start,
        DateTimeOffset end) =>
        records.Where(record => record.Record.ObservedAt >= start && record.Record.ObservedAt < end).ToArray();

    private static TimeInterval Interval(
        IReadOnlyList<Marker> markers,
        string name,
        string start,
        string end) =>
        new(name, FindMarker(markers, start).At, FindMarker(markers, end).At);

    private static Marker FindMarker(IReadOnlyList<Marker> markers, string name) =>
        markers.Single(marker => marker.Name == name);

    private static IReadOnlyList<CaptureRecord> ReadCapture(string path)
    {
        using var stream = File.OpenRead(path);
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: false);
        var header = Encoding.ASCII.GetString(reader.ReadBytes(8));
        if (header != CaptureHeader)
        {
            throw new InvalidDataException($"Unsupported capture header: {header}");
        }

        var result = new List<CaptureRecord>();
        while (stream.Position < stream.Length)
        {
            var observedAt = new DateTimeOffset(reader.ReadInt64(), TimeSpan.Zero);
            var direction = (CaptureDirection)reader.ReadByte();
            var sourceAddress = reader.ReadString();
            var sourcePort = reader.ReadUInt16();
            var destinationAddress = reader.ReadString();
            var destinationPort = reader.ReadUInt16();
            var length = reader.ReadInt32();
            var payload = reader.ReadBytes(length);
            result.Add(new CaptureRecord(
                result.Count + 1,
                observedAt,
                direction,
                sourceAddress,
                sourcePort,
                destinationAddress,
                destinationPort,
                payload));
        }

        return result;
    }

    private static IReadOnlyList<Marker> ReadMarkers(string path) =>
        File.ReadLines(path)
            .Where(line => !string.IsNullOrWhiteSpace(line) && !line.StartsWith('#'))
            .Select(line => line.Split('\t'))
            .Select(parts => new Marker(
                DateTimeOffset.Parse(parts[0], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                parts[1],
                parts.Length > 2 ? parts[2] : string.Empty))
            .ToArray();

    private static DateTimeOffset ParseTime(string text, DateTime date)
    {
        if (DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var absolute))
        {
            return absolute.ToUniversalTime();
        }

        if (!TimeSpan.TryParse(text, CultureInfo.InvariantCulture, out var time))
        {
            throw new ArgumentException($"Invalid time '{text}'.");
        }

        return new DateTimeOffset(date.Add(time), TimeSpan.Zero);
    }

    private enum CaptureDirection : byte
    {
        Unknown,
        Inbound,
        Outbound
    }

    private readonly record struct CaptureRecord(
        int Index,
        DateTimeOffset ObservedAt,
        CaptureDirection Direction,
        string SourceAddress,
        int SourcePort,
        string DestinationAddress,
        int DestinationPort,
        byte[] Payload);

    private readonly record struct ParsedCaptureRecord(
        CaptureRecord Record,
        bool IsIris,
        UnrealIrisPacket Packet);

    private readonly record struct FlowKey(
        CaptureDirection Direction,
        string SourceAddress,
        int SourcePort,
        string DestinationAddress,
        int DestinationPort);

    private readonly record struct Marker(DateTimeOffset At, string Name, string Note);

    private readonly record struct TimeInterval(
        string Name,
        DateTimeOffset Start,
        DateTimeOffset End);

    private readonly record struct BatchSignature(
        CaptureDirection Direction,
        ulong Handle,
        int Bits,
        bool Owner,
        bool Exports);

    private readonly record struct SignatureDelta(BatchSignature Signature, int Before, int After);

    private readonly record struct CrossEventCandidate(
        BatchSignature Signature,
        int EventsPresentAfter,
        int EventsWithPositiveDelta,
        int AfterTotal,
        int BeforeTotal);

    private readonly record struct SpendCandidate(
        BatchSignature Signature,
        int SpendCount,
        double SpendRate,
        int ControlCount,
        double ControlRate);

    private readonly record struct PacketShape(
        CaptureDirection Direction,
        int Bytes,
        bool IsIris,
        bool HasDataStream);
}
