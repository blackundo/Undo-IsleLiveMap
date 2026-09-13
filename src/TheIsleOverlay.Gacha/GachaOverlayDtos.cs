using System.Text.Json;
using System.Text.Json.Serialization;

namespace TheIsleOverlay.Gacha;

public static class GachaOverlayJson
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString
    };
}

/// <summary>Wire envelope used by the official Gacha overlay WebSocket.</summary>
public sealed record GachaOverlayFrameDto
{
    [JsonPropertyName("type")]
    public string? Type { get; init; }

    [JsonPropertyName("t")]
    public string? ShortType { get; init; }

    public long? Sequence { get; init; }

    [JsonPropertyName("seq")]
    public long? ShortSequence { get; init; }

    public string? ServerId { get; init; }

    public string? Server { get; init; }

    public string? SteamId { get; init; }

    /// <summary>
    /// Optional pawn/actor identity.  Gacha deployments have used more than
    /// one name for this field; the parser accepts all aliases so a respawn
    /// cannot inherit the previous dinosaur's cached stats.
    /// </summary>
    public string? DinoId { get; init; }

    public string? ActorId { get; init; }

    public string? PawnId { get; init; }

    public GachaDinoStatsDto? Stats { get; init; }

    public GachaDinoStatsDto? Dino { get; init; }

    public GachaHealthDto? Health { get; init; }

    public GachaOverlayPositionDto? Position { get; init; }

    public GachaPrimeDto? Prime { get; init; }

    public bool? HasDino { get; init; }

    public string? Error { get; init; }
}

/// <summary>
/// The stats object sent in overlay.dino and /api/overlay/me responses.
/// Every member is nullable because the service intentionally sends sparse
/// deltas while a dino is alive.
/// </summary>
public sealed record GachaDinoStatsDto
{
    public bool? Found { get; init; }
    public string? DinoName { get; init; }
    public string? Species { get; init; }
    public string? DinoId { get; init; }
    public string? ActorId { get; init; }
    public string? PawnId { get; init; }
    public double? Growth { get; init; }
    public double? Health { get; init; }
    public double? MaxHealth { get; init; }
    public double? Hunger { get; init; }
    public double? MaxHunger { get; init; }
    public double? Thirst { get; init; }
    public double? MaxThirst { get; init; }
    // Older Gacha builds called these fields water.  They are normalized to
    // thirst by the reducer so both wire contracts remain compatible.
    public double? Water { get; init; }
    public double? MaxWater { get; init; }
    public double? Stamina { get; init; }
    public double? MaxStamina { get; init; }
    public double? FoodValue { get; init; }
    public double? MaxFoodValue { get; init; }
    // Nutrition has appeared both as a nested object and as flat fields.
    public double? Carb { get; init; }
    public double? Protein { get; init; }
    public double? Lipid { get; init; }
    public GachaNutritionDto? Nutrition { get; init; }

    public bool HasAnyValue =>
        Found is not null
        || !string.IsNullOrWhiteSpace(DinoName)
        || !string.IsNullOrWhiteSpace(Species)
        || Growth is not null
        || Health is not null
        || MaxHealth is not null
        || Hunger is not null
        || MaxHunger is not null
        || Thirst is not null
        || MaxThirst is not null
        || Water is not null
        || MaxWater is not null
        || Stamina is not null
        || MaxStamina is not null
        || FoodValue is not null
        || MaxFoodValue is not null
        || Carb is not null
        || Protein is not null
        || Lipid is not null
        || Nutrition is not null;
}

public sealed record GachaHealthDto
{
    public double? Health { get; init; }
    public double? MaxHealth { get; init; }
}

public sealed record GachaNutritionDto
{
    public double? Carb { get; init; }
    public double? Protein { get; init; }
    public double? Lipid { get; init; }
}

