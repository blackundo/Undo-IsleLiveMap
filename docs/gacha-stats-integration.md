# Gacha stats adapter

`TheIsleOverlay.Gacha` is an optional, first-party integration seam for
servers whose official Gacha overlay is the source of dinosaur stats.

## Security boundary

- The adapter accepts an access token from the host's own authenticated Steam
  callback (`GachaOverlayCredentials`).
- It never reads Gacha's user-data directory, cookies, registry, process
  memory, or another overlay's token.
- Credentials are held in memory only by the adapter and are redacted by
  `ToString()`; the host owns persistence/expiry and should use its normal
  encrypted credential store.
- The client is pinned to `https://player.isle.vn` and `wss://player.isle.vn`.

## Data flow

1. The host creates `GachaOverlayClient` with an injected credential.
2. `GachaStatsSession` obtains the initial `/api/overlay/me` snapshot and then
   consumes the official `/overlay` WebSocket.
3. `GachaStatsReducer` merges sparse `overlay.dino`, `overlay.health`,
   `overlay.position`, and `overlay.ready` frames.
4. `GachaStatsSnapshotMerger.Merge` can add only the player stats to an
   existing local GPS/map snapshot. It preserves map markers and local GPS.

The reducer rejects out-of-order sequence/timestamp frames, resets state when
the server or actor/pawn identity changes, and marks data stale after the
configured freshness window. A transient `found:false` `/me` response cannot
erase a fresh WebSocket dino.

## Host wiring (outline)

The current WPF launch flow intentionally does not auto-read the standalone
Gacha client. Once an official login callback is added, compose the session in
the host and merge each snapshot:

```csharp
var client = new GachaOverlayClient(credentials);
await using var gacha = new GachaStatsSession(client);
await foreach (var stats in gacha.WatchAsync(cancellationToken))
{
    var merged = GachaStatsSnapshotMerger.Merge(hostSnapshot, stats, DateTimeOffset.UtcNow);
    // publish merged to the normal render lane
}
```

The host should discard/recreate the session on logout, game-process change,
server change, or credential refresh. It should not enable the experimental
Iris/GAS canary merely because Gacha stats are unavailable; inbound values
remain a separate, confidence-gated fallback.
