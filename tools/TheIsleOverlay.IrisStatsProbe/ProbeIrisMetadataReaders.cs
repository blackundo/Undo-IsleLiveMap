using System.Text;
using TheIsleOverlay.Core;
using TheIsleOverlay.LocalTelemetry;

/// <summary>
/// Probe-local Iris export reader. This deliberately lives in the diagnostic tool rather than
/// production telemetry: the format is reverse-engineering support and is not yet a stable API.
/// </summary>
internal sealed class ProbeIrisObjectExportTracker
{
    private const int MaximumRecordsPerList = 8_192;
    private const int MaximumReferenceDepth = 32;

    private readonly Dictionary<TokenKey, string> _strings = [];
    private readonly Dictionary<ulong, ProbeIrisObjectReferenceExport> _objects = [];

    public IReadOnlyList<ProbeIrisObjectReferenceExport> Observe(
        ReadOnlySpan<byte> payload,
        UnrealIrisReplicationBatch batch)
    {
        if (!batch.HasExports
            || batch.DataBitOffset < 0
            || batch.DataBitCount <= 0
            || batch.DataBitOffset > payload.Length * 8 - batch.DataBitCount)
        {
            return [];
        }

        // Export lists follow the replication payload. Parse into temporary dictionaries so a
        // malformed/truncated export cannot partially poison state used by later packets.
        var reader = new BitReader(payload, batch.DataBitOffset + batch.DataBitCount, payload.Length * 8);
        var pendingStrings = new Dictionary<TokenKey, string>();
        var pendingObjects = new Dictionary<ulong, ProbeIrisObjectReferenceExport>();
        if (!TryReadNetTokenExports(ref reader, pendingStrings)
            || !TryReadObjectReferenceExports(ref reader, pendingStrings, pendingObjects)
            || !TryReadMustBeMappedHandles(ref reader))
        {
            return [];
        }

        foreach (var item in pendingStrings)
        {
            _strings[item.Key] = item.Value;
        }

        foreach (var item in pendingObjects)
        {
            _objects[item.Key] = item.Value;
        }

        return pendingObjects.Values.ToArray();
    }

    public bool TryGetObject(ulong netRefHandle, out ProbeIrisObjectReferenceExport objectReference) =>
        _objects.TryGetValue(netRefHandle, out objectReference);

    private bool TryReadNetTokenExports(
        ref BitReader reader,
        IDictionary<TokenKey, string> pendingStrings)
    {
        for (var index = 0; index < MaximumRecordsPerList; index++)
        {
            if (!reader.TryReadBits(1, out var hasRecord))
            {
                return false;
            }

            if (hasRecord == 0)
            {
                return true;
            }

            if (!TryReadToken(ref reader, includesTypeId: true, out var token)
                || token.Index == 0
                || !reader.TryReadString(out var value))
            {
                return false;
            }

            pendingStrings[token] = value;
        }

        return false;
    }

    private bool TryReadObjectReferenceExports(
        ref BitReader reader,
        IDictionary<TokenKey, string> pendingStrings,
        IDictionary<ulong, ProbeIrisObjectReferenceExport> pendingObjects)
    {
        for (var index = 0; index < MaximumRecordsPerList; index++)
        {
            if (!reader.TryReadBits(1, out var hasRecord))
            {
                return false;
            }

            if (hasRecord == 0)
            {
                return true;
            }

            if (!TryReadFullObjectReference(
                    ref reader,
                    pendingStrings,
                    pendingObjects,
                    depth: 0,
                    out _))
            {
                return false;
            }
        }

        return false;
    }

    private static bool TryReadMustBeMappedHandles(ref BitReader reader)
    {
        for (var index = 0; index < MaximumRecordsPerList; index++)
        {
            if (!reader.TryReadBits(1, out var hasRecord))
            {
                return false;
            }

            if (hasRecord == 0)
            {
                return true;
            }

            if (!reader.TryReadNetRefHandle(out var handle) || handle == 0)
            {
                return false;
            }
        }

        return false;
    }

