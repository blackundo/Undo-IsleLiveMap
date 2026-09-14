namespace TheIsleOverlay.Core;

public interface IRealtimeConnectionControl
{
    Task PauseRealtimeAsync(CancellationToken cancellationToken = default);
    void ResumeRealtime();
}