public sealed record GachaOverlayPositionDto
{
    public double? X { get; init; }
    public double? Y { get; init; }
    public double? Z { get; init; }
    public double? Yaw { get; init; }
}

public sealed record GachaPrimeDto
{
    public bool? Elder { get; init; }
    public bool? Eligible { get; init; }
    public int? Done { get; init; }
    public int? Required { get; init; }
}

/// <summary>Response shape for the official /api/overlay/me endpoint.</summary>
public sealed record GachaOverlayMeDto
{
    public bool? HasData { get; init; }
    public bool? Online { get; init; }
    public bool? HasDino { get; init; }
    public string? SteamId { get; init; }
    public string? PersonaName { get; init; }
    public string? Name { get; init; }
    public string? Server { get; init; }
    public string? ServerId { get; init; }
    public string? DinoId { get; init; }
    public string? ActorId { get; init; }
    public string? PawnId { get; init; }
    public string? Species { get; init; }
    public string? DinoName { get; init; }
    public double? Growth { get; init; }
    public double? Health { get; init; }
    public double? MaxHealth { get; init; }
    public double? Hunger { get; init; }
    public double? MaxHunger { get; init; }
    public double? Thirst { get; init; }
    public double? MaxThirst { get; init; }
    public double? Water { get; init; }
    public double? MaxWater { get; init; }
    public double? Stamina { get; init; }
    public double? MaxStamina { get; init; }
    public double? FoodValue { get; init; }
    public double? MaxFoodValue { get; init; }
    public double? Carb { get; init; }
    public double? Protein { get; init; }
    public double? Lipid { get; init; }
    public GachaNutritionDto? Nutrition { get; init; }
    public GachaPrimeDto? Prime { get; init; }
}

