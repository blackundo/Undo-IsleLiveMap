using TheIsleOverlay.Core;

namespace TheIsleOverlay.Gacha;

/// <summary>
/// Reconstructs the sparse official Gacha feed into a monotonic state.
/// WebSocket deltas have precedence over /me while they are fresh; an older
/// API response can only fill fields that the live stream has not supplied.
/// Explicit server changes reset the dino state so stats cannot cross a
/// reconnect, respawn, or server switch.
/// </summary>
public sealed class GachaStatsReducer
{
    // The official Gacha feed is sparse: in live measurements a valid
    // heartbeat/stat frame can be 7–8 seconds apart.  Six seconds caused the
    // HUD to oscillate LIVE/DATA STALE even while the WebSocket was healthy.
    // Keep the explicit connection-ended signal as the fast stale path and
    // use a 15-second silence window for an open but quiet socket.
    public static readonly TimeSpan DefaultLiveDataLifetime = TimeSpan.FromSeconds(15);
    public static readonly TimeSpan DefaultApiDataLifetime = TimeSpan.FromMinutes(2);

    private readonly TimeSpan _liveDataLifetime;
    private readonly TimeSpan _apiDataLifetime;
    private State _state = new();
    private DateTimeOffset? _lastLiveAt;
    // A valid sparse frame (including position:null) proves that the
    // authenticated WebSocket is still alive even when no stat field changed.
    // Keep this separate from _lastLiveAt so a quiet data delta cannot make a
    // healthy transport flicker into DATA STALE.
    private DateTimeOffset? _lastTransportAt;
    private DateTimeOffset? _lastApiAt;
    private long? _lastSequence;
    private string? _activeDinoIdentity;
    private bool _awaitingFreshDino;
    private bool _readySeen;

