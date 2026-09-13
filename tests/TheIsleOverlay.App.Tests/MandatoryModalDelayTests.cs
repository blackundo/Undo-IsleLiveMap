using TheIsleOverlay.App;

namespace TheIsleOverlay.App.Tests;

public sealed class MandatoryModalDelayTests
{
    [Fact]
    public void Gate_RejectsCloseUntilEntireDurationHasElapsed()
    {
        var gate = new MandatoryModalDelay(TimeSpan.FromSeconds(5));

        Assert.False(gate.CanClose(TimeSpan.Zero));
        Assert.False(gate.CanClose(TimeSpan.FromMilliseconds(4999)));
        Assert.True(gate.CanClose(TimeSpan.FromSeconds(5)));
        Assert.True(gate.CanClose(TimeSpan.FromSeconds(9)));
    }

    [Fact]
    public void RemainingAndProgress_AreClamped()
    {
        var gate = new MandatoryModalDelay(TimeSpan.FromSeconds(5));

        Assert.Equal(TimeSpan.FromSeconds(5), gate.Remaining(TimeSpan.FromSeconds(-1)));
        Assert.Equal(TimeSpan.FromSeconds(3), gate.Remaining(TimeSpan.FromSeconds(2)));
        Assert.Equal(TimeSpan.Zero, gate.Remaining(TimeSpan.FromSeconds(8)));
        Assert.Equal(0d, gate.Progress(TimeSpan.FromSeconds(-1)));
        Assert.Equal(0.5d, gate.Progress(TimeSpan.FromSeconds(2.5)));
        Assert.Equal(1d, gate.Progress(TimeSpan.FromSeconds(8)));
    }

    [Fact]
    public void Constructor_RejectsNonPositiveDuration()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new MandatoryModalDelay(TimeSpan.Zero));
    }
}
