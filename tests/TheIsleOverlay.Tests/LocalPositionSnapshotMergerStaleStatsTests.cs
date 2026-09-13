using TheIsleOverlay.Core;
using TheIsleOverlay.LocalTelemetry;

namespace TheIsleOverlay.Tests;

public sealed class LocalPositionSnapshotMergerStaleStatsTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 12, 13, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Merge_PreservesRemoteStatsStalenessWhenRefreshingLocalGps()
    {
        var remoteVitals = new ExactVitals { Health = 80, MaxHealth = 100 };
        var remote = new TelemetrySnapshot
        {
            Source = "GACHA",
            Success = true,
            ServerOnline = true,
            PlayerOnline = false,
            LiveDataStale = true,
            SessionState = TelemetrySessionState.Stale,
            Player = new PlayerTelemetry
            {
                Class = "Cera",
                ExactVitals = remoteVitals,
                ExactVitalsSource = "GachaOfficialWebSocket",
                HealthPercent = 80
            }
        };

        var local = new LocalMovementObservation(
            Now,
            new UnrealMovementCandidate(100, 200, 300, 45, 1f, 64, 380, 26),
            "origin:7777");

        var merged = LocalPositionSnapshotMerger.Merge(remote, local, Now);

        Assert.Equal(100, merged.Player?.Location?.X);
        Assert.Same(remoteVitals, merged.Player?.ExactVitals);
        Assert.Equal("GachaOfficialWebSocket", merged.Player?.ExactVitalsSource);
        Assert.True(merged.LiveDataStale);
        Assert.Equal(TelemetrySessionState.Stale, merged.SessionState);
    }

    [Fact]
    public void Merge_PreservesStaleStateForRemoteSnapshotWithoutPlayerStats()
    {
        var remote = new TelemetrySnapshot
        {
            Source = "GACHA",
            Success = true,
            ServerOnline = true,
            PlayerOnline = false,
            LiveDataStale = true,
            SessionState = TelemetrySessionState.Stale,
            StatusMessage = "Gacha · DATA STALE"
        };
        var local = new LocalMovementObservation(
            Now,
            new UnrealMovementCandidate(100, 200, 300, 45, 1f, 64, 380, 26),
            "origin:7777");

        var merged = LocalPositionSnapshotMerger.Merge(remote, local, Now);

        Assert.Equal(100, merged.Player?.Location?.X);
        Assert.True(merged.LiveDataStale);
        Assert.Equal(TelemetrySessionState.Stale, merged.SessionState);
    }

    [Fact]
    public void Merge_FreshLocalIrisVitalsDoNotClearRemoteStatsStaleness()
    {
        var remote = new TelemetrySnapshot
        {
            Source = "GACHA",
            Success = true,
            ServerOnline = true,
            PlayerOnline = false,
            LiveDataStale = true,
            SessionState = TelemetrySessionState.Stale,
            Player = new PlayerTelemetry
            {
                Class = "Cera",
                ExactVitals = new ExactVitals { Health = 80, MaxHealth = 100 },
                ExactVitalsSource = "GachaOfficialWebSocket",
                HealthPercent = 80,
                Nutrition = new NutritionTelemetry { Carb = 2 },
                Prime = new PrimeTelemetry { Done = 1, Required = 3 }
            }
        };
        var localVitals = new ExactVitals { Health = 75, MaxHealth = 100 };
        var local = new LocalMovementObservation(
            Now,
            new UnrealMovementCandidate(100, 200, 300, 45, 1f, 64, 380, 26),
            "origin:7777",
            new LocalDinosaurVitalsObservation(Now, localVitals, 42));

        var merged = LocalPositionSnapshotMerger.Merge(
            remote,
            local,
            Now,
            allowLocalVitals: true);

        Assert.Same(localVitals, merged.Player?.ExactVitals);
        Assert.Equal(LocalVitalsFeature.SourceName, merged.Player?.ExactVitalsSource);
        Assert.Equal(75, merged.Player?.HealthPercent);
        Assert.True(merged.LiveDataStale);
        Assert.Equal(TelemetrySessionState.Stale, merged.SessionState);
        Assert.Equal(2, merged.Player?.Nutrition?.Carb);
        Assert.Equal(1, merged.Player?.Prime?.Done);
    }
}
