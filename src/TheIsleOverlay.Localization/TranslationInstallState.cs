using System.Text.Json.Serialization;

namespace TheIsleOverlay.Localization;

public sealed record TranslationInstallState
{
    [JsonPropertyName("resourceVersion")]
    public string ResourceVersion { get; init; } = string.Empty;

    [JsonPropertyName("archiveSha256")]
    public string ArchiveSha256 { get; init; } = string.Empty;

    [JsonPropertyName("gameBuildId")]
    public string GameBuildId { get; init; } = string.Empty;

    [JsonPropertyName("installedAt")]
    public DateTimeOffset InstalledAt { get; init; }

    [JsonPropertyName("files")]
    public IReadOnlyList<string> Files { get; init; } = [];

    [JsonPropertyName("backupDirectory")]
    public string BackupDirectory { get; init; } = string.Empty;

    [JsonPropertyName("activeLocale")]
    public string ActiveLocale { get; init; } = "en";
}
