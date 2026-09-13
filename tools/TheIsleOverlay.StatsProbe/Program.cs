using System.Diagnostics;
using System.Collections.Concurrent;
using System.Globalization;
using System.Net.NetworkInformation;
using System.Text;
using PacketDotNet;
using SharpPcap;
using TheIsleOverlay.LocalTelemetry;

const string Header = "ISLETR01";

if (args.Length == 0)
{
    Console.Error.WriteLine(
        "Usage: capture <seconds> <output.bin> | convert-inbound <capture.bin> <output.bin> | " +
        "analyze <capture.bin> name=value [...] | " +
        "pairs <capture.bin> <sequence|HH:mm:ss.fff> [window-ms]");
    return 2;
}

if (string.Equals(args[0], "convert-inbound", StringComparison.OrdinalIgnoreCase))
{
    if (args.Length != 3)
    {
        Console.Error.WriteLine("Usage: convert-inbound <capture.bin> <output.bin>");
        return 2;
    }

    ConvertInbound(Path.GetFullPath(args[1]), Path.GetFullPath(args[2]));
    return 0;
}

if (string.Equals(args[0], "capture", StringComparison.OrdinalIgnoreCase))
{
    if (args.Length != 3 || !int.TryParse(args[1], out var seconds))
    {
        Console.Error.WriteLine("Usage: capture <seconds> <output.bin>");
        return 2;
    }

    // Diagnostic recordings may span a full reconnect/session window. Keep the
    // upper bound explicit so a malformed command cannot create an unbounded
    // capture, while allowing the requested 20–30 minute observation period.
    await CaptureAsync(Math.Clamp(seconds, 1, 1_800), Path.GetFullPath(args[2]));
    return 0;
}

if (string.Equals(args[0], "analyze", StringComparison.OrdinalIgnoreCase))
{
    if (args.Length < 3)
    {
        Console.Error.WriteLine("Usage: analyze <capture.bin> name=value [...]");
        return 2;
    }

    var targets = args.Skip(2).Select(ParseTarget).ToArray();
    Analyze(Path.GetFullPath(args[1]), targets);
    return 0;
}

if (string.Equals(args[0], "pairs", StringComparison.OrdinalIgnoreCase))
{
    if (args.Length is < 3 or > 4)
    {
        Console.Error.WriteLine("Usage: pairs <capture.bin> <sequence|HH:mm:ss.fff> [window-ms]");
        return 2;
    }

    var windowMilliseconds = args.Length == 4
        ? int.Parse(args[3], CultureInfo.InvariantCulture)
        : 0;
    PrintAttributePairs(Path.GetFullPath(args[1]), args[2], windowMilliseconds);
    return 0;
}

Console.Error.WriteLine($"Unknown command: {args[0]}");
return 2;

