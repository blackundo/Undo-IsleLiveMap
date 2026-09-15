using System.IO.Compression;
using System.Security.Cryptography;
using TheIsleOverlay.Localization;

namespace TheIsleOverlay.Tests;

public sealed class TranslationSecurityTests
{
    private static readonly string[] AllowedPaths =
    [
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
    ];

    [Theory]
    [InlineData("../evil.locres")]
    [InlineData("C:/evil.locres")]
    [InlineData("Engine\\evil.locres")]
    [InlineData("/absolute.locres")]
    public void NormalizeRelativePath_RejectsTraversalAndNonPortablePaths(string path)
    {
        Assert.Throws<InvalidDataException>(() =>
            TranslationManifestValidator.NormalizeRelativePath(path));
    }

    [Fact]
    public void Validator_RejectsUnpinnedHostAndQueryCredentials()
    {
        var archive = BuildArchive(out var files);
        var validator = new TranslationManifestValidator(new LocalizationOptions());

        var wrongHost = Manifest(archive, files) with { Url = "https://attacker.example/assets/translation/a.zip" };
        var queryCredential = Manifest(archive, files) with { Url = "https://isle-localization.modundo.com/assets/translation/a.zip?token=secret" };

        Assert.Throws<InvalidDataException>(() => validator.Validate(wrongHost));
        Assert.Throws<InvalidDataException>(() => validator.Validate(queryCredential));
    }

    [Fact]
    public async Task Installer_WritesOnlyApprovedResourcesAndPersistsVerifiedState()
    {
        var archive = BuildArchive(out var files);
        var manifest = Manifest(archive, files);
        var gameRoot = Path.Combine(Path.GetTempPath(), $"isle-game-{Guid.NewGuid():N}");
        var stateRoot = Path.Combine(Path.GetTempPath(), $"isle-state-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(gameRoot);
            var installer = new TranslationPackageInstaller();

            var state = await installer.InstallAsync(
                manifest,
                archive,
                gameRoot,
                stateRoot,
                "24664737",
                GameLocale.Vietnamese);

            Assert.Equal(10, state.Files.Count);
            Assert.Equal("vi", state.ActiveLocale);
            Assert.True(File.Exists(Path.Combine(stateRoot, "translation-state.json")));
            foreach (var path in AllowedPaths)
            {
                Assert.True(File.Exists(Path.Combine(gameRoot, path.Replace('/', Path.DirectorySeparatorChar))));
            }
        }
        finally
        {
            if (Directory.Exists(gameRoot)) Directory.Delete(gameRoot, recursive: true);
            if (Directory.Exists(stateRoot)) Directory.Delete(stateRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Installer_RejectsTamperedArchiveBeforeWritingGameFiles()
    {
        var archive = BuildArchive(out var files);
        var manifest = Manifest(archive, files);
        archive[^1] ^= 0x5A;
        var gameRoot = Path.Combine(Path.GetTempPath(), $"isle-game-{Guid.NewGuid():N}");
        var stateRoot = Path.Combine(Path.GetTempPath(), $"isle-state-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(gameRoot);
            var installer = new TranslationPackageInstaller();

            await Assert.ThrowsAsync<InvalidDataException>(() => installer.InstallAsync(
                manifest,
                archive,
                gameRoot,
                stateRoot,
                "24664737",
                GameLocale.Vietnamese));

            Assert.Empty(Directory.GetFiles(gameRoot, "*", SearchOption.AllDirectories));
        }
        finally
        {
            if (Directory.Exists(gameRoot)) Directory.Delete(gameRoot, recursive: true);
            if (Directory.Exists(stateRoot)) Directory.Delete(stateRoot, recursive: true);
        }
    }

    [Fact]
    public void Validator_RejectsUnsupportedGameBuild()
    {
        var archive = BuildArchive(out var files);
        var manifest = Manifest(archive, files);

        Assert.Throws<InvalidOperationException>(() =>
            new TranslationManifestValidator(new LocalizationOptions()).Validate(manifest, "999"));
    }

    private static byte[] BuildArchive(out IReadOnlyList<TranslationFile> files)
    {
        var list = new List<TranslationFile>();
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            for (var index = 0; index < AllowedPaths.Length; index++)
            {
                var bytes = System.Text.Encoding.UTF8.GetBytes($"safe-locres-{index}");
                var path = AllowedPaths[index];
                var entry = archive.CreateEntry(path, CompressionLevel.NoCompression);
                using (var stream = entry.Open())
                {
                    stream.Write(bytes);
                }

                list.Add(new TranslationFile
                {
                    Source = path,
                    Path = path,
                    Sha256 = Convert.ToHexString(SHA256.HashData(bytes)),
                    Size = bytes.Length
                });
            }
        }

        files = list;
        return output.ToArray();
    }

    private static TranslationManifest Manifest(byte[] archive, IReadOnlyList<TranslationFile> files) => new()
    {
        Version = "1.1.4",
        Notes = "test",
        Published = true,
        Url = "https://isle-localization.modundo.com/assets/translation/isle-live-map-vi-1.1.4.zip",
        Sha256 = Convert.ToHexString(SHA256.HashData(archive)),
        SupportedBuildIds = ["24664737"],
        Files = files,
        Source = "isle-live-map-localization"
    };
}
