using System.Globalization;
using System.Text.Json;
using TheIsleOverlay.Core;

namespace TheIsleOverlay.Origin;

public sealed class OriginStatsSession : ITelemetrySession
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(10);
    private readonly OriginStatsClient _client;
    private OriginServer? _activeServer;
    private int _watchStarted;
    private int _disposed;

    public OriginStatsSession(OriginStatsClient client, OriginServer? activeServer = null)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _activeServer = activeServer;
    }

    public async IAsyncEnumerable<TelemetrySnapshot> WatchAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation]
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (Interlocked.Exchange(ref _watchStarted, 1) != 0)
        {
            throw new InvalidOperationException("An Origin stats session can only be watched once.");
        }

        yield return new TelemetrySnapshot
        {
            Source = "ORIGIN x5",
            Success = false,
            SessionState = TelemetrySessionState.Connecting,
            StatusMessage = "Đang tìm dino trên Main Origin và Voice Chat Server…"
        };

        while (!cancellationToken.IsCancellationRequested)
        {
            TelemetrySnapshot snapshot;
            try
            {
                snapshot = await ProbeServersAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                yield break;
            }
            catch (OriginAuthenticationException)
            {
                snapshot = new TelemetrySnapshot
                {
                    Source = "ORIGIN x5",
                    Success = false,
                    SessionState = TelemetrySessionState.AuthenticationRequired,
                    StatusMessage = "Phiên Origin hết hạn; hãy đăng nhập lại."
                };
            }
            catch (Exception exception)
            {
                snapshot = new TelemetrySnapshot
                {
                    Source = "ORIGIN x5",
                    Success = false,
                    SessionState = TelemetrySessionState.Stale,
                    StatusMessage = $"Origin stats tạm thời không khả dụng: {exception.Message}"
                };
            }

            yield return snapshot;
            await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<TelemetrySnapshot> ProbeServersAsync(CancellationToken cancellationToken)
    {
        if (_activeServer is { } current)
        {
            var currentResult = await ProbeHealthAsync(current, cancellationToken).ConfigureAwait(false);
            if (currentResult.Result is not null)
            {
                return await BuildLiveSnapshotAsync(current, currentResult.Result.Value, cancellationToken)
                    .ConfigureAwait(false);
            }

            _activeServer = null;
        }

        // The dashboard supports exactly two active server ids. Probe them in
        // parallel so a user on Voice does not wait for a full Main timeout
        // first (or vice versa).
        var probes = await Task.WhenAll(
                OrderedServers().Select(server => ProbeHealthAsync(server, cancellationToken)))
            .ConfigureAwait(false);
        foreach (var probe in probes)
        {
            if (probe.Result is not { } result)
            {
                continue;
            }

            _activeServer = probe.Server;
            return await BuildLiveSnapshotAsync(probe.Server, result, cancellationToken)
                .ConfigureAwait(false);
        }

        return new TelemetrySnapshot
        {
            Source = "ORIGIN x5",
            Success = true,
            ServerOnline = true,
            PlayerOnline = false,
            UpdatedAt = DateTimeOffset.UtcNow,
            SessionState = TelemetrySessionState.Polling,
            StatusMessage = "Không tìm thấy dino đang chơi trên Main Origin hoặc Voice Chat Server."
        };
    }

    private IReadOnlyList<OriginServer> OrderedServers()
    {
        if (_client.PreferredServer is not { } preferred)
        {
            return OriginServer.All;
        }

        return OriginServer.All
            .OrderByDescending(server => Equals(server, preferred))
            .ToArray();
    }

    private async Task<(OriginServer Server, JsonElement? Result)> ProbeHealthAsync(
        OriginServer server,
        CancellationToken cancellationToken)
    {
        try
        {
            var health = await _client.ExecuteHealthAsync(server, cancellationToken)
                .ConfigureAwait(false);
            if (!health.IsCompletedSuccessfully || health.Result is not { } result)
            {
                return (server, null);
            }

            var player = ParsePlayer(result, server);
            if (player is null)
            {
                return (server, null);
            }

            return (server, result);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OriginAuthenticationException)
        {
            throw;
        }
        catch
        {
            return (server, null);
        }
    }

    private async Task<TelemetrySnapshot> BuildLiveSnapshotAsync(
        OriginServer server,
        JsonElement result,
        CancellationToken cancellationToken)
    {
        var player = ParsePlayer(result, server)!;
        PrimeTelemetry? prime = null;
        try
        {
            var primeResult = await _client.ExecutePrimeAsync(server, cancellationToken)
                .ConfigureAwait(false);
            if (primeResult.IsCompletedSuccessfully && primeResult.Result is { } primeJson)
            {
                prime = ParsePrime(primeJson);
            }
        }
        catch (OriginProtocolException)
        {
            // Stats remain useful when a server does not expose prime data.
        }

        player = player with { Prime = prime };

        return new TelemetrySnapshot
        {
            Source = "ORIGIN x5",
            Success = true,
            ServerOnline = true,
            PlayerOnline = true,
            UpdatedAt = DateTimeOffset.UtcNow,
            Player = player,
            SessionState = TelemetrySessionState.Live,
            StatusMessage = $"Origin · {server.DisplayName}"
        };
    }

    private static PlayerTelemetry? ParsePlayer(JsonElement result, OriginServer server)
    {
        var species = ReadString(result, "species") ?? ReadString(result, "dino");
        if (string.IsNullOrWhiteSpace(species))
        {
            return null;
        }

        var growth = ReadDouble(result, "growth");
        var vitals = new ExactVitals
        {
            Growth = growth,
            Health = ReadDouble(result, "hp"),
            MaxHealth = ReadDouble(result, "maxHp"),
            Hunger = ReadDouble(result, "hunger"),
            MaxHunger = ReadDouble(result, "maxHunger"),
            Thirst = ReadDouble(result, "thirst"),
            MaxThirst = ReadDouble(result, "maxThirst"),
            Stamina = ReadDouble(result, "stamina"),
            MaxStamina = ReadDouble(result, "maxStamina")
        };

        return new PlayerTelemetry
        {
            Class = species,
            Server = server.DisplayName,
            GrowthPercent = growth is { } g ? Math.Clamp(g <= 1d ? g * 100d : g, 0d, 100d) : null,
            HealthPercent = Percent(vitals.Health, vitals.MaxHealth),
            HungerPercent = Percent(vitals.Hunger, vitals.MaxHunger),
            ThirstPercent = Percent(vitals.Thirst, vitals.MaxThirst),
            StaminaPercent = Percent(vitals.Stamina, vitals.MaxStamina),
            ExactVitals = vitals,
            ExactVitalsSource = "OriginDashboard",
            Prime = null
        };
    }

    private static PrimeTelemetry ParsePrime(JsonElement result)
    {
        var growth = ReadDouble(result, "growth");
        var completed = ReadInt(result, "completed");
        var total = ReadInt(result, "total");
        var conditions = new List<PrimeQuestTelemetry>();
        if (result.TryGetProperty("conditions", out var values)
            && values.ValueKind == JsonValueKind.Array)
        {
            var names = new[] { "Sanctuary", "Nested", "Diet", "Mass Migration", "2 Migration", "4 Patrol", "Infertile", "Spasms", "Children", "Small Species" };
            var index = 0;
            foreach (var item in values.EnumerateArray())
            {
                var done = item.ValueKind is JsonValueKind.True or JsonValueKind.False
                    ? item.GetBoolean()
                    : (bool?)null;
                conditions.Add(new PrimeQuestTelemetry
                {
                    Name = index < names.Length ? names[index] : $"Quest {index + 1}",
                    Done = done
                });
                index++;
            }
        }

        return new PrimeTelemetry
        {
            Progress = growth,
            Done = completed,
            Required = total,
            Eligible = ReadBool(result, "eligible"),
            Elder = ReadBool(result, "hasElder"),
            Quests = conditions
        };
    }

    private static double? Percent(double? current, double? maximum) =>
        current is { } value && maximum is > 0d and var max
            ? Math.Clamp(value / max * 100d, 0d, 100d)
            : null;

    private static string? ReadString(JsonElement value, string name) =>
        value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static bool? ReadBool(JsonElement value, string name) =>
        value.TryGetProperty(name, out var property)
            ? property.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? property.GetBoolean()
                : bool.TryParse(property.ToString(), out var parsed) ? parsed : null
            : null;

    private static int? ReadInt(JsonElement value, string name) =>
        int.TryParse(ReadString(value, name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var textValue)
            ? textValue
            : value.TryGetProperty(name, out var property) && property.TryGetInt32(out var numberValue)
                ? numberValue
                : null;

    private static double? ReadDouble(JsonElement value, string name)
    {
        if (!value.TryGetProperty(name, out var property))
        {
            return null;
        }

        if (property.ValueKind == JsonValueKind.Number && property.TryGetDouble(out var number))
        {
            return double.IsFinite(number) ? number : null;
        }

        return double.TryParse(property.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var text)
            && double.IsFinite(text)
                ? text
                : null;
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _client.Dispose();
        }

        return ValueTask.CompletedTask;
    }
}