    public GachaStatsReducer(
        TimeSpan? liveDataLifetime = null,
        TimeSpan? apiDataLifetime = null)
    {
        _liveDataLifetime = liveDataLifetime ?? DefaultLiveDataLifetime;
        _apiDataLifetime = apiDataLifetime ?? DefaultApiDataLifetime;
        if (_liveDataLifetime <= TimeSpan.Zero || _apiDataLifetime <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(liveDataLifetime),
                "Gacha freshness windows must be positive.");
        }
    }

    public string? ActiveServerId => _state.ServerId;

    public void Reset()
    {
        _state = new();
        _lastLiveAt = null;
        _lastTransportAt = null;
        _lastApiAt = null;
        _lastSequence = null;
        _activeDinoIdentity = null;
        _awaitingFreshDino = false;
        _readySeen = false;
    }

    public void ApplyApi(GachaOverlayMeDto me, DateTimeOffset observedAt)
    {
        ArgumentNullException.ThrowIfNull(me);

        var serverId = NormalizeText(me.ServerId) ?? NormalizeText(me.Server);
        EnsureServer(serverId);

        var liveFresh = IsFresh(
            _lastTransportAt ?? _lastLiveAt,
            observedAt,
            _liveDataLifetime);
        var incomingFound = me.HasDino;
        // /me is a polling snapshot and can briefly report offline while the
        // WebSocket is still carrying a valid dino. Never erase fresh live
        // state with that transient response.
        if (liveFresh && incomingFound == false)
        {
            incomingFound = null;
        }

        var incomingIdentity = FirstIdentity(me.DinoId, me.ActorId, me.PawnId);
        if (incomingIdentity is not null && _activeDinoIdentity is not null
            && !string.Equals(incomingIdentity, _activeDinoIdentity, StringComparison.Ordinal))
        {
            ClearDinoState();
        }
        else if (incomingFound == true && _awaitingFreshDino)
        {
            ClearDinoState();
        }

        // A false /me result is a transient miss while the live lane is
        // fresh, or an authoritative no-dino result once that lane is quiet.
        // In both cases its accompanying fields must not be allowed to
        // reintroduce stale vitals into the merge.
        var incoming = me.HasDino == false
            ? new ExactVitals()
            : ToValidatedVitals(me);
        var existing = _state.Vitals;
        var mergedVitals = liveFresh ? MergeMissing(existing, incoming) : Merge(existing, incoming);
        if (!liveFresh && me.HasDino == false)
        {
            // An API response is authoritative only after the live lane has
            // gone quiet. This prevents a transient /me miss from erasing a
            // valid WebSocket dino, while still clearing stats after a real
            // respawn/logout.
            ClearDinoState();
            existing = _state.Vitals;
            mergedVitals = new ExactVitals();
            _awaitingFreshDino = true;
        }

        if (me.HasDino == true
            || me.HasDino is null && incoming.HasAnyValue())
        {
            _activeDinoIdentity = incomingIdentity ?? _activeDinoIdentity;
        }
        else if (me.HasDino == false && !liveFresh)
        {
            // Do not retain an identity supplied by a stale /me response
            // after it has explicitly reported that no dino is active.
            _activeDinoIdentity = null;
        }
        if (incomingFound == true)
        {
            _awaitingFreshDino = false;
        }
        _state = _state with
        {
            Online = liveFresh ? _state.Online || me.Online == true : me.Online ?? _state.Online,
            AuthoritativeOffline = incomingFound == true || incoming.HasAnyValue()
                ? false
                : _state.AuthoritativeOffline,
            HasDino = (incomingFound ?? _state.HasDino) || incoming.HasAnyValue(),
            SteamId = NormalizeSteamId(me.SteamId) ?? _state.SteamId,
            PersonaName = NormalizeText(me.PersonaName) ?? NormalizeText(me.Name) ?? _state.PersonaName,
            ServerId = serverId ?? _state.ServerId,
            ServerName = NormalizeText(me.Server) ?? _state.ServerName,
            Species = NormalizeText(me.DinoName) ?? NormalizeText(me.Species) ?? _state.Species,
            Vitals = mergedVitals,
            Nutrition = Merge(
                _state.Nutrition,
                new GachaDinoStatsDto
                {
                    Carb = me.Carb,
                    Protein = me.Protein,
                    Lipid = me.Lipid,
                    Nutrition = me.Nutrition
                }),
            Prime = Merge(_state.Prime, me.Prime),
            Source = liveFresh && _state.Source == GachaStatsSource.OfficialWebSocket
                ? GachaStatsSource.OfficialWebSocket
                : GachaStatsSource.OfficialApi,
            Confidence = ComputeConfidence(mergedVitals),
            ObservedAt = observedAt,
            ReceivedAt = observedAt,
            StatusMessage = null
        };
        _lastApiAt = observedAt;
    }

    public bool ApplyFrame(GachaOverlayFrameDto frame, DateTimeOffset observedAt)
    {
        ArgumentNullException.ThrowIfNull(frame);
        var type = NormalizeFrameType(frame.Type ?? frame.ShortType);
        if (type is null)
        {
            return false;
        }

        var sequence = frame.Sequence ?? frame.ShortSequence;
        var previousServerId = _state.ServerId;
        var previousSequence = _lastSequence;
        var serverId = NormalizeText(frame.ServerId) ?? NormalizeText(frame.Server);
        var isReady = type == "overlay.ready";
        var sameServerReady = isReady
                              && !string.IsNullOrWhiteSpace(serverId)
                              && string.Equals(
                                  previousServerId,
                                  serverId,
                                  StringComparison.Ordinal);
        var reconnectEpoch = sameServerReady
                             && (_readySeen || _lastLiveAt is not null);

        // Once the stream has authoritatively told us that the player left,
        // late position/health/dino frames from the old socket are ignored.
        // A new stream must first send its ready handshake.
        if (_state.AuthoritativeOffline && !isReady)
        {
            // Report the frame as consumed so a host can publish the current
            // offline snapshot and keep its diagnostics counters accurate;
            // importantly, do not mutate sequence, timestamps, or state.
            return true;
        }

        // The official socket starts a fresh sequence on reconnect.  A
        // repeated ready frame is the protocol's epoch boundary; discard the
        // previous sequence and dino cache before accepting the new stream.
        // This prevents a reconnect with seq=1 from being rejected forever by
        // a prior stream that ended at a larger sequence.
        if (reconnectEpoch)
        {
            _lastSequence = null;
            _lastLiveAt = null;
            ClearDinoState();
            _awaitingFreshDino = true;
        }
        else if (sameServerReady)
        {
            // The first ready frame can follow the initial /me bootstrap.
            // Start its sequence epoch but retain the API snapshot until a
            // live dino frame arrives.
            _lastSequence = null;
        }
        if (sequence is null
            && _lastLiveAt is { } previousLiveAt
            && observedAt < previousLiveAt)
        {
            return false;
        }
        if (!isReady
            && serverId is not null
            && previousServerId is not null
            && !string.Equals(previousServerId, serverId, StringComparison.Ordinal)
            && sequence is { } changedServerSequence
            && previousSequence is { } previousServerSequence
            && changedServerSequence <= previousServerSequence)
        {
            // A late frame from a previous server must not reset the active
            // session. A new server is accepted through a ready frame (or a
            // strictly newer sequence).
            return false;
        }
        if (serverId is not null)
        {
            EnsureServer(serverId);
        }

        if (!isReady
            && sequence is { } next
            && _lastSequence is { } previous
            && next <= previous)
        {
            return false;
        }

        if (sequence is { } acceptedSequence)
        {
            _lastSequence = acceptedSequence;
        }

        // The frame was syntactically valid, passed sequence ordering, and
        // belongs to the current server. Count it as a transport heartbeat
        // even when its sparse payload contains no changed values.
        _lastTransportAt = observedAt;

        if (type == "overlay.ready")
        {
            if (string.IsNullOrWhiteSpace(serverId))
            {
                // This is the only authoritative live "left server" signal.
                Reset();
                _state = _state with
                {
                    Online = false,
                    AuthoritativeOffline = true,
                    ObservedAt = observedAt,
                    ReceivedAt = observedAt,
                    Source = GachaStatsSource.OfficialWebSocket,
                    StatusMessage = "Gacha · CHƯA VÀO SERVER"
                };
                _readySeen = false;
                return true;
            }

            EnsureServer(serverId);
            _state = _state with
            {
                Online = true,
                AuthoritativeOffline = false,
                ServerId = serverId,
                ObservedAt = observedAt,
                ReceivedAt = observedAt,
                Source = GachaStatsSource.OfficialWebSocket,
                Sequence = sequence,
                StatusMessage = null
            };
            _lastSequence = sequence;
            _lastLiveAt = observedAt;
            _readySeen = true;
            return true;
        }

        var changed = false;
        var nextVitals = _state.Vitals;
        var nextSpecies = _state.Species;
        var nextHasDino = _state.HasDino;
        var nextNutrition = _state.Nutrition;
        var nextPrime = _state.Prime;
        var frameIdentity = FirstIdentity(frame.DinoId, frame.ActorId, frame.PawnId);

        // An actor/pawn id is stronger than a sparse stats delta.  Process it
        // before the per-frame branch so a position/health frame cannot carry
        // vitals across a respawn merely because it omitted `stats`.
        var frameIdentityChanged = frameIdentity is not null
                                   && _activeDinoIdentity is not null
                                   && !string.Equals(
                                       frameIdentity,
                                       _activeDinoIdentity,
                                       StringComparison.Ordinal);
        if (frameIdentityChanged)
        {
            ClearDinoState();
            nextVitals = new ExactVitals();
            nextNutrition = null;
            nextPrime = null;
            nextSpecies = null;
            nextHasDino = false;
            changed = true;
        }

        if (type == "overlay.dino")
        {
            var stats = frame.Stats ?? frame.Dino;
            if (stats is not null)
            {
                var validatedVitals = ToValidatedVitals(stats);
                var species = NormalizeText(stats.DinoName) ?? NormalizeText(stats.Species);
                var dinoIdentity = FirstIdentity(
                    stats.DinoId,
                    stats.ActorId,
                    stats.PawnId,
                    frame.DinoId,
                    frame.ActorId,
                    frame.PawnId);
                var identityChanged = dinoIdentity is not null
                                      && _activeDinoIdentity is not null
                                      && !string.Equals(
                                          dinoIdentity,
                                          _activeDinoIdentity,
                                          StringComparison.Ordinal);
                var speciesChanged = dinoIdentity is null
                                     && species is not null
                                     && nextSpecies is not null
                                     && !string.Equals(
                                         species,
                                         nextSpecies,
                                         StringComparison.OrdinalIgnoreCase);
                var knownDino = HasKnownDino(
                    nextHasDino,
                    nextSpecies,
                    nextVitals,
                    _activeDinoIdentity);

                if (identityChanged || frameIdentityChanged
                    || stats.Found == true
                       && (_awaitingFreshDino || speciesChanged))
                {
                    // A new dino/respawn must not inherit old vital values.
                    ClearDinoState();
                    nextVitals = new ExactVitals();
                    nextNutrition = null;
                    nextPrime = null;
                    nextSpecies = null;
                    nextHasDino = false;
                }

                // The official overlay deliberately retains the last found
                // dino when a sparse poll emits found:false.  Treat that
                // value as transient while a known pawn/position exists;
                // otherwise the HUD flickers off until the next position
                // frame.  A first-ever found:false still establishes the
                // waiting state, and a changed identity is handled above.
                var transientNotFound = stats.Found == false
                                         && knownDino
                                         && !identityChanged
                                         && !frameIdentityChanged;
                if (!transientNotFound)
                {
                    if (stats.Found == true)
                    {
                        _activeDinoIdentity = dinoIdentity ?? _activeDinoIdentity;
                        _awaitingFreshDino = false;
                    }
                    else if (stats.Found == false)
                    {
                        _activeDinoIdentity = null;
                        _awaitingFreshDino = true;
                    }

                    nextSpecies = species ?? nextSpecies;
                    nextHasDino = stats.Found switch
                    {
                        true => true,
                        false => false,
                        _ => nextHasDino || validatedVitals.HasAnyValue() || species is not null
                    };
                    nextVitals = Merge(nextVitals, validatedVitals);
                    nextNutrition = Merge(nextNutrition, stats);
                    changed = validatedVitals.HasAnyValue()
                              || species is not null
                              || stats.Found is not null;
                }
            }

            if (frame.HasDino is true)
            {
                nextHasDino = true;
                changed = true;
            }

            var previousPrime = nextPrime;
            nextPrime = Merge(nextPrime, frame.Prime);
            changed |= !Equals(previousPrime, nextPrime);
        }
        else if (type == "overlay.health"
                 && frame.Health is not null
                 && HasKnownDino(nextHasDino, nextSpecies, nextVitals, _activeDinoIdentity))
        {
            var health = ToValidatedVitals(frame.Health);
            nextVitals = nextVitals with
            {
                Health = health.Health ?? nextVitals.Health,
                MaxHealth = health.MaxHealth ?? nextVitals.MaxHealth
            };
            changed = health.Health is not null || health.MaxHealth is not null;
        }
        else if (type == "overlay.position" && frame.Position is not null)
        {
            var position = ToLocation(frame.Position);
            if (position is not null)
            {
                _state = _state with
                {
                    Location = position,
                    UnrealYaw = ValidNumber(frame.Position.Yaw) ?? _state.UnrealYaw
                };
                nextHasDino = true;
                changed = true;
            }
        }
        else if (type == "overlay.notification")
        {
            changed = !string.IsNullOrWhiteSpace(frame.Error);
        }

        if (!changed)
        {
            return false;
        }

        _state = _state with
        {
            Online = true,
            AuthoritativeOffline = false,
            HasDino = nextHasDino,
            SteamId = NormalizeSteamId(frame.SteamId) ?? _state.SteamId,
            ServerId = serverId ?? _state.ServerId,
            Species = nextSpecies,
            Vitals = nextVitals,
            Nutrition = nextNutrition,
            Prime = nextPrime,
            Source = GachaStatsSource.OfficialWebSocket,
            Confidence = ComputeConfidence(nextVitals),
            Sequence = sequence ?? _state.Sequence,
            ObservedAt = observedAt,
            ReceivedAt = observedAt,
            StatusMessage = NormalizeText(frame.Error)
        };
        if (frameIdentity is not null && _state.HasDino)
        {
            _activeDinoIdentity = frameIdentity;
        }
        _lastLiveAt = observedAt;
        return true;
    }

    public GachaStatsSnapshot BuildSnapshot(DateTimeOffset now)
    {
        var liveStale = !IsFresh(
            _lastTransportAt ?? _lastLiveAt,
            now,
            _liveDataLifetime);
        var apiStale = !IsFresh(_lastApiAt, now, _apiDataLifetime);
        var stale = !_state.AuthoritativeOffline && liveStale && apiStale;
        var status = stale
            ? "Gacha · ĐANG CHỜ ĐỒNG BỘ STATS"
            : _state.StatusMessage;
        return new GachaStatsSnapshot
        {
            Online = _state.Online && !(_state.AuthoritativeOffline || !_state.HasDino && stale),
            HasDino = _state.HasDino && !stale,
            SteamId = _state.SteamId,
            PersonaName = _state.PersonaName,
            ServerId = _state.ServerId,
            ServerName = _state.ServerName,
            Species = _state.Species,
            Vitals = _state.Vitals,
            Nutrition = _state.Nutrition,
            Prime = _state.Prime,
            Location = _state.Location,
            UnrealYaw = _state.UnrealYaw,
            Source = _state.Source,
            Confidence = _state.Confidence,
            ObservedAt = _state.ObservedAt,
            ReceivedAt = _state.ReceivedAt,
            Sequence = _state.Sequence,
            IsStale = stale,
            StatusMessage = status
        };
    }

    private void EnsureServer(string? serverId)
    {
        if (string.IsNullOrWhiteSpace(serverId)
            || string.IsNullOrWhiteSpace(_state.ServerId)
            || string.Equals(_state.ServerId, serverId, StringComparison.Ordinal))
        {
            return;
        }

        _state = new State { ServerId = serverId };
        _lastLiveAt = null;
        _lastTransportAt = null;
        _lastApiAt = null;
        _lastSequence = null;
        _activeDinoIdentity = null;
        _awaitingFreshDino = false;
        _readySeen = false;
    }

    private void ClearDinoState()
    {
        _state = _state with
        {
            HasDino = false,
            Species = null,
            Vitals = new ExactVitals(),
            Nutrition = null,
            Prime = null,
            Location = null,
            UnrealYaw = null,
            Confidence = GachaStatsConfidence.None
        };
        _activeDinoIdentity = null;
    }

    private static string? NormalizeFrameType(string? value)
    {
        var normalized = NormalizeText(value)?.ToLowerInvariant();
        return normalized switch
        {
            "overlay.ready" or "ready" => "overlay.ready",
            "overlay.dino" or "dino" => "overlay.dino",
            "overlay.health" or "health" => "overlay.health",
            "overlay.position" or "position" => "overlay.position",
            "overlay.notification" or "notification" => "overlay.notification",
            _ => null
        };
    }

    private static ExactVitals ToValidatedVitals(GachaOverlayMeDto me) =>
        ToValidatedVitals(new GachaDinoStatsDto
        {
            Growth = me.Growth,
            Health = me.Health,
            MaxHealth = me.MaxHealth,
            Hunger = me.Hunger,
            MaxHunger = me.MaxHunger,
            Thirst = me.Thirst ?? me.Water,
            MaxThirst = me.MaxThirst ?? me.MaxWater,
            Stamina = me.Stamina,
            MaxStamina = me.MaxStamina,
            FoodValue = me.FoodValue,
            MaxFoodValue = me.MaxFoodValue,
            Carb = me.Carb,
            Protein = me.Protein,
            Lipid = me.Lipid,
            Nutrition = me.Nutrition
        });

    private static ExactVitals ToValidatedVitals(GachaDinoStatsDto stats) => new()
    {
        Growth = ValidGrowth(stats.Growth),
        Health = ValidCurrent(stats.Health),
        MaxHealth = ValidMaximum(stats.MaxHealth),
        Hunger = ValidCurrent(stats.Hunger),
        MaxHunger = ValidMaximum(stats.MaxHunger),
        Thirst = ValidCurrent(stats.Thirst ?? stats.Water),
        MaxThirst = ValidMaximum(stats.MaxThirst ?? stats.MaxWater),
        Stamina = ValidCurrent(stats.Stamina),
        MaxStamina = ValidMaximum(stats.MaxStamina),
        FoodValue = ValidCurrent(stats.FoodValue),
        MaxFoodValue = ValidMaximum(stats.MaxFoodValue)
    };

    private static ExactVitals ToValidatedVitals(GachaHealthDto health) => new()
    {
        Health = ValidCurrent(health.Health),
        MaxHealth = ValidMaximum(health.MaxHealth)
    };

    private static ExactVitals Merge(ExactVitals current, ExactVitals incoming) => current with
    {
        Growth = incoming.Growth ?? current.Growth,
        Health = incoming.Health ?? current.Health,
        MaxHealth = incoming.MaxHealth ?? current.MaxHealth,
        Stamina = incoming.Stamina ?? current.Stamina,
        MaxStamina = incoming.MaxStamina ?? current.MaxStamina,
        Hunger = incoming.Hunger ?? current.Hunger,
        MaxHunger = incoming.MaxHunger ?? current.MaxHunger,
        Thirst = incoming.Thirst ?? current.Thirst,
        MaxThirst = incoming.MaxThirst ?? current.MaxThirst,
        FoodValue = incoming.FoodValue ?? current.FoodValue,
        MaxFoodValue = incoming.MaxFoodValue ?? current.MaxFoodValue
    };

    private static ExactVitals MergeMissing(ExactVitals current, ExactVitals incoming) => current with
    {
        Growth = current.Growth ?? incoming.Growth,
        Health = current.Health ?? incoming.Health,
        MaxHealth = current.MaxHealth ?? incoming.MaxHealth,
        Stamina = current.Stamina ?? incoming.Stamina,
        MaxStamina = current.MaxStamina ?? incoming.MaxStamina,
        Hunger = current.Hunger ?? incoming.Hunger,
        MaxHunger = current.MaxHunger ?? incoming.MaxHunger,
        Thirst = current.Thirst ?? incoming.Thirst,
        MaxThirst = current.MaxThirst ?? incoming.MaxThirst,
        FoodValue = current.FoodValue ?? incoming.FoodValue,
        MaxFoodValue = current.MaxFoodValue ?? incoming.MaxFoodValue
    };

    private static NutritionTelemetry? Merge(NutritionTelemetry? current, GachaNutritionDto? incoming)
    {
        if (incoming is null && current is null)
        {
            return null;
        }

        return new NutritionTelemetry
        {
            Carb = ValidCurrent(incoming?.Carb) ?? current?.Carb,
            Protein = ValidCurrent(incoming?.Protein) ?? current?.Protein,
            Lipid = ValidCurrent(incoming?.Lipid) ?? current?.Lipid
        };
    }

    private static NutritionTelemetry? Merge(
        NutritionTelemetry? current,
        GachaDinoStatsDto incoming)
    {
        var nested = Merge(current, incoming.Nutrition);
        if (incoming.Carb is null && incoming.Protein is null && incoming.Lipid is null)
        {
            return nested;
        }

        return new NutritionTelemetry
        {
            Carb = ValidCurrent(incoming.Carb) ?? nested?.Carb,
            Protein = ValidCurrent(incoming.Protein) ?? nested?.Protein,
            Lipid = ValidCurrent(incoming.Lipid) ?? nested?.Lipid
        };
    }

    private static PrimeTelemetry? Merge(PrimeTelemetry? current, GachaPrimeDto? incoming) =>
        incoming is null
            ? current
            : new PrimeTelemetry
            {
                Elder = incoming.Elder ?? current?.Elder,
                IsPrime = incoming.Elder ?? current?.IsPrime,
                Eligible = incoming.Eligible ?? current?.Eligible,
                Done = incoming.Done ?? current?.Done,
                Required = incoming.Required ?? current?.Required,
                Progress = incoming.Done is { } done
                           && incoming.Required is { } required
                           && required > 0
                    ? Math.Clamp((double)done / required * 100d, 0d, 100d)
                    : current?.Progress,
                Quests = current?.Quests ?? []
            };

    private static WorldLocation? ToLocation(GachaOverlayPositionDto position) =>
        ValidNumber(position.X) is { } x && ValidNumber(position.Y) is { } y
            ? new WorldLocation { X = x, Y = y, Z = ValidNumber(position.Z) }
            : null;

    private static GachaStatsConfidence ComputeConfidence(ExactVitals vitals) =>
        vitals.Health is not null && vitals.MaxHealth is > 0d
        || vitals.Stamina is not null && vitals.MaxStamina is > 0d
        || vitals.Hunger is not null && vitals.MaxHunger is > 0d
        || vitals.Thirst is not null && vitals.MaxThirst is > 0d
            ? GachaStatsConfidence.Verified
            : vitals.HasAnyValue()
                ? GachaStatsConfidence.Partial
                : GachaStatsConfidence.None;

    private static double? ValidGrowth(double? value) =>
        ValidNumber(value) is { } growth && growth is >= 0d and <= 1d
            ? growth
            : null;

    private static string? FirstIdentity(params string?[] values) =>
        values.Select(NormalizeText).FirstOrDefault(value => value is not null);

    private static double? ValidCurrent(double? value) =>
        ValidNumber(value) is { } current && current is >= 0d and <= 1_000_000_000d
            ? current
            : null;

    private static double? ValidMaximum(double? value) =>
        ValidNumber(value) is { } maximum && maximum is > 0d and <= 1_000_000_000d
            ? maximum
            : null;

    private static double? ValidNumber(double? value) =>
        value is { } number && double.IsFinite(number) ? number : null;

    private static bool HasKnownDino(
        bool hasDino,
        string? species,
        ExactVitals vitals,
        string? identity) =>
        hasDino
        || species is not null
        || identity is not null
        || vitals.HasAnyValue();

    private static bool IsFresh(DateTimeOffset? timestamp, DateTimeOffset now, TimeSpan lifetime) =>
        timestamp is { } value && now >= value && now - value <= lifetime;

    private static string? NormalizeText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = value.Trim();
        return normalized.Length <= 256 && !normalized.Any(char.IsControl)
            ? normalized
            : null;
    }

    private static string? NormalizeSteamId(string? value)
    {
        var normalized = NormalizeText(value);
        return normalized is { Length: 17 } && normalized.All(char.IsDigit)
            ? normalized
            : null;
    }

    private sealed record State
    {
        public bool Online { get; init; }
        public bool AuthoritativeOffline { get; init; }
        public bool HasDino { get; init; }
        public string? SteamId { get; init; }
        public string? PersonaName { get; init; }
        public string? ServerId { get; init; }
        public string? ServerName { get; init; }
        public string? Species { get; init; }
        public ExactVitals Vitals { get; init; } = new();
        public NutritionTelemetry? Nutrition { get; init; }
        public PrimeTelemetry? Prime { get; init; }
        public WorldLocation? Location { get; init; }
        public double? UnrealYaw { get; init; }
        public GachaStatsSource Source { get; init; }
        public GachaStatsConfidence Confidence { get; init; }
        public DateTimeOffset? ObservedAt { get; init; }
        public DateTimeOffset? ReceivedAt { get; init; }
        public long? Sequence { get; init; }
        public string? StatusMessage { get; init; }
    }
}

internal static class GachaExactVitalsExtensions
{
    public static bool HasAnyValue(this ExactVitals vitals) =>
        vitals.Growth is not null
        || vitals.Health is not null
        || vitals.MaxHealth is not null
        || vitals.Stamina is not null
        || vitals.MaxStamina is not null
        || vitals.Hunger is not null
        || vitals.MaxHunger is not null
        || vitals.Thirst is not null
        || vitals.MaxThirst is not null
        || vitals.FoodValue is not null
        || vitals.MaxFoodValue is not null;
}
