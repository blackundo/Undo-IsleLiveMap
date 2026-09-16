# Optional Pro Agent integration

This public repository builds and tests the Free application independently. It contains the IPC client and key-entry UI, not the private Agent, tracking algorithms, or an accepted activation key.

The user supplies a one-time key. The host creates an ECDSA P-256 device identity, proves possession to `https://isle-system.modundo.com/api/v1/activations`, and receives a device-bound RS256 lease. Only that lease is saved with Windows DPAPI; the one-time key is never persisted. The lease authorizes the signed Pro manifest and raw single-file Agent download. The executable is installed under `%LocalAppData%/Undo-Isle/IsleLiveMap/Pro/versions/<version>` and the active descriptor is `%LocalAppData%/Undo-Isle/IsleLiveMap/Pro/current.json`.

Build and test:

    dotnet build TheIsleOverlay.sln -c Release
    dotnet test TheIsleOverlay.sln -c Release

Build the Free release normally. It does not contain the private Agent:

    ./scripts/Package-Release.ps1 -Version 2.1.0

The release service must return a signed manifest whose artifact is the raw `IsleLiveMap.Pro.Agent.exe`, and accept only an active lease for manifest/artifact requests. Keep private Agent binaries out of public releases.

IPC 2 uses `activationMode=device-lease-v1`; `activationKey` carries the signed lease, not the redemption code. The Agent validates issuer, audience, algorithm, claims and expiry locally, and polls lease status every 15 minutes with ±2 minutes jitter. The private repository owns the PHP/MariaDB backend, signing keys, Agent and real-Agent integration tests.

Updates point to blackundo/Undo-IsleLiveMap. The upstream remote remains klong-dev/IsleLiveMap for optional future merges. No upstream changes are fetched or merged automatically.