static async Task CaptureAsync(int seconds, string outputPath)
{
    using var process = Process.GetProcessesByName(NpcapLocalMovementSource.DefaultGameProcessName)
                            .FirstOrDefault()
                        ?? throw new InvalidOperationException("The Isle game process is not running.");
    var portResolver = new WindowsUdpPortOwnerResolver();
    var ports = portResolver.GetOwnedPorts(process.Id);
    if (ports.Count == 0)
    {
        throw new InvalidOperationException($"No UDP ports belong to game process {process.Id}.");
    }

    Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
    using var writer = new BinaryWriter(
        new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.Read),
        Encoding.UTF8,
        leaveOpen: false);
    writer.Write(Encoding.ASCII.GetBytes(Header));

    var gate = new object();
    var packets = 0L;
    var inbound = 0L;
    var outbound = 0L;
    var ownedPorts = new ConcurrentDictionary<int, byte>(
        ports.Select(port => new KeyValuePair<int, byte>(port, 0)));
    var devices = OpenDevices(packetCapture =>
    {
        try
        {
            var raw = packetCapture.GetPacket();
            var packet = Packet.ParsePacket(raw.LinkLayerType, raw.Data);
            var ip = packet.Extract<IPPacket>();
            var udp = packet.Extract<UdpPacket>();
            var payload = udp?.PayloadData;
            if (ip is null || udp is null || payload is null || payload.Length == 0)
            {
                return;
            }

            var direction = ownedPorts.ContainsKey(udp.SourcePort)
                ? TrafficDirection.Outbound
                : ownedPorts.ContainsKey(udp.DestinationPort)
                    ? TrafficDirection.Inbound
                    : TrafficDirection.Unknown;
            if (direction == TrafficDirection.Unknown)
            {
                return;
            }

            lock (gate)
            {
                writer.Write(DateTimeOffset.UtcNow.UtcTicks);
                writer.Write((byte)direction);
                writer.Write(ip.SourceAddress.ToString());
                writer.Write((ushort)udp.SourcePort);
                writer.Write(ip.DestinationAddress.ToString());
                writer.Write((ushort)udp.DestinationPort);
                writer.Write(payload.Length);
                writer.Write(payload);
                writer.Flush();
                packets++;
                if (direction == TrafficDirection.Inbound) inbound++;
                if (direction == TrafficDirection.Outbound) outbound++;
            }
        }
        catch
        {
            // Unrelated or malformed packets are ignored by this diagnostic tool.
        }
    });

    Console.WriteLine(
        $"Capturing bidirectional game UDP for PID {process.Id}, ports " +
        $"{string.Join(",", ports.Order())}, for {seconds}s...");
    try
    {
        var endsAt = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(seconds);
        while (DateTimeOffset.UtcNow < endsAt)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(250));
            var latestPorts = portResolver.GetOwnedPorts(process.Id);
            var changed = false;
            foreach (var port in ownedPorts.Keys.Where(port => !latestPorts.Contains(port)))
            {
                changed |= ownedPorts.TryRemove(port, out _);
            }

            foreach (var port in latestPorts)
            {
                changed |= ownedPorts.TryAdd(port, 0);
            }

            if (changed)
            {
                Console.WriteLine(
                    $"Game UDP ports changed: {string.Join(',', ownedPorts.Keys.Order())}");
            }
        }
    }
    finally
    {
        foreach (var opened in devices)
        {
            opened.Device.OnPacketArrival -= opened.Handler;
            try { opened.Device.StopCapture(); } catch { }
            try { opened.Device.Close(); } catch { }
        }
    }

    Console.WriteLine($"Captured packets={packets:N0}, inbound={inbound:N0}, outbound={outbound:N0}");
    Console.WriteLine($"Capture: {outputPath}");
}

static IReadOnlyList<OpenedDevice> OpenDevices(PacketHandler onPacket)
{
    var devices = CaptureDeviceList.Instance;
    var activeDescriptions = NetworkInterface.GetAllNetworkInterfaces()
        .Where(network => network.OperationalStatus == OperationalStatus.Up
                          && network.NetworkInterfaceType != NetworkInterfaceType.Loopback)
        .Select(network => network.Description)
        .ToHashSet(StringComparer.OrdinalIgnoreCase);
    var candidates = devices
        .Where(device => !string.IsNullOrWhiteSpace(device.Description)
                         && activeDescriptions.Contains(device.Description))
        .ToArray();
    if (candidates.Length == 0)
    {
        candidates = devices.ToArray();
    }

    var opened = new List<OpenedDevice>();
    foreach (var device in candidates)
    {
        PacketArrivalEventHandler handler = (_, packetCapture) => onPacket(packetCapture);
        try
        {
            device.Open(DeviceModes.None, 250);
            // Keep the kernel filter stable across reconnects. The packet
            // handler still persists only datagrams whose source/destination
            // port currently belongs to the game process, while the resolver
            // refreshes that exact set every 250 ms.
            device.Filter = "udp";
            device.OnPacketArrival += handler;
            device.StartCapture();
            opened.Add(new OpenedDevice(device, handler));
            Console.WriteLine($"Adapter: {device.Description}");
        }
        catch (Exception exception)
        {
            device.OnPacketArrival -= handler;
            try { device.Close(); } catch { }
            Console.Error.WriteLine($"Skipped adapter '{device.Description}': {exception.Message}");
        }
    }

    if (opened.Count == 0)
    {
        throw new InvalidOperationException("Npcap could not open an active adapter.");
    }

    return opened;
}

