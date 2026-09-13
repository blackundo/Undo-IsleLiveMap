using System.Text.Json.Serialization;

namespace TheIsleOverlay.Localization;

public sealed record TranslationManifest
{
    [JsonPropertyName("version")]
    public string Version { get; init; } = string.Empty;

    [JsonPropertyName("notes")]
    public string Notes { get; init; } = string.Empty;

    [JsonPropertyName("published")]
    public bool Published { get; init; }

    [JsonPropertyName("url")]
    public string Url { get; init; } = string.Empty;

    [JsonPropertyName("sha256")]
    public string Sha256 { get; init; } = string.Empty;

    [JsonPropertyName("supportedBuildIds")]
    public IReadOnlyList<string> SupportedBuildIds { get; init; } = [];

    [JsonPropertyName("files")]
    public IReadOnlyList<TranslationFile> Files { get; init; } = [];

    [JsonPropertyName("source")]
    public string Source { get; init; } = string.Empty;
}

public sealed record TranslationFile
{
    [JsonPropertyName("source")]
    public string Source { get; init; } = string.Empty;

    [JsonPropertyName("path")]
    public string Path { get; init; } = string.Empty;

    [JsonPropertyName("sha256")]
    public string Sha256 { get; init; } = string.Empty;

    [JsonPropertyName("size")]
    public long Size { get; init; }
}