    private bool TryReadFullObjectReference(
        ref BitReader reader,
        IDictionary<TokenKey, string> pendingStrings,
        IDictionary<ulong, ProbeIrisObjectReferenceExport> pendingObjects,
        int depth,
        out ProbeIrisObjectReferenceExport objectReference)
    {
        objectReference = default;
        if (depth > MaximumReferenceDepth || !reader.TryReadBits(1, out var isClientReference))
        {
            return false;
        }

        if (isClientReference != 0)
        {
            if (!reader.TryReadNetRefHandle(out var handle)
                || !TryReadToken(ref reader, includesTypeId: false, out var token)
                || !TryReadConditionalString(ref reader, token, pendingStrings, out var relativePath))
            {
                return false;
            }

            objectReference = new ProbeIrisObjectReferenceExport(handle, relativePath, 0, relativePath);
            if (handle != 0 && !string.IsNullOrWhiteSpace(relativePath))
            {
                pendingObjects[handle] = objectReference;
            }

            return true;
        }

        return TryReadObjectReferenceBody(
            ref reader,
            pendingStrings,
            pendingObjects,
            depth,
            out objectReference);
    }

    private bool TryReadObjectReferenceBody(
        ref BitReader reader,
        IDictionary<TokenKey, string> pendingStrings,
        IDictionary<ulong, ProbeIrisObjectReferenceExport> pendingObjects,
        int depth,
        out ProbeIrisObjectReferenceExport objectReference)
    {
        objectReference = default;
        if (depth > MaximumReferenceDepth || !reader.TryReadNetRefHandle(out var handle))
        {
            return false;
        }

        if (handle == 0)
        {
            return true;
        }

        if (!reader.TryReadBits(1, out var isExported))
        {
            return false;
        }

        if (isExported == 0)
        {
            return TryResolveObject(handle, pendingObjects, out objectReference);
        }

        // bIsPackageObject is not needed by this diagnostic, but it is present in the stream.
        if (!reader.TrySkip(1) || !reader.TryReadBits(1, out var hasPath))
        {
            return false;
        }

        var relativePath = string.Empty;
        ulong outerHandle = 0;
        string? outerPath = null;
        if (hasPath != 0)
        {
            if (!TryReadToken(ref reader, includesTypeId: false, out var token)
                || !TryReadConditionalString(ref reader, token, pendingStrings, out relativePath)
                || !TryReadObjectReferenceBody(
                    ref reader,
                    pendingStrings,
                    pendingObjects,
                    depth + 1,
                    out var outer))
            {
                return false;
            }

            outerHandle = outer.NetRefHandle;
            outerPath = outer.FullPath;
        }

        var fullPath = string.IsNullOrWhiteSpace(outerPath)
            ? relativePath
            : string.IsNullOrWhiteSpace(relativePath)
                ? outerPath
                : $"{outerPath}.{relativePath}";
        objectReference = new ProbeIrisObjectReferenceExport(handle, relativePath, outerHandle, fullPath);
        if (!string.IsNullOrWhiteSpace(fullPath))
        {
            pendingObjects[handle] = objectReference;
        }

        return true;
    }

    private bool TryReadConditionalString(
        ref BitReader reader,
        TokenKey token,
        IDictionary<TokenKey, string> pendingStrings,
        out string value)
    {
        value = string.Empty;
        if (!reader.TryReadBits(1, out var hasInlineString))
        {
            return false;
        }

        if (hasInlineString != 0)
        {
            if (!reader.TryReadString(out value))
            {
                return false;
            }

            if (token.Index != 0)
            {
                pendingStrings[token] = value;
            }

            return true;
        }

        if (token.Index == 0)
        {
            return true;
        }

        return pendingStrings.TryGetValue(token, out value!) || _strings.TryGetValue(token, out value!);
    }

    private bool TryResolveObject(
        ulong handle,
        IDictionary<ulong, ProbeIrisObjectReferenceExport> pendingObjects,
        out ProbeIrisObjectReferenceExport objectReference)
    {
        if (pendingObjects.TryGetValue(handle, out objectReference)
            || _objects.TryGetValue(handle, out objectReference))
        {
            return true;
        }

        objectReference = new ProbeIrisObjectReferenceExport(handle, string.Empty, 0, string.Empty);
        return true;
    }

