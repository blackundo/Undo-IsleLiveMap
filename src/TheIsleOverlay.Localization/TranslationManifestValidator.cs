using System.Text.RegularExpressions;

namespace TheIsleOverlay.Localization;

public sealed partial class TranslationManifestValidator
{
    public const int RequiredFileCount = 10;

    private static readonly HashSet<string> AllowedTargets = new(
        new[]
        {
            "Engine/Content/Localization/Engine/vi/Engine.locres",
            "Engine/Content/Localization/Engine/vi-VN/Engine.locres",
            "Engine/Plugins/Online/OnlineSubsystem/Content/Localization/OnlineSubsystem/vi/OnlineSubsystem.locres",
            "Engine/Plugins/Online/OnlineSubsystem/Content/Localization/OnlineSubsystem/vi-VN/OnlineSubsystem.locres",
            "Engine/Plugins/Online/OnlineSubsystemSteam/Content/Localization/OnlineSubsystemSteam/vi/OnlineSubsystemSteam.locres",
            "Engine/Plugins/Online/OnlineSubsystemSteam/Content/Localization/OnlineSubsystemSteam/vi-VN/OnlineSubsystemSteam.locres",
            "Engine/Plugins/Online/OnlineSubsystemUtils/Content/Localization/OnlineSubsystemUtils/vi/OnlineSubsystemUtils.locres",
            "Engine/Plugins/Online/OnlineSubsystemUtils/Content/Localization/OnlineSubsystemUtils/vi-VN/OnlineSubsystemUtils.locres",
            "TheIsle/Content/Localization/Game/vi/Game.locres",
            "TheIsle/Content/Localization/Game/vi-VN/Game.locres"
        },
        StringComparer.OrdinalIgnoreCase);

    public TranslationManifestValidator(LocalizationOptions options)
    {
        Options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public LocalizationOptions Options { get; }

    public void Validate(TranslationManifest manifest, string? gameBuildId = null)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (!manifest.Published)
        {
            throw new InvalidDataException("Gói Việt hóa chưa được phát hành.");
        }

        if (!SemanticVersionRegex().IsMatch(manifest.Version)
            || !Sha256Regex().IsMatch(manifest.Sha256)
            || string.IsNullOrWhiteSpace(manifest.Source))
        {
            throw new InvalidDataException("Manifest Việt hóa không hợp lệ.");
        }

        if (!Uri.TryCreate(manifest.Url, UriKind.Absolute, out var artifactUri)
            || artifactUri.Scheme != Uri.UriSchemeHttps
            || !artifactUri.Host.Equals(Options.AllowedHost, StringComparison.OrdinalIgnoreCase)
            || !artifactUri.AbsolutePath.StartsWith(Options.ArtifactPathPrefix, StringComparison.Ordinal)
            || !string.IsNullOrEmpty(artifactUri.Query)
            || !string.IsNullOrEmpty(artifactUri.Fragment))
        {
            throw new InvalidDataException("URL gói Việt hóa nằm ngoài máy chủ được phép.");
        }

        if (manifest.Files.Count != RequiredFileCount
            || manifest.Files.Count > Options.MaximumFileCount
            || manifest.SupportedBuildIds.Count == 0
            || manifest.SupportedBuildIds.Any(id => !BuildIdRegex().IsMatch(id)))
        {
            throw new InvalidDataException("Danh sách file hoặc build game trong manifest không hợp lệ.");
        }

        if (gameBuildId is not null
            && !manifest.SupportedBuildIds.Contains(gameBuildId, StringComparer.Ordinal))
        {
            throw new InvalidOperationException($"Bản game {gameBuildId} chưa được gói Việt hóa hỗ trợ.");
        }

        var sources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in manifest.Files)
        {
            var source = NormalizeRelativePath(file.Source);
            var target = NormalizeRelativePath(file.Path);
            if (!sources.Add(source)
                || !targets.Add(target)
                || !AllowedTargets.Contains(target)
                || !source.Equals(target, StringComparison.OrdinalIgnoreCase)
                || !Sha256Regex().IsMatch(file.Sha256)
                || file.Size <= 0
                || file.Size > Options.MaximumFileBytes)
            {
                throw new InvalidDataException("Manifest chứa file Việt hóa không được phép.");
            }
        }

        if (!targets.SetEquals(AllowedTargets))
        {
            throw new InvalidDataException("Manifest không chứa đúng bộ 10 resource đã duyệt.");
        }
    }

    public static string NormalizeRelativePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)
            || Path.IsPathRooted(path)
            || path.Contains('\\')
            || path.Contains(':'))
        {
            throw new InvalidDataException("Đường dẫn resource không hợp lệ.");
        }

        var components = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (components.Length == 0
            || components.Any(component => component is "." or ".."))
        {
            throw new InvalidDataException("Đường dẫn resource thoát khỏi thư mục game.");
        }

        return string.Join('/', components);
    }

    [GeneratedRegex("^[0-9]+\\.[0-9]+\\.[0-9]+$", RegexOptions.CultureInvariant)]
    private static partial Regex SemanticVersionRegex();

    [GeneratedRegex("^[A-Fa-f0-9]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex Sha256Regex();

    [GeneratedRegex("^[0-9]+$", RegexOptions.CultureInvariant)]
    private static partial Regex BuildIdRegex();
}
