using TheIsleOverlay.Localization;

namespace TheIsleOverlay.Tests;

public sealed class SteamTheIsleInstallationLocatorTests
{
    [Fact]
    public void FromSteamLibraryRoot_ResolvesInstallDirectoryAndBuildId()
    {
        var libraryRoot = CreateTemporaryDirectory();
        try
        {
            var gameRoot = Path.Combine(libraryRoot, "steamapps", "common", "The Isle Evrima");
            Directory.CreateDirectory(gameRoot);
            File.WriteAllText(
                Path.Combine(libraryRoot, "steamapps", "appmanifest_376210.acf"),
                "\"AppState\" { \"installdir\" \"The Isle Evrima\" \"buildid\" \"24664737\" }");

            var installation = SteamTheIsleInstallationLocator.FromSteamLibraryRoot(libraryRoot);

            Assert.NotNull(installation);
            Assert.Equal(Path.GetFullPath(gameRoot), installation!.GameRoot);
            Assert.Equal("24664737", installation.BuildId);
        }
        finally
        {
            DeleteTemporaryDirectory(libraryRoot);
        }
    }

    [Fact]
    public void FromSteamLibraryRoot_RejectsManifestInstallDirectoryEscape()
    {
        var libraryRoot = CreateTemporaryDirectory();
        try
        {
            Directory.CreateDirectory(Path.Combine(libraryRoot, "steamapps", "common"));
            File.WriteAllText(
                Path.Combine(libraryRoot, "steamapps", "appmanifest_376210.acf"),
                "\"AppState\" { \"installdir\" \"..\\outside\" \"buildid\" \"24664737\" }");

            Assert.Null(SteamTheIsleInstallationLocator.FromSteamLibraryRoot(libraryRoot));
        }
        finally
        {
            DeleteTemporaryDirectory(libraryRoot);
        }
    }

    [Fact]
    public void FromGameRoot_ReturnsNullForMissingOrUnreadableManifest()
    {
        var gameRoot = CreateTemporaryDirectory();
        try
        {
            Assert.Null(SteamTheIsleInstallationLocator.FromGameRoot(gameRoot));
        }
        finally
        {
            DeleteTemporaryDirectory(gameRoot);
        }
    }

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "IsleLiveMapLocatorTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteTemporaryDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
            // Best effort cleanup; the test result should reflect the locator.
        }
    }
}