    private static bool TryReadToken(ref BitReader reader, bool includesTypeId, out TokenKey token)
    {
        token = default;
        if (!reader.TryReadPackedUInt32(out var index))
        {
            return false;
        }

        if (index == 0)
        {
            return true;
        }

        if (!reader.TryReadBits(1, out var isAuthority))
        {
            return false;
        }

        ulong typeId = 0;
        if (includesTypeId && !reader.TryReadBits(3, out typeId))
        {
            return false;
        }

        token = new TokenKey(checked((uint)typeId), index, isAuthority != 0);
        return true;
    }

    private readonly record struct TokenKey(uint TypeId, ulong Index, bool IsAuthority);

    private ref struct BitReader
    {
        private readonly ReadOnlySpan<byte> _payload;
        private readonly int _limit;

        public BitReader(ReadOnlySpan<byte> payload, int position, int limit)
        {
            _payload = payload;
            _limit = limit;
            Position = position;
        }

        public int Position { get; private set; }

        public bool TrySkip(int bitCount)
        {
            if (bitCount < 0 || Position > _limit - bitCount)
            {
                return false;
            }

            Position += bitCount;
            return true;
        }

        public bool TryReadBits(int bitCount, out ulong value)
        {
            value = 0;
            if (bitCount is < 0 or > 64 || Position > _limit - bitCount)
            {
                return false;
            }

            for (var bit = 0; bit < bitCount; bit++)
            {
                var sourceBit = Position + bit;
                if ((_payload[sourceBit >> 3] & (1 << (sourceBit & 7))) != 0)
                {
                    value |= 1UL << bit;
                }
            }

            Position += bitCount;
            return true;
        }

        public bool TryReadPackedUInt32(out ulong value)
        {
            value = 0;
            return TryReadBits(2, out var byteCountMinusOne)
                && TryReadBits(checked(((int)byteCountMinusOne + 1) * 8), out value);
        }

        public bool TryReadNetRefHandle(out ulong handle)
        {
            handle = 0;
            if (!TryReadBits(1, out var isValid))
            {
                return false;
            }

            if (isValid == 0)
            {
                return true;
            }

            return TryReadBits(3, out var byteCountMinusOne)
                && TryReadBits(checked(((int)byteCountMinusOne + 1) * 8), out handle)
                && handle != 0;
        }

        public bool TryReadString(out string value)
        {
            value = string.Empty;
            if (!TryReadBits(1, out _)
                || !TryReadBits(16, out var byteCount)
                || byteCount > ushort.MaxValue
                || Position > _limit - checked((int)byteCount * 8))
            {
                return false;
            }

            var bytes = new byte[checked((int)byteCount)];
            for (var index = 0; index < bytes.Length; index++)
            {
                if (!TryReadBits(8, out var nextByte))
                {
                    return false;
                }

                bytes[index] = checked((byte)nextByte);
            }

            value = Encoding.UTF8.GetString(bytes);
            return true;
        }
    }
}

internal sealed class ProbeUnrealIrisActorCreationScanner
{
    private const int ActorFactoryId = 0;

    public bool TryRead(
        ReadOnlySpan<byte> payload,
        UnrealIrisReplicationBatch batch,
        out ProbeUnrealIrisActorCreation creation)
    {
        creation = default;
        if (!batch.HasOwnerData
            || batch.DataBitOffset < 0
            || batch.DataBitCount <= 0
            || batch.DataBitOffset > payload.Length * 8 - batch.DataBitCount)
        {
            return false;
        }

        var reader = new BitReader(payload, batch.DataBitOffset, batch.DataBitOffset + batch.DataBitCount);
        if (!reader.TrySkip(3)
            || !reader.TryReadBits(1, out var hasState)
            || hasState == 0
            || !reader.TryReadBits(1, out var isCreation)
            || isCreation == 0
            || !reader.TryReadBits(1, out var hasBaseline)
            || (hasBaseline != 0 && !reader.TrySkip(2))
            || !reader.TryReadBits(3, out var factoryId)
            || factoryId != ActorFactoryId
            || !reader.TryReadBits(32, out var protocolId)
            || !reader.TryReadBits(1, out var hasArchetype)
            || hasArchetype == 0
            || !TryReadImportedObjectReference(ref reader, out var archetypeHandle)
            || archetypeHandle == 0
            || !reader.TryReadBits(1, out var usesPersistentLevel))
        {
            return false;
        }

        if (usesPersistentLevel == 0
            && (!TryReadImportedObjectReference(ref reader, out var levelHandle) || levelHandle == 0))
        {
            return false;
        }

        var locationWasSerialized = false;
        var location = new WorldLocation();
        if (!reader.TryReadBits(1, out var hasLocation))
        {
            return false;
        }

        if (hasLocation != 0)
        {
            if (!reader.TryReadBits(1, out var locationIsValid)
                || locationIsValid == 0
                || !TryReadNetQuantize10Vector(ref reader, out location))
            {
                return false;
            }

            locationWasSerialized = true;
        }

        creation = new ProbeUnrealIrisActorCreation(
            batch.NetRefHandle,
            checked((uint)protocolId),
            archetypeHandle,
            usesPersistentLevel != 0,
            locationWasSerialized,
            location,
            reader.Position - batch.DataBitOffset);
        return true;
    }

