# Optional Pro Agent integration

This public repository builds and tests the Free application independently. It contains the IPC client and key-entry UI, not the private Agent, tracking algorithms, or an accepted activation key.

The user supplies a key. The host sends it as the bearer credential when requesting the signed Pro manifest and single-file Agent, installs the executable under `%LocalAppData%/Undo-Isle/IsleLiveMap/Pro/versions/<version>`, then verifies the same key with the Agent over a current-user named pipe. The active descriptor is stored in `%LocalAppData%/Undo-Isle/IsleLiveMap/Pro/current.json`. Pro access is granted and the key is saved with Windows DPAPI only after the Agent accepts it. A rejected key does not download the artifact when the release service enforces the bearer key, and failed activation does not overwrite a previously accepted key.

Build and test:

    dotnet build TheIsleOverlay.sln -c Release
    dotnet test TheIsleOverlay.sln -c Release

Build the Free release normally. It does not contain the private Agent:

    ./scripts/Package-Release.ps1 -Version 2.1.0

The release service must return a signed manifest whose artifact is the raw `IsleLiveMap.Pro.Agent.exe`, and must accept the activation key as its bearer credential for both the manifest and artifact requests. Keep private Agent binaries out of public releases.

IPC 2 supports activationMode local-key-v1, activationKey and probeOnly. The reply must accept the request and confirm local-key-v1 without a Steam ID. The private repository owns the accepted-key policy and real-Agent integration tests.

Updates point to blackundo/Undo-IsleLiveMap. The upstream remote remains klong-dev/IsleLiveMap for optional future merges. No upstream changes are fetched or merged automatically.