static void Analyze(string capturePath, IReadOnlyList<Target> targets)
{
    var records = ReadRecords(capturePath);
    var hits = new List<FloatHit>();
    foreach (var record in records)
    {
        var payloadBits = record.Payload.Length * 8;
        for (var bitOffset = 0; bitOffset + 32 <= payloadBits; bitOffset++)
        {
            var bits = (uint)ReadBits(record.Payload, bitOffset, 32);
            var value = BitConverter.Int32BitsToSingle((int)bits);
            if (!float.IsFinite(value))
            {
                continue;
            }

            foreach (var target in targets)
            {
                var tolerance = target.Tolerance
                                ?? Math.Max(0.005d, Math.Abs(target.Value) * 0.0002d);
                if (Math.Abs(value - target.Value) <= tolerance)
                {
                    hits.Add(new FloatHit(
                        record.Sequence,
                        record.ObservedAt,
                        record.Direction,
                        record.Payload.Length,
                        bitOffset,
                        target.Name,
                        value));
                }
            }
        }
    }

    Console.WriteLine(
        $"records={records.Count:N0} inbound={records.Count(r => r.Direction == TrafficDirection.Inbound):N0} " +
        $"outbound={records.Count(r => r.Direction == TrafficDirection.Outbound):N0} floatHits={hits.Count:N0}");
    Console.WriteLine("count direction target payload_bytes bit_offset first_utc last_utc min max");
    foreach (var group in hits
                 .GroupBy(hit => new { hit.Direction, hit.Target, hit.PayloadLength, hit.BitOffset })
                 .OrderByDescending(group => group.Count())
                 .ThenBy(group => group.Key.Direction)
                 .ThenBy(group => group.Key.Target)
                 .Take(300))
    {
        Console.WriteLine(
            $"{group.Count(),5} {group.Key.Direction,-8} {group.Key.Target,-14} " +
            $"{group.Key.PayloadLength,5} {group.Key.BitOffset,6} " +
            $"{group.Min(hit => hit.ObservedAt):HH:mm:ss.fff} {group.Max(hit => hit.ObservedAt):HH:mm:ss.fff} " +
            $"{group.Min(hit => hit.Value):R} {group.Max(hit => hit.Value):R}");
    }

    if (hits.Count <= 100)
    {
        Console.WriteLine("sequence utc direction target payload_bytes bit_offset value");
        foreach (var hit in hits.OrderBy(hit => hit.Sequence).ThenBy(hit => hit.BitOffset))
        {
            Console.WriteLine(
                $"{hit.Sequence,8} {hit.ObservedAt:HH:mm:ss.fff} {hit.Direction,-8} " +
                $"{hit.Target,-14} {hit.PayloadLength,5} {hit.BitOffset,6} {hit.Value:R}");
        }
    }
}

static void PrintAttributePairs(string capturePath, string selector, int windowMilliseconds)
{
    var records = ReadRecords(capturePath);
    IReadOnlyList<TrafficRecord> selected;
    if (int.TryParse(selector, NumberStyles.Integer, CultureInfo.InvariantCulture, out var sequence))
    {
        selected = records.Where(record => record.Sequence == sequence).ToArray();
    }
    else if (TimeSpan.TryParseExact(
                 selector,
                 ["hh\\:mm\\:ss\\.fff", "h\\:mm\\:ss\\.fff"],
                 CultureInfo.InvariantCulture,
                 out var targetTime))
    {
        var targetMilliseconds = targetTime.TotalMilliseconds;
        var window = Math.Max(windowMilliseconds, 5);
        selected = records
            .Where(record => Math.Abs(record.ObservedAt.TimeOfDay.TotalMilliseconds - targetMilliseconds) <= window)
            .ToArray();
        if (selected.Count == 0)
        {
            selected = records
                .OrderBy(record => Math.Abs(record.ObservedAt.TimeOfDay.TotalMilliseconds - targetMilliseconds))
                .Take(1)
                .ToArray();
        }
    }
    else
    {
        throw new ArgumentException($"Invalid record selector '{selector}'.");
    }

    foreach (var record in selected.OrderBy(record => record.ObservedAt))
    {
        Console.WriteLine(
            $"record={record.Sequence} utc={record.ObservedAt:HH:mm:ss.fff} " +
            $"direction={record.Direction} bytes={record.Payload.Length} " +
            $"{record.SourceAddress}:{record.SourcePort} -> " +
            $"{record.DestinationAddress}:{record.DestinationPort}");

        var pairs = new List<AttributePair>();
        var payloadBits = record.Payload.Length * 8;
        for (var bitOffset = 0; bitOffset + 65 <= payloadBits; bitOffset++)
        {
            var first = ReadFloat(record.Payload, bitOffset);
            var second = ReadFloat(record.Payload, bitOffset + 33);
            if (!IsPlausibleAttributeValue(first)
                || !IsPlausibleAttributeValue(second)
                || Math.Abs(first - second) > Math.Max(0.0001f, Math.Abs(first) * 0.000001f))
            {
                continue;
            }

            pairs.Add(new AttributePair(bitOffset, first, IsBitSet(record.Payload, bitOffset + 32)));
        }

        Console.WriteLine("  bit_offset separator value");
        foreach (var pair in pairs)
        {
            Console.WriteLine($"  {pair.BitOffset,10} {Convert.ToInt32(pair.SeparatorBit),9} {pair.Value,14:R}");
        }
    }
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
            throw new EndOfStreamException("The final traffic record is incomplete.");
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

    return records;
}

