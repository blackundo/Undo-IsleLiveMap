using TheIsleOverlay.Core;
using TheIsleOverlay.ProClient;

namespace TheIsleOverlay.ProClient.Tests;

public sealed class ProAgentRealtimeControlTests
{
    [Fact]
    public async Task PauseAndResume_AreAcknowledgedAndIdempotent()
    {
        await using var source = CreateSource();
        var control = new FakeRealtimeConnectionControl();
        source.AttachRealtimeConnectionControl(control);

        var firstPause = await source.HandleRealtimeControlAsync(
            new AgentRealtimeControlRequest("pause-1", Pause: true),
            TestContext.Current.CancellationToken);
        var duplicatePause = await source.HandleRealtimeControlAsync(
            new AgentRealtimeControlRequest("pause-2", Pause: true),
            TestContext.Current.CancellationToken);
        var resume = await source.HandleRealtimeControlAsync(
            new AgentRealtimeControlRequest("resume-1", Pause: false),
            TestContext.Current.CancellationToken);

        Assert.True(firstPause.Success);
        Assert.True(duplicatePause.Success);
        Assert.True(resume.Success);
        Assert.Equal(1, control.PauseCount);
        Assert.Equal(1, control.ResumeCount);
    }

    [Fact]
    public async Task Dispose_ResumesRealtimeAfterAgentHeldPause()
    {
        var source = CreateSource();
        var control = new FakeRealtimeConnectionControl();
        source.AttachRealtimeConnectionControl(control);
        await source.HandleRealtimeControlAsync(
            new AgentRealtimeControlRequest("pause", Pause: true),
            TestContext.Current.CancellationToken);

        await source.DisposeAsync();

        Assert.Equal(1, control.ResumeCount);
    }

    [Fact]
    public async Task PauseWithoutAttachedTelemetry_ReturnsFailure()
    {
        await using var source = CreateSource();

        var result = await source.HandleRealtimeControlAsync(
            new AgentRealtimeControlRequest("pause", Pause: true),
            TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Contains("dino stats", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PauseFailure_RollsBackRealtimeBeforeReturningFailure()
    {
        await using var source = CreateSource();
        var control = new FakeRealtimeConnectionControl { FailPause = true };
        source.AttachRealtimeConnectionControl(control);

        var result = await source.HandleRealtimeControlAsync(
            new AgentRealtimeControlRequest("pause", Pause: true),
            TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Equal(1, control.PauseCount);
        Assert.Equal(1, control.ResumeCount);
    }

    private static ProAgentRemotePlayerSource CreateSource() =>
        new("agent.exe", "2.0.0", "steam-id", "signed-license");

    private sealed class FakeRealtimeConnectionControl : IRealtimeConnectionControl
    {
        public int PauseCount { get; private set; }
        public int ResumeCount { get; private set; }
        public bool FailPause { get; init; }

        public Task PauseRealtimeAsync(CancellationToken cancellationToken = default)
        {
            PauseCount++;
            if (FailPause)
            {
                throw new InvalidOperationException("pause failed");
            }
            return Task.CompletedTask;
        }

        public void ResumeRealtime() => ResumeCount++;
    }
}
