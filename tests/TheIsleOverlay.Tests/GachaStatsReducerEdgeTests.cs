using System.Text;
using TheIsleOverlay.Core;
using TheIsleOverlay.Gacha;

namespace TheIsleOverlay.Tests;

public sealed class GachaStatsReducerEdgeTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 12, 13, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ApplyFrame_PrimeOnlyDinoDeltaIsPublished()
    {
        var reducer = new GachaStatsReducer();

        var applied = reducer.ApplyFrame(new GachaOverlayFrameDto
        {
            Type = "overlay.dino",
            ServerId = "gacha-01",
            Prime = new GachaPrimeDto
            {
                Elder = false,
                Eligible = true,
                Done = 7,
                Required = 10
            }
        }, Now);

        Assert.True(applied);
        var snapshot = reducer.BuildSnapshot(Now);
        Assert.NotNull(snapshot.Prime);
        Assert.False(snapshot.Prime!.Elder);
        Assert.True(snapshot.Prime.Eligible);
        Assert.Equal(7, snapshot.Prime.Done);
        Assert.Equal(10, snapshot.Prime.Required);
    }

    [Fact]
    public void GrowthOutsideDocumentedFractionRangeIsIgnored()
    {
        var reducer = new GachaStatsReducer();

        Assert.True(reducer.ApplyFrame(new GachaOverlayFrameDto
        {
            Type = "overlay.dino",
            ServerId = "gacha-01",
            Stats = new GachaDinoStatsDto
            {
                Found = true,
                DinoName = "Cera",
                Growth = 50,
                Hunger = 5,
                MaxHunger = 10
            }
        }, Now));

        var snapshot = reducer.BuildSnapshot(Now);
        Assert.Null(snapshot.Vitals.Growth);
        Assert.Null(snapshot.ToPlayerTelemetry().GrowthPercent);
    }

    [Fact]
    public void TransientFoundFalseDoesNotFlickerAnExistingDino()
    {
        var reducer = new GachaStatsReducer();

        Assert.True(reducer.ApplyFrame(new GachaOverlayFrameDto
        {
            Type = "overlay.dino",
            ServerId = "gacha-01",
            Stats = new GachaDinoStatsDto
            {
                Found = true,
                DinoName = "Deinosuchus",
                ActorId = "actor-a",
                Health = 80,
                MaxHealth = 100
            }
        }, Now));

        // The official client keeps its last found stats when this sparse
        // miss arrives.  The reducer must not briefly turn the player off.
        Assert.False(reducer.ApplyFrame(new GachaOverlayFrameDto
        {
            Type = "overlay.dino",
            ServerId = "gacha-01",
            Stats = new GachaDinoStatsDto { Found = false }
        }, Now.AddMilliseconds(100)));

        var snapshot = reducer.BuildSnapshot(Now.AddMilliseconds(100));
        Assert.True(snapshot.Online);
        Assert.True(snapshot.HasDino);
        Assert.Equal("Deinosuchus", snapshot.Species);
        Assert.Equal(80, snapshot.Vitals.Health);
    }

    [Fact]
    public void FoundFalseWithChangedIdentityClearsUntilFreshDinoArrives()
    {
        var reducer = new GachaStatsReducer();
        reducer.ApplyFrame(new GachaOverlayFrameDto
        {
            Type = "overlay.dino",
            ServerId = "gacha-01",
            Stats = new GachaDinoStatsDto
            {
                Found = true,
                DinoName = "Cera",
                ActorId = "actor-a",
                Health = 50,
                MaxHealth = 100
            }
        }, Now);

        reducer.ApplyFrame(new GachaOverlayFrameDto
        {
            Type = "overlay.dino",
            ServerId = "gacha-01",
            Stats = new GachaDinoStatsDto
            {
                Found = false,
                ActorId = "actor-b"
            }
        }, Now.AddSeconds(1));

        var cleared = reducer.BuildSnapshot(Now.AddSeconds(1));
        Assert.False(cleared.HasDino);
        Assert.Null(cleared.Species);
        Assert.Null(cleared.Vitals.Health);

        reducer.ApplyFrame(new GachaOverlayFrameDto
        {
            Type = "overlay.dino",
            ServerId = "gacha-01",
            Stats = new GachaDinoStatsDto
            {
                Found = true,
                DinoName = "Cera",
                ActorId = "actor-b",
                Health = 25,
                MaxHealth = 100
            }
        }, Now.AddSeconds(2));

        Assert.Equal(25, reducer.BuildSnapshot(Now.AddSeconds(2)).Vitals.Health);
    }

    [Fact]
    public void ParserAcceptsWaterAndFlatNutritionAliasesButRejectsFractionalSequence()
    {
        const string json = """
            {"type":"overlay.dino","seq": "4.5", "stats":{
              "found":true,"dinoName":"Cera","water":12.5,"maxWater":100,
              "carb":1.25,"protein":2.5,"lipid":3.75}}
            """;

        Assert.True(GachaOverlayDtoParser.TryParseFrame(Encoding.UTF8.GetBytes(json), out var frame));
        Assert.Null(frame.Sequence);
        Assert.Equal(12.5, frame.Stats?.Water);
        Assert.Equal(100, frame.Stats?.MaxWater);
        Assert.Equal(1.25, frame.Stats?.Carb);
        Assert.Equal(2.5, frame.Stats?.Nutrition?.Protein);
    }

    [Fact]
    public void SparsePrimeFrameStillKeepsAPlayerRow()
    {
        var reducer = new GachaStatsReducer();
        reducer.ApplyFrame(new GachaOverlayFrameDto
        {
            Type = "overlay.dino",
            ServerId = "gacha-01",
            Prime = new GachaPrimeDto { Elder = false, Done = 1, Required = 4 }
        }, Now);

        var merged = GachaStatsSnapshotMerger.Merge(
            null,
            reducer.BuildSnapshot(Now),
            Now);

        Assert.NotNull(merged.Player);
        Assert.Equal(1, merged.Player?.Prime?.Done);
    }

    [Fact]
    public void StaleGachaPlayerIsNotReportedOnline()
    {
        var reducer = new GachaStatsReducer(
            liveDataLifetime: TimeSpan.FromSeconds(1),
            apiDataLifetime: TimeSpan.FromSeconds(1));
        reducer.ApplyFrame(new GachaOverlayFrameDto
        {
            Type = "overlay.dino",
            ServerId = "gacha-01",
            Stats = new GachaDinoStatsDto { Found = true, DinoName = "Cera", Health = 1 }
        }, Now);
        var fresh = GachaStatsSnapshotMerger.Merge(null, reducer.BuildSnapshot(Now), Now);

        var stale = GachaStatsSnapshotMerger.Merge(
            fresh,
            reducer.BuildSnapshot(Now.AddSeconds(2)),
            Now.AddSeconds(2));

        Assert.False(stale.PlayerOnline);
        Assert.True(stale.LiveDataStale);
        Assert.NotNull(stale.Player);
    }

    [Fact]
    public void ReadyFrameStartsNewSequenceEpochAfterReconnect()
    {
        var reducer = new GachaStatsReducer();
        reducer.ApplyFrame(new GachaOverlayFrameDto
        {
            Type = "overlay.ready",
            ServerId = "gacha-01",
            Sequence = 100
        }, Now);
        reducer.ApplyFrame(new GachaOverlayFrameDto
        {
            Type = "overlay.dino",
            ServerId = "gacha-01",
            Sequence = 101,
            Stats = new GachaDinoStatsDto { Found = true, DinoName = "Cera", Health = 90 }
        }, Now.AddMilliseconds(10));

        // A new socket sends ready and restarts its sequence at a low value.
        Assert.True(reducer.ApplyFrame(new GachaOverlayFrameDto
        {
            Type = "overlay.ready",
            ServerId = "gacha-01",
            Sequence = 1
        }, Now.AddSeconds(1)));
        Assert.False(reducer.BuildSnapshot(Now.AddSeconds(1)).HasDino);

        Assert.True(reducer.ApplyFrame(new GachaOverlayFrameDto
        {
            Type = "overlay.dino",
            ServerId = "gacha-01",
            Sequence = 2,
            Stats = new GachaDinoStatsDto { Found = true, DinoName = "Cera", Health = 40 }
        }, Now.AddSeconds(2)));
        Assert.Equal(40, reducer.BuildSnapshot(Now.AddSeconds(2)).Vitals.Health);
    }

    [Fact]
    public void ReadyFrameAcceptsServerSwitchWithResetSequence()
    {
        var reducer = new GachaStatsReducer();
        reducer.ApplyFrame(new GachaOverlayFrameDto
        {
            Type = "overlay.dino",
            ServerId = "old",
            Sequence = 50,
            Stats = new GachaDinoStatsDto { Found = true, DinoName = "Cera", Health = 10 }
        }, Now);

        Assert.True(reducer.ApplyFrame(new GachaOverlayFrameDto
        {
            Type = "overlay.ready",
            ServerId = "new",
            Sequence = 1
        }, Now.AddSeconds(1)));

        var snapshot = reducer.BuildSnapshot(Now.AddSeconds(1));
        Assert.Equal("new", snapshot.ServerId);
        Assert.False(snapshot.HasDino);
        Assert.Null(snapshot.Vitals.Health);
    }

    [Fact]
    public void LateFrameAfterAuthoritativeLeaveCannotResurrectDino()
    {
        var reducer = new GachaStatsReducer();
        reducer.ApplyFrame(new GachaOverlayFrameDto
        {
            Type = "overlay.ready",
            ServerId = "gacha-01",
            Sequence = 10
        }, Now);
        reducer.ApplyFrame(new GachaOverlayFrameDto
        {
            Type = "overlay.dino",
            ServerId = "gacha-01",
            Sequence = 11,
            Stats = new GachaDinoStatsDto { Found = true, DinoName = "Cera", Health = 20 }
        }, Now.AddMilliseconds(10));
        reducer.ApplyFrame(new GachaOverlayFrameDto
        {
            Type = "overlay.ready",
            Sequence = 12
        }, Now.AddSeconds(1));

        Assert.True(reducer.ApplyFrame(new GachaOverlayFrameDto
        {
            Type = "overlay.position",
            ServerId = "gacha-01",
            Sequence = 13,
            Position = new GachaOverlayPositionDto { X = 1, Y = 2, Z = 3 }
        }, Now.AddSeconds(1.1)));

        var left = reducer.BuildSnapshot(Now.AddSeconds(1.1));
        Assert.False(left.Online);
        Assert.False(left.HasDino);
        Assert.Null(left.Location);
    }

    [Fact]
    public void FirstReadyAfterApiBootstrapStartsSequenceWithoutDiscardingApiStats()
    {
        var reducer = new GachaStatsReducer();
        reducer.ApplyApi(new GachaOverlayMeDto
        {
            ServerId = "gacha-01",
            Online = true,
            HasDino = true,
            DinoName = "Stego",
            Health = 90,
            MaxHealth = 100
        }, Now);

        Assert.True(reducer.ApplyFrame(new GachaOverlayFrameDto
        {
            Type = "overlay.ready",
            ServerId = "gacha-01",
            Sequence = 1
        }, Now.AddMilliseconds(10)));

        var snapshot = reducer.BuildSnapshot(Now.AddMilliseconds(10));
        Assert.True(snapshot.HasDino);
        Assert.Equal(90, snapshot.Vitals.Health);
    }
}
