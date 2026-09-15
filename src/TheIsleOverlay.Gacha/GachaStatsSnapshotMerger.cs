using TheIsleOverlay.Core;

namespace TheIsleOverlay.Gacha;

/// <summary>
/// Integration seam for hosts that already have a local GPS/map snapshot.
/// Gacha contributes only the player identity/stats fields; map markers,
/// local position, and Pro entity data remain owned by the host snapshot.
/// Stale Gacha data is marked stale and never presented as a fresh update.
/// </summary>
public static class GachaStatsSnapshotMerger
{
    public static TelemetrySnapshot Merge(
        TelemetrySnapshot? hostSnapshot,
        GachaStatsSnapshot gacha,
        DateTimeOffset now,
        string sourceName = "GACHA")
    {
        ArgumentNullException.ThrowIfNull(gacha);

        var host = hostSnapshot ?? new TelemetrySnapshot
        {
            Source = sourceName,
            Success = true,
            ServerOnline = true
        };

        if (gacha.IsStale)
        {
            // Keep a previous Gacha player visible only as a clearly stale
            // snapshot. Never replace a fresh host source with old Gacha
            // values after a server switch or logout.
            var existing = host.Player;
            var sameSource = existing is not null
                              && string.Equals(
                                  existing.ExactVitalsSource,
                                  "GachaOfficialWebSocket",
                                  StringComparison.Ordinal)
                              || existing is not null
                              && string.Equals(
                                  existing.ExactVitalsSource,
                                  "GachaOfficialApi",
                                  StringComparison.Ordinal);
            return sameSource
                ? host with
                {
                    Source = sourceName,
                    PlayerOnline = false,
                    LiveDataStale = true,
                    SessionState = TelemetrySessionState.Stale,
                    UpdatedAt = gacha.ReceivedAt ?? host.UpdatedAt,
                    StatusMessage = gacha.StatusMessage ?? "Gacha · DATA STALE"
                }
                : host;
        }

        if (!gacha.HasDino && !gacha.HasAnyData)
        {
            var previousGachaPlayer = host.Player is { } previous
                && (string.Equals(
                        previous.ExactVitalsSource,
                        "GachaOfficialWebSocket",
                        StringComparison.Ordinal)
                    || string.Equals(
                        previous.ExactVitalsSource,
                        "GachaOfficialApi",
                        StringComparison.Ordinal));
            return host with
            {
                Source = sourceName,
                PlayerOnline = previousGachaPlayer ? false : host.PlayerOnline,
                Player = previousGachaPlayer ? null : host.Player,
                StatusMessage = gacha.StatusMessage ?? "Gacha · ĐANG CHỜ DINO",
                LiveDataStale = false,
                SessionState = TelemetrySessionState.Connecting
            };
        }

        var existingPlayer = host.Player;
        var player = gacha.ToPlayerTelemetry(existingPlayer?.Location);
        if (existingPlayer is not null)
        {
            player = player with
            {
                Name = player.Name ?? existingPlayer.Name,
                Server = player.Server ?? existingPlayer.Server,
                Location = player.Location ?? existingPlayer.Location,
                MapLocation = existingPlayer.MapLocation,
                ExactMapHeadingDegrees = player.ExactMapHeadingDegrees
                                           ?? existingPlayer.ExactMapHeadingDegrees,
                Prime = player.Prime ?? existingPlayer.Prime,
                Nutrition = player.Nutrition ?? existingPlayer.Nutrition
            };
        }

        return host with
        {
            Source = sourceName,
            Success = true,
            ServerOnline = true,
            PlayerOnline = gacha.Online && gacha.HasDino,
            UpdatedAt = gacha.ReceivedAt ?? gacha.ObservedAt ?? now,
            Player = player,
            SessionState = TelemetrySessionState.Live,
            LiveDataStale = false,
            StatusMessage = gacha.StatusMessage
        };
    }
}
