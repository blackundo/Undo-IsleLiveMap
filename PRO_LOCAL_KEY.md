# Optional Pro Agent integration

This public repository builds and tests the Free application independently. It contains the IPC client and key-entry UI, not the private Agent, tracking algorithms, or an accepted activation key.

The user supplies a key. The host sends it over a current-user named pipe to the separately installed Agent. Pro access is granted and the key is saved with Windows DPAPI only after the Agent accepts it. Failed activation does not upgrade the application or overwrite a previously accepted key. On restart the saved key is rechecked with the Agent.

Build and test:

    dotnet build TheIsleOverlay.sln -c Release
    dotnet test TheIsleOverlay.sln -c Release

For a Free release, publish normally. To include an authorized, prebuilt Agent, set ProAgentDirectory to the absolute directory containing the Agent executable and all published dependencies:

    dotnet publish src/TheIsleOverlay.App/TheIsleOverlay.App.csproj -c Release -r win-x64 --self-contained true -p:ProAgentDirectory=C:/path/to/published-agent -o artifacts/full-build

The Agent is copied under ProAgent beside IsleLiveMap.exe. A source checkout of the private repository is not required by this public project. Keep private Agent binaries out of public releases unless you intend to distribute them.

IPC 2 supports activationMode local-key-v1, activationKey and probeOnly. The reply must accept the request and confirm local-key-v1 without a Steam ID. The private repository owns the accepted-key policy and real-Agent integration tests.

Updates point to blackundo/Undo-IsleLiveMap. The upstream remote remains klong-dev/IsleLiveMap for optional future merges. No upstream changes are fetched or merged automatically.