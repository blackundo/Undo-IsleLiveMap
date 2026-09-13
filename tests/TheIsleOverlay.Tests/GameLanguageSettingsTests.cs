using TheIsleOverlay.Localization;

namespace TheIsleOverlay.Tests;

public sealed class GameLanguageSettingsTests
{
    [Fact]
    public void Rewrite_SwitchesLocaleWithoutTouchingOtherSections()
    {
        const string original = "[Video]\nQuality=3\n[Internationalization]\nCulture=en\nLanguage=en\n[Audio]\nVolume=1\n";

        var vietnamese = GameLanguageSettings.Rewrite(original, GameLocale.Vietnamese);
        var english = GameLanguageSettings.Rewrite(vietnamese, GameLocale.English);

        Assert.Contains("[Video]\r\nQuality=3", vietnamese, StringComparison.Ordinal);
        Assert.Contains("[Audio]\r\nVolume=1", vietnamese, StringComparison.Ordinal);
        Assert.Contains("Culture=vi\r\nLanguage=vi\r\nLocale=vi", vietnamese, StringComparison.Ordinal);
        Assert.DoesNotContain("Culture=en", vietnamese, StringComparison.Ordinal);
        Assert.Contains("Culture=en\r\nLanguage=en\r\nLocale=en", english, StringComparison.Ordinal);
        Assert.Equal(1, Count(english, "[Internationalization]"));
    }

    [Fact]
    public async Task SetAsync_CreatesBackupAndCanSwitchBothDirections()
    {
        var root = Path.Combine(Path.GetTempPath(), $"isle-locale-{Guid.NewGuid():N}");
        var configPath = Path.Combine(root, "GameUserSettings.ini");
        try
        {
            Directory.CreateDirectory(root);
            await File.WriteAllTextAsync(configPath, "[Video]\nQuality=2\n");

            await GameLanguageSettings.SetAsync(configPath, GameLocale.Vietnamese);
            await GameLanguageSettings.SetAsync(configPath, GameLocale.English);

            Assert.True(File.Exists(configPath + ".language-backup"));
            var saved = await File.ReadAllTextAsync(configPath);
            Assert.Equal(GameLocale.English, GameLanguageSettings.Read(saved));
            Assert.Contains("Quality=2", saved, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static int Count(string value, string needle) =>
        value.Split(needle, StringSplitOptions.None).Length - 1;
}
