# Pro tracking investigation — 2026-09-12

## Status / next prerequisite

Investigation only; no production changes or release. Game remains running.
The launched installed host is 1.5.2, PID 192904. It opened direct GPS with
diagnostics enabled. Pro credential file is absent; ProAccessService probe
returns `auth=False pro=False agent=False status=signed_out`. No Pro Agent
process exists. Developer must sign in through Home / Activate Pro before a
valid live Agent-to-map comparison can start. Do not bypass entitlement or
reuse old cache to make the map appear populated.

## Evidence collected

- Raw inbound: `output/baseline-20260912-inbound.bin` (ISLEIN01).
- Capture: 06:06:42.171–06:09:42.411 UTC (13:06–13:09 local), 180.2 seconds.
- Game PID 53716; endpoint 15.235.219.98:7777 -> 172.16.0.2:54783.
- 3,900 UDP payloads, 462,323 payload bytes, one observed endpoint.
- Inspector: `output/baseline-20260912-inbound-report-final.json`.
- 3,798 parsed DataStream packets, 21,344 batches, 201 creation records,
  17 exports, 16 discovered identity records, 228 destroy records.
- Zero parsed packets flagged incomplete is NOT proof of zero packet loss.
- The 16 identities and 201 creations are NOT counts of nearby players.
- Map diagnostics: `output/baseline-20260912-map.jsonl`; first 491 sampled
  render-end rows all have ProPlayerTrackingActive=false and zero markers.
  UI queue-delay p95 at that checkpoint: 60.4471 ms. Sampling is 4 Hz;
  do not interpret its timestamps as exhaustive UI-frame measurements.

## Chronological replay, no seeded identities

Ran existing StatefulReplay against an empty identity document
(`output/baseline-20260912-empty-replay-cache.json`), with fixed local
XY=(56259.84,21716.63). Tool uses fixed Z=28000, not the exact live local Z;
therefore distances and visibility are diagnostic approximations. Single
endpoint only. No future identity seeding or stale session cache used.

- One player track: actor 585538, PlayerState 585402, Pawn 585404, Ptera.
- First visible in replay at 06:09:14.6915216 UTC, the creation timestamp;
  this is 152.5 s after capture start, NOT a demonstrated 152.5 s decoder lag.
- Creation location (34847.5,15251.875,27166.875), approximately 224 m
  horizontally from the local checkpoint.
- Latest replay location (38919.9,14911.0,27918.3) at 06:09:41.9792804 UTC,
  approximately 186 m horizontally. Final replay current count = 1.
- This demonstrates one packet-proven player during the recording, not
  exhaustive scene coverage or current real-time presence after recording.
- Ongoing-only candidate outputs are not accepted as players. Handle 578656
  references Allo, Dryo, Herrera and Trice assets: an asset name alone cannot
  establish actor species/control. Candidate 585538 also has a conflicting
  scanned location, while its verified track has a different stable location.
  Never replace verified coordinates with a generic candidate to inflate count.

Inspector performs a roster-discovery prepass; do not use its discovery
counts as an online-latency benchmark. Chronological replay is the stronger
check here, but it still omits the live intake/fusion/IPC chain.

## Next iterations / acceptance criteria

1. Authenticate Pro normally, verify active entitlement, Agent executable
   version and capture health; keep game connected.
2. Start aligned bounded 180–300 s raw/Agent/map recordings. Agent diagnostic
   variable: ISLELIVEMAP_PRO_LIVE_COMPARE_PATH; map variable:
   ISLE_MAP_DIAGNOSTICS_PATH. Compare by timestamp/endpoint/handle, not just count.
3. Measure capture intake loss by lane, sequence gaps/reorder, evidence
   acquisition timing, first eligibility-to-Agent output, Agent-to-render lag,
   rejected reason, and destroy removal. Separate verified/provisional/AI.
4. Compare chronological replay with live results using equal initial state,
   the same endpoint, local trajectory and per-stage freshness/range gates.
5. Patch only a reproducible divergence. First add a regression that fails on
   baseline; then run tests and replay the exact same recording on both builds.
6. Run live confirmation on fresh traffic plus a same-session map restart.
   Reconnect/server-switch tests require the developer's cooperation.
7. Retain 1 km gate, local-player exclusion, AI/player proof separation,
   aquatic allowlist Coel/Turtle, destroy/session generation invalidation and
   no Free-to-Pro privilege regression. Never use roster count as coordinates.
8. Do not publish a release without separate authorization. No claim of
   perfection or all-server coverage from a single recording.