public static class GachaOverlayDtoParser
{
    /// <summary>
    /// Parses a JSON frame without assuming property casing or whether the
    /// service wrapped the payload in a <c>data</c> object. Unknown frames are
    /// returned with a null type and are safe to ignore.
    /// </summary>
    public static bool TryParseFrame(
        ReadOnlySpan<byte> utf8Json,
        out GachaOverlayFrameDto frame)
    {
        frame = new GachaOverlayFrameDto();
        try
        {
            using var document = JsonDocument.Parse(utf8Json.ToArray());
            var envelope = document.RootElement;
            var root = SelectPayload(envelope);
            if (root.ValueKind != JsonValueKind.Object || envelope.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            var type = ReadString(envelope, "type")
                       ?? ReadString(envelope, "t")
                       ?? ReadString(root, "type")
                       ?? ReadString(root, "t");
            var sequence = ReadInt64(envelope, "sequence")
                           ?? ReadInt64(envelope, "seq")
                           ?? ReadInt64(root, "sequence")
                           ?? ReadInt64(root, "seq");
            var serverId = ReadString(envelope, "serverId")
                           ?? ReadString(envelope, "server")
                           ?? ReadString(root, "serverId")
                           ?? ReadString(root, "server");
            var steamId = ReadString(envelope, "steamId") ?? ReadString(root, "steamId");
            var dinoId = ReadString(envelope, "dinoId")
                         ?? ReadString(envelope, "dino_id")
                         ?? ReadString(root, "dinoId")
                         ?? ReadString(root, "dino_id");
            var actorId = ReadString(envelope, "actorId")
                          ?? ReadString(envelope, "actor_id")
                          ?? ReadString(root, "actorId")
                          ?? ReadString(root, "actor_id");
            var pawnId = ReadString(envelope, "pawnId")
                         ?? ReadString(envelope, "pawn_id")
                         ?? ReadString(root, "pawnId")
                         ?? ReadString(root, "pawn_id");

            var statsElement = FindObject(root, "stats") ?? FindObject(root, "dino");
            var healthElement = FindObject(root, "health");
            var positionElement = FindObject(root, "position");
            var primeElement = FindObject(root, "prime");

            frame = new GachaOverlayFrameDto
            {
                Type = type,
                ShortType = type,
                Sequence = sequence,
                ShortSequence = sequence,
                ServerId = serverId,
                Server = serverId,
                SteamId = steamId,
                DinoId = dinoId,
                ActorId = actorId,
                PawnId = pawnId,
                Stats = statsElement is { } stats ? ParseStats(stats) : null,
                Dino = statsElement is { } dino ? ParseStats(dino) : null,
                Health = healthElement is { } health ? ParseHealth(health) : null,
                Position = positionElement is { } position ? ParsePosition(position) : null,
                Prime = primeElement is { } prime ? ParsePrime(prime) : null,
                HasDino = ReadBoolean(envelope, "hasDino")
                           ?? ReadBoolean(root, "hasDino")
                           ?? (statsElement is { } statsValue ? ReadBoolean(statsValue, "found") : null),
                Error = ReadString(envelope, "error") ?? ReadString(root, "error")
            };
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public static bool TryParseMe(
        ReadOnlySpan<byte> utf8Json,
        out GachaOverlayMeDto me)
    {
        me = new GachaOverlayMeDto();
        try
        {
            using var document = JsonDocument.Parse(utf8Json.ToArray());
            var envelope = document.RootElement;
            var root = SelectPayload(envelope);
            if (root.ValueKind != JsonValueKind.Object || envelope.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            var stats = FindObject(root, "stats") ?? FindObject(root, "dino");
            var statsValue = stats.GetValueOrDefault();
            me = new GachaOverlayMeDto
            {
                HasData = ReadBoolean(envelope, "hasData") ?? ReadBoolean(root, "hasData"),
                Online = ReadBoolean(envelope, "online") ?? ReadBoolean(root, "online"),
                HasDino = ReadBoolean(envelope, "hasDino")
                          ?? ReadBoolean(root, "hasDino")
                          ?? ReadBoolean(statsValue, "found"),
                SteamId = ReadString(envelope, "steamId") ?? ReadString(root, "steamId"),
                PersonaName = ReadString(envelope, "personaName") ?? ReadString(root, "personaName"),
                Name = ReadString(envelope, "name") ?? ReadString(root, "name"),
                Server = ReadString(envelope, "server") ?? ReadString(root, "server"),
                ServerId = ReadString(envelope, "serverId") ?? ReadString(root, "serverId"),
                DinoId = ReadString(envelope, "dinoId") ?? ReadString(root, "dinoId")
                         ?? ReadString(statsValue, "dinoId"),
                ActorId = ReadString(envelope, "actorId") ?? ReadString(root, "actorId")
                          ?? ReadString(statsValue, "actorId"),
                PawnId = ReadString(envelope, "pawnId") ?? ReadString(root, "pawnId")
                         ?? ReadString(statsValue, "pawnId"),
                Species = ReadString(envelope, "species") ?? ReadString(root, "species")
                          ?? ReadString(statsValue, "species"),
                DinoName = ReadString(envelope, "dinoName") ?? ReadString(root, "dinoName")
                           ?? ReadString(statsValue, "dinoName"),
                Growth = ReadNumber(envelope, "growth") ?? ReadNumber(root, "growth") ?? ReadNumber(statsValue, "growth"),
                Health = ReadNumber(envelope, "health") ?? ReadNumber(root, "health") ?? ReadNumber(statsValue, "health"),
                MaxHealth = ReadNumber(envelope, "maxHealth") ?? ReadNumber(root, "maxHealth") ?? ReadNumber(statsValue, "maxHealth"),
                Hunger = ReadNumber(envelope, "hunger") ?? ReadNumber(root, "hunger") ?? ReadNumber(statsValue, "hunger"),
                MaxHunger = ReadNumber(envelope, "maxHunger") ?? ReadNumber(root, "maxHunger") ?? ReadNumber(statsValue, "maxHunger"),
                Thirst = ReadNumber(envelope, "thirst") ?? ReadNumber(root, "thirst") ?? ReadNumber(statsValue, "thirst"),
                MaxThirst = ReadNumber(envelope, "maxThirst") ?? ReadNumber(root, "maxThirst") ?? ReadNumber(statsValue, "maxThirst"),
                Water = ReadNumber(envelope, "water") ?? ReadNumber(root, "water") ?? ReadNumber(statsValue, "water"),
                MaxWater = ReadNumber(envelope, "maxWater") ?? ReadNumber(root, "maxWater") ?? ReadNumber(statsValue, "maxWater"),
                Stamina = ReadNumber(envelope, "stamina") ?? ReadNumber(root, "stamina") ?? ReadNumber(statsValue, "stamina"),
                MaxStamina = ReadNumber(envelope, "maxStamina") ?? ReadNumber(root, "maxStamina") ?? ReadNumber(statsValue, "maxStamina"),
                FoodValue = ReadNumber(envelope, "foodValue") ?? ReadNumber(root, "foodValue") ?? ReadNumber(statsValue, "foodValue"),
                MaxFoodValue = ReadNumber(envelope, "maxFoodValue") ?? ReadNumber(root, "maxFoodValue") ?? ReadNumber(statsValue, "maxFoodValue"),
                Carb = ReadNumber(envelope, "carb") ?? ReadNumber(root, "carb") ?? ReadNumber(statsValue, "carb"),
                Protein = ReadNumber(envelope, "protein") ?? ReadNumber(root, "protein") ?? ReadNumber(statsValue, "protein"),
                Lipid = ReadNumber(envelope, "lipid") ?? ReadNumber(root, "lipid") ?? ReadNumber(statsValue, "lipid"),
                Nutrition = FindObject(envelope, "nutrition") is { } nutrition
                    ? ParseNutrition(nutrition)
                    : FindObject(root, "nutrition") is { } nestedNutrition
                        ? ParseNutrition(nestedNutrition)
                        : ParseFlatNutrition(envelope, root, statsValue),
                Prime = FindObject(envelope, "prime") is { } prime
                    ? ParsePrime(prime)
                    : FindObject(root, "prime") is { } nestedPrime
                        ? ParsePrime(nestedPrime)
                        : null
            };
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static JsonElement SelectPayload(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Object
            && (TryGetProperty(root, "data", out var data)
                || TryGetProperty(root, "d", out data))
            && data.ValueKind == JsonValueKind.Object)
        {
            return data;
        }

        return root;
    }

    private static GachaDinoStatsDto ParseStats(JsonElement value) => new()
    {
        Found = ReadBoolean(value, "found"),
        DinoName = ReadString(value, "dinoName") ?? ReadString(value, "name"),
        Species = ReadString(value, "species"),
        DinoId = ReadString(value, "dinoId") ?? ReadString(value, "dino_id"),
        ActorId = ReadString(value, "actorId") ?? ReadString(value, "actor_id"),
        PawnId = ReadString(value, "pawnId") ?? ReadString(value, "pawn_id"),
        Growth = ReadNumber(value, "growth"),
        Health = ReadNumber(value, "health"),
        MaxHealth = ReadNumber(value, "maxHealth"),
        Hunger = ReadNumber(value, "hunger"),
        MaxHunger = ReadNumber(value, "maxHunger"),
        Thirst = ReadNumber(value, "thirst"),
        MaxThirst = ReadNumber(value, "maxThirst"),
        Water = ReadNumber(value, "water"),
        MaxWater = ReadNumber(value, "maxWater"),
        Stamina = ReadNumber(value, "stamina"),
        MaxStamina = ReadNumber(value, "maxStamina"),
        FoodValue = ReadNumber(value, "foodValue"),
        MaxFoodValue = ReadNumber(value, "maxFoodValue"),
        Carb = ReadNumber(value, "carb"),
        Protein = ReadNumber(value, "protein"),
        Lipid = ReadNumber(value, "lipid"),
        Nutrition = FindObject(value, "nutrition") is { } nutrition
            ? ParseNutrition(nutrition)
            : ParseFlatNutrition(value)
    };

    private static GachaHealthDto ParseHealth(JsonElement value) => new()
    {
        Health = ReadNumber(value, "health"),
        MaxHealth = ReadNumber(value, "maxHealth")
    };

    private static GachaOverlayPositionDto ParsePosition(JsonElement value) => new()
    {
        X = ReadNumber(value, "x"),
        Y = ReadNumber(value, "y"),
        Z = ReadNumber(value, "z"),
        Yaw = ReadNumber(value, "yaw")
    };

    private static GachaNutritionDto ParseNutrition(JsonElement value) => new()
    {
        Carb = ReadNumber(value, "carb"),
        Protein = ReadNumber(value, "protein"),
        Lipid = ReadNumber(value, "lipid")
    };

    private static GachaNutritionDto? ParseFlatNutrition(params JsonElement[] values)
    {
        var nutrition = new GachaNutritionDto
        {
            Carb = values.Select(value => ReadNumber(value, "carb")).FirstOrDefault(value => value is not null),
            Protein = values.Select(value => ReadNumber(value, "protein")).FirstOrDefault(value => value is not null),
            Lipid = values.Select(value => ReadNumber(value, "lipid")).FirstOrDefault(value => value is not null)
        };
        return nutrition.Carb is not null || nutrition.Protein is not null || nutrition.Lipid is not null
            ? nutrition
            : null;
    }

    private static GachaPrimeDto ParsePrime(JsonElement value) => new()
    {
        Elder = ReadBoolean(value, "elder"),
        Eligible = ReadBoolean(value, "eligible"),
        Done = ReadInt32(value, "done"),
        Required = ReadInt32(value, "required")
    };

    private static JsonElement? FindObject(JsonElement value, string name) =>
        TryGetProperty(value, name, out var result) && result.ValueKind == JsonValueKind.Object
            ? result
            : null;

    private static bool TryGetProperty(JsonElement value, string name, out JsonElement result)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in value.EnumerateObject())
            {
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    result = property.Value;
                    return true;
                }
            }
        }

        result = default;
        return false;
    }

    private static string? ReadString(JsonElement value, string name) =>
        TryGetProperty(value, name, out var result) && result.ValueKind == JsonValueKind.String
            ? result.GetString()
            : null;

    private static bool? ReadBoolean(JsonElement value, string name)
    {
        if (!TryGetProperty(value, name, out var result))
        {
            return null;
        }

        if (result.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            return result.GetBoolean();
        }

        if (result.ValueKind == JsonValueKind.String
            && bool.TryParse(result.GetString(), out var parsed))
        {
            return parsed;
        }

        return null;
    }

    private static double? ReadNumber(JsonElement value, string name)
    {
        if (!TryGetProperty(value, name, out var result))
        {
            return null;
        }

        double parsed;
        if (result.ValueKind == JsonValueKind.Number && result.TryGetDouble(out parsed)
            || result.ValueKind == JsonValueKind.String && double.TryParse(
                result.GetString(),
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out parsed))
        {
            return double.IsFinite(parsed) ? parsed : null;
        }

        return null;
    }

    private static long? ReadInt64(JsonElement value, string name)
    {
        var number = ReadNumber(value, name);
        return number is { } parsed
               && Math.Truncate(parsed) == parsed
               && parsed >= long.MinValue && parsed <= long.MaxValue
            ? (long)parsed
            : null;
    }

    private static int? ReadInt32(JsonElement value, string name)
    {
        var number = ReadNumber(value, name);
        return number is { } parsed
               && Math.Truncate(parsed) == parsed
               && parsed >= int.MinValue && parsed <= int.MaxValue
            ? (int)parsed
            : null;
    }
}
