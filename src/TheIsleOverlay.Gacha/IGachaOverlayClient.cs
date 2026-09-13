namespace TheIsleOverlay.Gacha;

/// <summary>
/// Integration seam for an official Gacha overlay feed.  Implementations
/// receive credentials from the host's own login flow; they must not inspect
/// another process' storage or infer authentication from cookies on disk.
/// </summary>
public interface IGachaOverlayClient : IAsyncDisposable
{
    Task<GachaOverlayMeDto> GetMeAsync(CancellationToken cancellationToken = default);

    IAsyncEnumerable<GachaOverlayFrameDto> ReadLiveAsync(
        CancellationToken cancellationToken = default);
}
