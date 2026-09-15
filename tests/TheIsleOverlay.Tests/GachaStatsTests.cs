using System.Text;
using TheIsleOverlay.Core;
using TheIsleOverlay.Gacha;

namespace TheIsleOverlay.Tests;

public sealed class GachaStatsTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 12, 13, 0, 0, TimeSpan.Zero);

    [Fact]
    public void FrameParser_ReadsOfficialDinoEnvelopeAndSparseValues()
    {
        const string json = """
            {
              "type":"overlay.dino",
              "seq":42,
              "serverId":"gacha-01",
              "steamId":"76561198000000000",
              "stats":{
                "found":true,
                "dinoName":"Pteranodon",
                "growth":0.73,
                "health":11.6,
                "maxHealth":11.6,
                "hunger":2.2,
                "maxHunger":3.83,
                "thirst":243.32,
                "maxThirst":1000,
                "stamina":328.31,
                "maxStamina":328.31,
                "nutrition":{"carb":1,"protein":2,"lipid":3}
              }
            }
            """;

        Assert.True(GachaOverlayDtoParser.TryParseFrame(Encoding.UTF8.GetBytes(json), out var frame));
        Assert.Equal("overlay.dino", frame.Type);
        Assert.Equal(42, frame.Sequence);
        Assert.Equal("gacha-01", frame.ServerId);
        Assert.True(frame.Stats?.Found);
        Assert.Equal("Pteranodon", frame.Stats?.DinoName);
        Assert.Equal(328.31, frame.Stats?.MaxStamina);
        Assert.Equal(2, frame.Stats?.Nutrition?.Protein);
    }

    [Fact]
    public void MeParser_AcceptsDataWrapperAndStringNumbers()
    {
        const string json = """
            {"data":{"online":true,"hasDino":true,"server":"Gacha","steamId":"76561198000000000",
              "stats":{"dinoName":"Deinosuchus","health":"99.5","maxHealth":"100","hunger":"8.5","maxHunger":"10"}}}
            """;

        Assert.True(GachaOverlayDtoParser.TryParseMe(Encoding.UTF8.GetBytes(json), out var me));
        Assert.True(me.Online);
        Assert.True(me.HasDino);
        Assert.Equal("Gacha", me.Server);
        Assert.Equal("Deinosuchus", me.DinoName);
        Assert.Equal(99.5, me.Health);
        Assert.Equal(10, me.MaxHunger);
    }

    [Fact]
    public void FrameParser_AcceptsCompactDEnvelopeAndMetadataOutsideData()
    {
        const string json = """
            {"t":"overlay.position","seq":"7","serverId":"gacha-02",
             "d":{"position":{"x":1,"y":2,"z":3,"yaw":4}}}
            """;

        Assert.True(GachaOverlayDtoParser.TryParseFrame(Encoding.UTF8.GetBytes(json), out var frame));
        Assert.Equal("overlay.position", frame.Type);
        Assert.Equal(7, frame.Sequence);
        Assert.Equal("gacha-02", frame.ServerId);
        Assert.Equal(2, frame.Position?.Y);
    }

    [Fact]
    public void Reducer_MergesSparseLiveFramesWithoutLosingVerifiedFields()
    {
        var reducer = new GachaStatsReducer();
        reducer.ApplyApi(new GachaOverlayMeDto
        {
            Online = true,
            HasDino = true,
            SteamId = "76561198000000000",
            Server = "Gacha",
            DinoName = "Pteranodon",
            Health = 11.6,
            MaxHealth = 11.6,
            Hunger = 2.2,
            MaxHunger = 3.83,
            Thirst = 243.32,
            MaxThirst = 1000
        }, Now);

        Assert.True(reducer.ApplyFrame(new GachaOverlayFrameDto
        {
            Type = "overlay.dino",
            Sequence = 2,
            Stats = new GachaDinoStatsDto { Hunger = 2.1 }
        }, Now.AddSeconds(1)));

        var snapshot = reducer.BuildSnapshot(Now.AddSeconds(1));
        Assert.Equal(2.1, snapshot.Vitals.Hunger);
        Assert.Equal(3.83, snapshot.Vitals.MaxHunger);
        Assert.Equal(243.32, snapshot.Vitals.Thirst);
        Assert.Equal(GachaStatsSource.OfficialWebSocket, snapshot.Source);
        Assert.Equal(GachaStatsConfidence.Verified, snapshot.Confidence);
    }

    [Fact]
    public void Reducer_IgnoresOlderSequenceAndTransientOfflineApi()
    {
        var reducer = new GachaStatsReducer();
        reducer.ApplyFrame(new GachaOverlayFrameDto
        {
            Type = "overlay.dino",
            Sequence = 10,
            ServerId = "gacha-01",
            Stats = new GachaDinoStatsDto
            {
                Found = true,
                DinoName = "Deinosuchus",
                Hunger = 8,
                MaxHunger = 10
            }
        }, Now);

        Assert.False(reducer.ApplyFrame(new GachaOverlayFrameDto
        {
            Type = "overlay.dino",
            Sequence = 9,
            Stats = new GachaDinoStatsDto { Hunger = 1 }
        }, Now.AddMilliseconds(1)));

        reducer.ApplyApi(new GachaOverlayMeDto
        {
            Online = false,
            HasDino = false,
            ServerId = "gacha-01"
        }, Now.AddSeconds(1));

        var snapshot = reducer.BuildSnapshot(Now.AddSeconds(1));
        Assert.True(snapshot.Online);
        Assert.True(snapshot.HasDino);
        Assert.Equal(8, snapshot.Vitals.Hunger);
    }

    [Fact]
    public void Reducer_ServerChangeAndReadyNullResetDinoState()
    {
        var reducer = new GachaStatsReducer();
        reducer.ApplyFrame(new GachaOverlayFrameDto
        {
            Type = "overlay.dino",
            Sequence = 1,
            ServerId = "one",
            Stats = new GachaDinoStatsDto { Found = true, DinoName = "Cera", Health = 5 }
        }, Now);

        reducer.ApplyFrame(new GachaOverlayFrameDto
        {
            Type = "overlay.ready",
            Sequence = 2,
            ServerId = "two"
        }, Now.AddSeconds(1));
        var changed = reducer.BuildSnapshot(Now.AddSeconds(1));
        Assert.Equal("two", changed.ServerId);
        Assert.False(changed.HasDino);
        Assert.Null(changed.Vitals.Health);

        reducer.ApplyFrame(new GachaOverlayFrameDto
        {
            Type = "overlay.dino",
            Sequence = 3,
            ServerId = "two",
            Stats = new GachaDinoStatsDto { Found = true, DinoName = "T-Rex", Health = 20 }
        }, Now.AddSeconds(2));
        reducer.ApplyFrame(new GachaOverlayFrameDto
        {
            Type = "overlay.ready",
            Sequence = 4,
            ServerId = null
        }, Now.AddSeconds(3));

        var left = reducer.BuildSnapshot(Now.AddSeconds(3));
        Assert.False(left.Online);
        Assert.False(left.HasDino);
        Assert.Null(left.Species);
        Assert.Null(left.Vitals.Health);
    }

    [Fact]
    public void Reducer_ActorIdentityChangeResetsVitalsEvenWhenSpeciesIsTheSame()
    {
        var reducer = new GachaStatsReducer();
        reducer.ApplyFrame(new GachaOverlayFrameDto
        {
            Type = "overlay.dino",
            Sequence = 1,
            ServerId = "gacha",
            Stats = new GachaDinoStatsDto
            {
                Found = true,
                DinoName = "Deinosuchus",
                ActorId = "actor-a",
                Health = 100,
                MaxHealth = 100
            }
        }, Now);

        reducer.ApplyFrame(new GachaOverlayFrameDto
        {
            Type = "overlay.dino",
            Sequence = 2,
            ServerId = "gacha",
            Stats = new GachaDinoStatsDto
            {
                Found = true,
                DinoName = "Deinosuchus",
                ActorId = "actor-b",
                Health = 40,
                MaxHealth = 40
            }
        }, Now.AddSeconds(1));

        var snapshot = reducer.BuildSnapshot(Now.AddSeconds(1));
        Assert.Equal(40, snapshot.Vitals.Health);
        Assert.Equal(40, snapshot.Vitals.MaxHealth);
    }

    [Fact]
    public void Reducer_MarksStateStaleWithoutInventingFreshValues()
    {
        var reducer = new GachaStatsReducer(
            liveDataLifetime: TimeSpan.FromSeconds(5),
            apiDataLifetime: TimeSpan.FromSeconds(10));
        reducer.ApplyApi(new GachaOverlayMeDto
        {
            Online = true,
            HasDino = true,
            DinoName = "Stego",
            Hunger = 7,
            MaxHunger = 10
        }, Now);

        var fresh = reducer.BuildSnapshot(Now.AddSeconds(4));
        Assert.False(fresh.IsStale);
        Assert.True(fresh.HasDino);

        var stale = reducer.BuildSnapshot(Now.AddSeconds(11));
        Assert.True(stale.IsStale);
        Assert.False(stale.HasDino);
        Assert.True(stale.Online);
        Assert.Equal(7, stale.Vitals.Hunger);
    }

    [Fact]
    public void Reducer_ToleratesNormalSparseGachaHeartbeatGap()
    {
        var reducer = new GachaStatsReducer();
        reducer.ApplyFrame(new GachaOverlayFrameDto
        {
            Type = "overlay.ready",
            ServerId = "gacha-01",
            Sequence = 1
        }, Now);
        reducer.ApplyFrame(new GachaOverlayFrameDto
        {
            Type = "overlay.dino",
            ServerId = "gacha-01",
            Sequence = 2,
            Stats = new GachaDinoStatsDto
            {
                Found = true,
                DinoName = "Triceratops",
                Health = 100,
                MaxHealth = 100
            }
        }, Now.AddMilliseconds(10));

        var withinObservedGap = reducer.BuildSnapshot(Now.AddSeconds(10));
        Assert.False(withinObservedGap.IsStale);
        Assert.True(withinObservedGap.HasDino);

        var afterSilenceWindow = reducer.BuildSnapshot(Now.AddSeconds(16));
        Assert.True(afterSilenceWindow.IsStale);
        Assert.False(afterSilenceWindow.HasDino);
    }

    [Fact]
    public void Reducer_UsesRecognizedNullPositionAsTransportHeartbeat()
    {
        var reducer = new GachaStatsReducer(
            liveDataLifetime: TimeSpan.FromSeconds(5),
            apiDataLifetime: TimeSpan.FromSeconds(1));
        reducer.ApplyFrame(new GachaOverlayFrameDto
        {
            Type = "overlay.ready",
            ServerId = "gacha-01",
            Sequence = 1
        }, Now);
        reducer.ApplyFrame(new GachaOverlayFrameDto
        {
            Type = "overlay.dino",
            ServerId = "gacha-01",
            Sequence = 2,
            Stats = new GachaDinoStatsDto
            {
                Found = true,
                DinoName = "Triceratops",
                Health = 100,
                MaxHealth = 100
            }
        }, Now.AddMilliseconds(10));

        // Gacha emits these sparse heartbeats while no position delta is
        // available. They must keep the authenticated stream live without
        // inventing or clearing any stats.
        for (var i = 1; i <= 10; i++)
        {
            reducer.ApplyFrame(new GachaOverlayFrameDto
            {
                Type = "overlay.position",
                ServerId = "gacha-01",
                Sequence = 2 + i,
                Position = new GachaOverlayPositionDto()
            }, Now.AddSeconds(i));
        }

        var heartbeatFresh = reducer.BuildSnapshot(Now.AddSeconds(10));
        Assert.False(heartbeatFresh.IsStale);
        Assert.True(heartbeatFresh.HasDino);
        Assert.Equal(100, heartbeatFresh.Vitals.Health);

        var afterTransportSilence = reducer.BuildSnapshot(Now.AddSeconds(16));
        Assert.True(afterTransportSilence.IsStale);
        Assert.False(afterTransportSilence.HasDino);
    }

    [Fact]
    public void SnapshotMapperCarriesOfficialSourceAndConvertsGrowthAndHeading()
    {
        var reducer = new GachaStatsReducer();
        reducer.ApplyFrame(new GachaOverlayFrameDto
        {
            Type = "overlay.dino",
            Sequence = 1,
            ServerId = "gacha",
            SteamId = "76561198000000000",
            Stats = new GachaDinoStatsDto
            {
                Found = true,
                DinoName = "Triceratops",
                Growth = 0.5,
                Hunger = 5,
                MaxHunger = 10
            }
        }, Now);
        reducer.ApplyFrame(new GachaOverlayFrameDto
        {
            Type = "overlay.position",
            Sequence = 2,
            Position = new GachaOverlayPositionDto { X = 10, Y = 20, Z = 30, Yaw = 90 }
        }, Now.AddMilliseconds(50));

        var player = reducer.BuildSnapshot(Now.AddMilliseconds(50)).ToPlayerTelemetry();
        Assert.Equal("GachaOfficialWebSocket", player.ExactVitalsSource);
        Assert.Equal(50, player.GrowthPercent);
        Assert.Equal(20, player.Location?.Y);
        Assert.Equal(180, player.ExactMapHeadingDegrees);
        Assert.Equal("Triceratops", player.Class);
    }

    [Fact]
    public void CredentialsNeverExposeBearerTokenAndOptionsRejectUntrustedHost()
    {
        var credentials = new GachaOverlayCredentials(
            "secret-token",
            "76561198000000000");
        Assert.DoesNotContain("secret-token", credentials.ToString(), StringComparison.Ordinal);
        Assert.Equal("76561198000000000", credentials.SteamId);

        var options = new GachaOverlayOptions { BaseUri = new Uri("https://evil.example/") };
        Assert.Throws<ArgumentException>(() => options.Validate());
    }

    [Fact]
    public void SnapshotMergerContributesStatsWithoutReplacingHostMapOrGps()
    {
        var reducer = new GachaStatsReducer();
        reducer.ApplyFrame(new GachaOverlayFrameDto
        {
            Type = "overlay.dino",
            Sequence = 1,
            ServerId = "gacha",
            Stats = new GachaDinoStatsDto
            {
                Found = true,
                DinoName = "Cera",
                Hunger = 4,
                MaxHunger = 10
            }
        }, Now);
        var stats = reducer.BuildSnapshot(Now);
        var host = new TelemetrySnapshot
        {
            Source = "DIRECT",
            Success = true,
            ServerOnline = true,
            PlayerOnline = true,
            Player = new PlayerTelemetry
            {
                Location = new WorldLocation { X = 100, Y = 200 },
                MapLocation = new MapPoint(0.2, 0.3)
            },
            Map = new MapTelemetry
            {
                Markers = [new MapMarkerTelemetry { Label = "keep" }]
            }
        };

        var merged = GachaStatsSnapshotMerger.Merge(host, stats, Now);
        Assert.Equal("GACHA", merged.Source);
        Assert.Equal("Cera", merged.Player?.Class);
        Assert.Equal(4, merged.Player?.ExactVitals?.Hunger);
        Assert.Equal(100, merged.Player?.Location?.X);
        Assert.Equal(new MapPoint(0.2, 0.3), merged.Player?.MapLocation);
        Assert.Same(host.Map, merged.Map);
    }

    [Fact]
    public async Task OfficialClient_GetMeUsesBearerAndPinnedEndpoint()
    {
        var handler = new RecordingHandler("""
            {"online":true,"hasDino":true,"serverId":"gacha-01",
             "dinoName":"Cera","hunger":4,"maxHunger":10}
            """);
        using var http = new HttpClient(handler);
        var credentials = new GachaOverlayCredentials("test-token");
        await using var client = new GachaOverlayClient(credentials, httpClient: http);

        var me = await client.GetMeAsync();

        Assert.Equal("https://player.isle.vn/api/overlay/me", handler.Request?.RequestUri?.ToString());
        Assert.Equal("Bearer test-token", handler.Request?.Headers.Authorization?.ToString());
        Assert.True(me.HasDino);
        Assert.Equal("Cera", me.DinoName);
        Assert.Equal(4, me.Hunger);
    }

    [Fact]
    public void SnapshotMerger_ClearsPreviousGachaPlayerAfterAuthoritativeLeave()
    {
        var reducer = new GachaStatsReducer();
        reducer.ApplyFrame(new GachaOverlayFrameDto
        {
            Type = "overlay.dino",
            Sequence = 1,
            ServerId = "gacha",
            Stats = new GachaDinoStatsDto { Found = true, DinoName = "Cera", Hunger = 2 }
        }, Now);
        var live = GachaStatsSnapshotMerger.Merge(
            null,
            reducer.BuildSnapshot(Now),
            Now);
        Assert.NotNull(live.Player);

        reducer.ApplyFrame(new GachaOverlayFrameDto
        {
            Type = "overlay.ready",
            Sequence = 2,
            ServerId = null
        }, Now.AddSeconds(1));
        var left = GachaStatsSnapshotMerger.Merge(
            live,
            reducer.BuildSnapshot(Now.AddSeconds(1)),
            Now.AddSeconds(1));

        Assert.False(left.PlayerOnline);
        Assert.Null(left.Player);
    }

    private sealed class RecordingHandler(string responseJson) : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Request = request;
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new StringContent(responseJson, Encoding.UTF8, "application/json")
            });
        }
    }
}
