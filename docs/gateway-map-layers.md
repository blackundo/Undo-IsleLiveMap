# Gateway map layers

The map overlays are split by lifetime and trust boundary:

- Migration and Patrol polygons are a one-time IslePilot snapshot bundled in
  `Assets/GatewayMapLayers.json`. The application does not download or refresh
  these polygons at runtime.
- Food regions are registered from `D:\nguonthucan.png` onto the bundled
  Gateway texture with a SIFT + RANSAC homography, then stored in the same local
  asset. Source and map hashes, calibration metrics, and the transform are kept
  in the asset's `provenance` block.
- Player heatmap is intentionally not baked into the asset. It is time-varying
  and appears only when IslePilot explicitly returns heat cells for the active
  server. The renderer never derives heat from normal markers or Pro entities.

The local catalog validates schema version, map ID, counts, unique IDs and all
normalized geometry before rendering. Invalid data fails closed instead of
placing a layer at an uncertain position.

Layer order is heatmap, zones, food regions, notes/team entities, remote
players/AI, then the local player. This keeps live tactical markers readable.
