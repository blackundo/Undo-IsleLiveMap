using System.IO;
using System.Xml.Linq;

namespace TheIsleOverlay.App.Tests;

public sealed class ReleaseHighlightsTests
{
    [Fact]
    public void Home_ShowsReleaseWizardUnlessTheCurrentVersionWasHidden()
    {
        var source = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "TestAssets",
            "HomeWindow.xaml.cs"));

        Assert.Contains(
            "highlightsStore.ShouldShow(ReleaseHighlightsWindow.ReleaseVersion)",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "proPresentation.HasCurrentProAccess",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "new ReleaseHighlightsWindow(",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "ShowProPromotionIfNeeded();",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ExpiringProSession_ReturnsToFreeAndShowsPromotionAgain()
    {
        var source = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "TestAssets",
            "HomeWindow.Pro.cs"));
        var expiryHandler = source[source.IndexOf(
            "private async void ProExpiryTimer_Tick",
            StringComparison.Ordinal)..];

        Assert.Contains("ApplyProAccessState(_proAccess);", expiryHandler, StringComparison.Ordinal);
        Assert.Contains("ShowProPromotionIfNeeded();", expiryHandler, StringComparison.Ordinal);
        Assert.True(
            expiryHandler.IndexOf("ApplyProAccessState(_proAccess);", StringComparison.Ordinal)
            < expiryHandler.IndexOf("ShowProPromotionIfNeeded();", StringComparison.Ordinal));
    }

    [Fact]
    public void Modal_IsAFiveStep200BriefingWithFinalOptOut()
    {
        var document = XDocument.Load(Path.Combine(
            AppContext.BaseDirectory,
            "TestAssets",
            "ReleaseHighlightsWindow.xaml"));
        XName nameAttribute = "{http://schemas.microsoft.com/winfx/2006/xaml}Name";

        XElement Control(string name) => Assert.Single(
            document.Descendants(),
            element => string.Equals(
                (string?)element.Attribute(nameAttribute),
                name,
                StringComparison.Ordinal));

        var allCopy = string.Join(
            " ",
            document.Descendants().SelectMany(element => new[]
            {
                (string?)element.Attribute("Text"),
                (string?)element.Attribute("Content")
            }));

        Assert.Equal("2.0.0", ReleaseHighlightsWindow.ReleaseVersion);
        Assert.Equal(5, ReleaseHighlightsWindow.PageCount);
        Assert.Contains("DINO STATS ĐÚNG NGUỒN HƠN", allCopy, StringComparison.Ordinal);
        Assert.Contains("GACHA", allCopy, StringComparison.Ordinal);
        Assert.Contains("VIỆT HÓA GAME", allCopy, StringComparison.Ordinal);
        Assert.Contains("43 MUTATION", allCopy, StringComparison.Ordinal);
        Assert.Contains("ALT + U", allCopy, StringComparison.Ordinal);
        Assert.Contains("ĐẶT MỐC, NHẬP XYZ, XÓA NGAY", allCopy, StringComparison.Ordinal);
        Assert.Contains("ALT + M", allCopy, StringComparison.Ordinal);
        Assert.Contains("Delete", allCopy, StringComparison.Ordinal);
        Assert.Contains("PHÍM TẮT RÕ RÀNG, BLOCK TỰ CHỦ", allCopy, StringComparison.Ordinal);
        Assert.Contains("CTRL + SHIFT + O", allCopy, StringComparison.Ordinal);
        Assert.Contains("ALT + P", allCopy, StringComparison.Ordinal);
        Assert.Contains("FREE / PRO RÕ RÀNG", allCopy, StringComparison.Ordinal);
        Assert.Contains("Không hiển thị lại thông báo này cho phiên bản 2.0.0", allCopy, StringComparison.Ordinal);
        Assert.Equal(
            "HOÀN TẤT",
            (string?)Control("FinishButton").Attribute("Content"));
        for (var step = 1; step <= ReleaseHighlightsWindow.PageCount; step++)
        {
            Assert.NotNull(Control($"Page{NumberWord(step)}"));
            Assert.NotNull(Control($"Step{NumberWord(step)}Marker"));
        }

        var optOut = Control("DoNotShowAgainCheckBox");
        Assert.Contains(
            optOut.Ancestors(),
            ancestor => string.Equals(
                (string?)ancestor.Attribute(nameAttribute),
                "PageFive",
                StringComparison.Ordinal));
        Assert.Equal("https://isle.klong.dev/", ReleaseHighlightsWindow.ProLandingPageUri.AbsoluteUri);

        static string NumberWord(int value) => value switch
        {
            1 => "One",
            2 => "Two",
            3 => "Three",
            4 => "Four",
            5 => "Five",
            _ => throw new ArgumentOutOfRangeException(nameof(value))
        };
    }
}
