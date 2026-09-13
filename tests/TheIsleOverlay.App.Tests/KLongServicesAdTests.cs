using System.IO;
using System.Runtime.CompilerServices;

namespace TheIsleOverlay.App.Tests;

public sealed class KLongServicesAdTests
{
    private static readonly string RepositoryRoot = FindRepositoryRoot();

    [Fact]
    public void Startup_ShowsDedicatedServicesAdOncePerProcessBeforeProPromotion()
    {
        var source = File.ReadAllText(Path.Combine(
            RepositoryRoot,
            "src",
            "TheIsleOverlay.App",
            "HomeWindow.xaml.cs"));

        var adIndex = source.IndexOf("TryMarkServicesAdShown", StringComparison.Ordinal);
        var proIndex = source.IndexOf("ShowProPromotionIfNeeded();", adIndex, StringComparison.Ordinal);

        Assert.True(adIndex >= 0);
        Assert.True(proIndex > adIndex);
    }

    [Fact]
    public void AdCopy_IncludesServicesContactsAndPrivacyEnhancedVideo()
    {
        var xaml = File.ReadAllText(Path.Combine(
            RepositoryRoot,
            "src",
            "TheIsleOverlay.App",
            "KLongServicesAdWindow.xaml"));
        var source = File.ReadAllText(Path.Combine(
            RepositoryRoot,
            "src",
            "TheIsleOverlay.App",
            "KLongServicesAdWindow.xaml.cs"));

        Assert.Contains("HOÀNG KIM LONG", xaml, StringComparison.Ordinal);
        Assert.Contains("website · mod game · tool/app · bot tự động", xaml, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("0705 878 781", xaml, StringComparison.Ordinal);
        Assert.Contains("TFT", xaml, StringComparison.Ordinal);
        Assert.Contains("FC", xaml, StringComparison.Ordinal);
        Assert.Contains("NRO", xaml, StringComparison.Ordinal);
        Assert.Contains("HSO", xaml, StringComparison.Ordinal);
        Assert.Contains("VLTN", xaml, StringComparison.Ordinal);
        Assert.Contains("Assets/Advertising/tft.png", xaml, StringComparison.Ordinal);
        Assert.Contains("Assets/Advertising/fco4.jpg", xaml, StringComparison.Ordinal);
        Assert.Contains("Assets/Advertising/nro.jpg", xaml, StringComparison.Ordinal);
        Assert.Contains("Assets/Advertising/hso.jpg", xaml, StringComparison.Ordinal);
        Assert.Contains("Assets/Advertising/vltn.webp", xaml, StringComparison.Ordinal);
        Assert.Contains("NHẬN DỰ ÁN GAME &amp; WEB", xaml, StringComparison.Ordinal);
        Assert.Contains("www.youtube-nocookie.com/embed/8mMaXM2Y-EQ", source, StringComparison.Ordinal);
        Assert.Contains("YouTubeWatchUri", source, StringComparison.Ordinal);
        Assert.Contains("browser.Navigate(YouTubeWatchUri.AbsoluteUri)", source, StringComparison.Ordinal);
        Assert.Contains("https://www.facebook.com/klong.dev/", source, StringComparison.Ordinal);
        Assert.Contains("TimeSpan.FromSeconds(5)", source, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("https://www.youtube-nocookie.com/embed/8mMaXM2Y-EQ", true)]
    [InlineData("https://www.youtube.com/watch?v=8mMaXM2Y-EQ&embed=1", true)]
    [InlineData("https://www.youtube.com/watch?v=other-video", false)]
    [InlineData("about:blank", true)]
    [InlineData("https://example.com/", false)]
    [InlineData("http://www.youtube-nocookie.com/embed/8mMaXM2Y-EQ", false)]
    public void EmbedNavigation_UsesStrictAllowlist(string uri, bool expected)
    {
        Assert.Equal(expected, KLongServicesAdWindow.IsAllowedEmbeddedUri(uri));
    }

    private static string FindRepositoryRoot(
        [CallerFilePath] string sourceFile = "")
    {
        foreach (var start in new[]
                 {
                     new DirectoryInfo(AppContext.BaseDirectory),
                     new DirectoryInfo(Environment.CurrentDirectory),
                     new DirectoryInfo(Path.GetDirectoryName(sourceFile) ?? string.Empty)
                 })
        {
            var directory = start;
            while (directory is not null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "TheIsleOverlay.sln")))
                {
                    return directory.FullName;
                }

                directory = directory.Parent;
            }
        }

        throw new DirectoryNotFoundException("Cannot locate repository root.");
    }
}
