using TheIsleOverlay.Gacha;

namespace TheIsleOverlay.Tests;

public sealed class GachaStatsReducerSafetyTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 12, 13, 0, 0, TimeSpan.Zero);

    [Fact]
    public void PositionAfterAuthoritativeLeaveDoesNotResurrectDino()
    {
        var reducer = new GachaStatsReducer();

        Assert.True(reducer.ApplyFrame(new GachaOverlayFrameDto
        {
            Type = "overlay.dino",
            Sequence = 10,
            ServerId = "origin",
            Stats = new GachaDinoStatsDto
            {
                Found = true,
                DinoName = "Cera",
                Health = 80,
                MaxHealth = 100
            }
        }, Now));

        Assert.True(reducer.ApplyFrame(new GachaOverlayFrameDto
        {
            Type = "overlay.ready",
            Sequence = 11,
            ServerId = null
        }, Now.AddSeconds(1)));

        // A delayed position packet from the old stream must not turn an
        // authoritative leave into a fresh player again.
        reducer.ApplyFrame(new GachaOverlayFrameDto
        {
            Type = "overlay.position",
            Position = new GachaOverlayPositionDto { X = 100, Y = 200 }
        }, Now.AddSeconds(2));

        var snapshot = reducer.BuildSnapshot(Now.AddSeconds(2));
        Assert.False(snapshot.Online);
        Assert.False(snapshot.HasDino);
        Assert.Null(snapshot.Location);
        Assert.Null(snapshot.Vitals.Health);
    }

    [Fact]
    public void SparseActorIdentityChangeClearsPreviousVitals()
    {
        var reducer = new GachaStatsReducer();

        Assert.True(reducer.ApplyFrame(new GachaOverlayFrameDto
        {
            Type = "overlay.dino",
            Sequence = 1,
            ServerId = "origin",
            ActorId = "actor-a",
            Stats = new GachaDinoStatsDto
            {
                Found = true,
                DinoName = "Cera",
                Health = 100,
                MaxHealth = 100
            }
        }, Now));

        Assert.True(reducer.ApplyFrame(new GachaOverlayFrameDto
        {
            Type = "overlay.dino",
            Sequence = 2,
            ServerId = "origin",
            ActorId = "actor-b",
            Stats = new GachaDinoStatsDto { Hunger = 4 }
        }, Now.AddSeconds(1)));

        var snapshot = reducer.BuildSnapshot(Now.AddSeconds(1));
        Assert.Null(snapshot.Vitals.Health);
        Assert.Null(snapshot.Vitals.MaxHealth);
        Assert.Equal(4, snapshot.Vitals.Hunger);
    }

    [Fact]
    public void ReadyStartsNewEpochWhenSequenceResetsOnSameServer()
    {
        var reducer = new GachaStatsReducer();

        Assert.True(reducer.ApplyFrame(new GachaOverlayFrameDto
        {
            Type = "overlay.ready",
            Sequence = 100,
            ServerId = "origin"
        }, Now));
        Assert.True(reducer.ApplyFrame(new GachaOverlayFrameDto
        {
            Type = "overlay.dino",
            Sequence = 101,
            ServerId = "origin",
            Stats = new GachaDinoStatsDto
            {
                Found = true,
                DinoName = "Stego",
                Health = 100,
                MaxHealth = 100
            }
        }, Now.AddSeconds(1)));

        // Reconnect starts a new sequence epoch at one.
        Assert.True(reducer.ApplyFrame(new GachaOverlayFrameDto
        {
            Type = "overlay.ready",
            Sequence = 1,
            ServerId = "origin"
        }, Now.AddSeconds(2)));

        var snapshot = reducer.BuildSnapshot(Now.AddSeconds(2));
        Assert.False(snapshot.HasDino);
        Assert.Null(snapshot.Vitals.Health);
    }
}
