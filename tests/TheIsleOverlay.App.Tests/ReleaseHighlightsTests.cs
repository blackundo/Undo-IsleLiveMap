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
            "proPresentation.IsVerified",
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
    public void Modal_IsASixStep202BriefingWithFinalOptOut()
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

        Assert.Equal("2.0.2", ReleaseHighlightsWindow.ReleaseVersion);
        Assert.Equal(6, ReleaseHighlightsWindow.PageCount);
        Assert.Contains("THÊM NGUỒN SERVER ORIGIN VÀ GACHA", allCopy, StringComparison.Ordinal);
        Assert.Contains("Chọn nguồn phía dưới nút Mở Map", allCopy, StringComparison.Ordinal);
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
        Assert.Contains("Không hiển thị lại thông báo này cho phiên bản 2.0.2", allCopy, StringComparison.Ordinal);
        Assert.Equal(
            "HOÀN TẤT",
            (string?)Control("FinishButton").Attribute("Content"));
        var pages = new[] { "PageIntro", "PageOne", "PageTwo", "PageThree", "PageFour", "PageFive" };
        var markers = new[] { "StepIntroMarker", "StepOneMarker", "StepTwoMarker", "StepThreeMarker", "StepFourMarker", "StepFiveMarker" };
        for (var step = 0; step < ReleaseHighlightsWindow.PageCount; step++)
        {
            Assert.NotNull(Control(pages[step]));
            Assert.NotNull(Control(markers[step]));
        }

        var optOut = Control("DoNotShowAgainCheckBox");
        Assert.Contains(
            optOut.Ancestors(),
            ancestor => string.Equals(
                (string?)ancestor.Attribute(nameAttribute),
                "PageFive",
                StringComparison.Ordinal));
        Assert.Equal("https://isle-system.modundo.com/", ReleaseHighlightsWindow.ProLandingPageUri.AbsoluteUri);

    }
}
