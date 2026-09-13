namespace TheIsleOverlay.Localization;

public sealed record LocalizationOptions
{
    public Uri ManifestUri { get; init; } = new("https://isle.klong.dev/v1/releases/translation/latest");

    public string AllowedHost { get; init; } = "isle.klong.dev";

    public string ArtifactPathPrefix { get; init; } = "/assets/translation/";

    public long MaximumArchiveBytes { get; init; } = 64L * 1024L * 1024L;

    public long MaximumFileBytes { get; init; } = 8L * 1024L * 1024L;

    public int MaximumFileCount { get; init; } = 10;
}