static void ConvertInbound(string capturePath, string outputPath)
{
    var records = ReadRecords(capturePath)
        .Where(record => record.Direction == TrafficDirection.Inbound)
        .ToArray();
    Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
    using var writer = new BinaryWriter(
        new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.Read),
        Encoding.UTF8,
        leaveOpen: false);
    writer.Write(Encoding.ASCII.GetBytes("ISLEIN01"));
    foreach (var record in records)
    {
        writer.Write(record.ObservedAt.UtcDateTime.Ticks);
        writer.Write(record.SourceAddress);
        writer.Write(checked((ushort)record.SourcePort));
        writer.Write(record.DestinationAddress);
        writer.Write(checked((ushort)record.DestinationPort));
        writer.Write(record.Payload.Length);
        writer.Write(record.Payload);
    }

    Console.WriteLine(
        $"Converted inbound records={records.Length:N0}: {outputPath}");
}

static Target ParseTarget(string value)
{
    var separator = value.IndexOf('=');
    var toleranceSeparator = value.IndexOf('@', separator + 1);
    var numberText = toleranceSeparator < 0
        ? value[(separator + 1)..]
        : value[(separator + 1)..toleranceSeparator];
    if (separator <= 0
        || !double.TryParse(
            numberText,
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out var number)
        || !double.IsFinite(number))
    {
        throw new ArgumentException($"Invalid target '{value}'. Expected name=value.");
    }

    double? tolerance = null;
    if (toleranceSeparator >= 0)
    {
        if (!double.TryParse(
                value[(toleranceSeparator + 1)..],
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var parsedTolerance)
            || !double.IsFinite(parsedTolerance)
            || parsedTolerance < 0)
        {
            throw new ArgumentException($"Invalid tolerance in target '{value}'.");
        }

        tolerance = parsedTolerance;
    }

    return new Target(value[..separator], number, tolerance);
}

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

static float ReadFloat(ReadOnlySpan<byte> payload, int bitOffset) =>
    BitConverter.Int32BitsToSingle((int)(uint)ReadBits(payload, bitOffset, 32));

static bool IsBitSet(ReadOnlySpan<byte> payload, int bitOffset) =>
    (payload[bitOffset >> 3] & (1 << (bitOffset & 7))) != 0;

static bool IsPlausibleAttributeValue(float value) =>
    float.IsFinite(value)
    && value >= 0.01f
    && value <= 100_000f;

enum TrafficDirection : byte
{
    Unknown,
    Inbound,
    Outbound
}

sealed record OpenedDevice(ILiveDevice Device, PacketArrivalEventHandler Handler);
delegate void PacketHandler(PacketCapture packetCapture);
sealed record Target(string Name, double Value, double? Tolerance);
sealed record TrafficRecord(
    int Sequence,
    DateTimeOffset ObservedAt,
    TrafficDirection Direction,
    string SourceAddress,
    int SourcePort,
    string DestinationAddress,
    int DestinationPort,
    byte[] Payload);
sealed record FloatHit(
    int Sequence,
    DateTimeOffset ObservedAt,
    TrafficDirection Direction,
    int PayloadLength,
    int BitOffset,
    string Target,
    float Value);
sealed record AttributePair(int BitOffset, float Value, bool SeparatorBit);