    private static bool TryReadImportedObjectReference(ref BitReader reader, out ulong handle)
    {
        handle = 0;
        return reader.TryReadBits(1, out var isClientReference)
            && isClientReference == 0
            && reader.TryReadBits(1, out var isExported)
            && isExported == 0
            && reader.TryReadBits(1, out var isValid)
            && isValid != 0
            && reader.TryReadPackedUInt64(out handle)
            && handle != 0;
    }

    private static bool TryReadNetQuantize10Vector(ref BitReader reader, out WorldLocation location)
    {
        location = new WorldLocation();
        if (!reader.TryReadBits(7, out var header))
        {
            return false;
        }

        var bitCount = checked((int)(header & 0x3f));
        var scaled = (header & 0x40) != 0;
        if (bitCount is < 1 or > 63
            || !reader.TryReadSigned(bitCount, out var x)
            || !reader.TryReadSigned(bitCount, out var y)
            || !reader.TryReadSigned(bitCount, out var z))
        {
            return false;
        }

        var divisor = scaled ? 8d : 1d;
        location = new WorldLocation
        {
            X = x / divisor,
            Y = y / divisor,
            Z = z / divisor
        };
        return true;
    }

    private ref struct BitReader
    {
        private readonly ReadOnlySpan<byte> _payload;
        private readonly int _limit;

        public BitReader(ReadOnlySpan<byte> payload, int position, int limit)
        {
            _payload = payload;
            _limit = limit;
            Position = position;
        }

        public int Position { get; private set; }

        public bool TrySkip(int bitCount)
        {
            if (bitCount < 0 || Position > _limit - bitCount)
            {
                return false;
            }

            Position += bitCount;
            return true;
        }

        public bool TryReadBits(int bitCount, out ulong value)
        {
            value = 0;
            if (bitCount is < 0 or > 64 || Position > _limit - bitCount)
            {
                return false;
            }

            for (var bit = 0; bit < bitCount; bit++)
            {
                var sourceBit = Position + bit;
                if ((_payload[sourceBit >> 3] & (1 << (sourceBit & 7))) != 0)
                {
                    value |= 1UL << bit;
                }
            }

            Position += bitCount;
            return true;
        }

        public bool TryReadSigned(int bitCount, out long value)
        {
            value = 0;
            if (!TryReadBits(bitCount, out var raw))
            {
                return false;
            }

            var signBit = 1UL << (bitCount - 1);
            value = unchecked((long)((raw ^ signBit) - signBit));
            return true;
        }

        public bool TryReadPackedUInt64(out ulong value)
        {
            value = 0;
            return TryReadBits(3, out var byteCountMinusOne)
                && TryReadBits(checked(((int)byteCountMinusOne + 1) * 8), out value);
        }
    }
}

internal readonly record struct ProbeIrisObjectReferenceExport(
    ulong NetRefHandle,
    string RelativePath,
    ulong OuterNetRefHandle,
    string FullPath);

internal readonly record struct ProbeUnrealIrisActorCreation(
    ulong NetRefHandle,
    uint ProtocolId,
    ulong ArchetypeNetRefHandle,
    bool UsesPersistentLevel,
    bool LocationWasSerialized,
    WorldLocation SpawnLocation,
    int SpawnLocationEndRelativeBitOffset);
