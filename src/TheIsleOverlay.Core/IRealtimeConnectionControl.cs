namespace TheIsleOverlay.Core;

public interface IRealtimeConnectionControl
{
    Task PauseRealtimeAsync(CancellationToken cancellationToken = default);
    void ResumeRealtime();

    Task ResumeRealtimeAsync(CancellationToken cancellationToken = default)
    {
        ResumeRealtime();
        return Task.CompletedTask;
    }
}
