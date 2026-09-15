namespace TheIsleOverlay.App;

public sealed class MandatoryModalDelay
{
    public MandatoryModalDelay(TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(duration));
        }

        Duration = duration;
    }

    public TimeSpan Duration { get; }

    public bool CanClose(TimeSpan elapsed) => elapsed >= Duration;

    public TimeSpan Remaining(TimeSpan elapsed)
    {
        if (elapsed <= TimeSpan.Zero)
        {
            return Duration;
        }

        var remaining = Duration - elapsed;
        return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
    }

    public double Progress(TimeSpan elapsed)
    {
        if (elapsed <= TimeSpan.Zero)
        {
            return 0d;
        }

        return Math.Clamp(elapsed.TotalMilliseconds / Duration.TotalMilliseconds, 0d, 1d);
    }
}
