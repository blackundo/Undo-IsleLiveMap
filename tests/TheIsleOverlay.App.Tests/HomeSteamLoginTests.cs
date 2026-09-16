using System.IO;
using System.Xml.Linq;

namespace TheIsleOverlay.App.Tests;

public sealed class HomeSteamLoginTests
{
    [Fact]
    public void Home_UsesIslePilotStatsWithDirectGpsAndRemovesWebsiteSourceBlocks()
    {
        var document = XDocument.Load(Path.Combine(
            AppContext.BaseDirectory,
            "TestAssets",
            "HomeWindow.xaml"));
        XName nameAttribute = "{http://schemas.microsoft.com/winfx/2006/xaml}Name";

        XElement Control(string name) => Assert.Single(
            document.Descendants(),
            element => string.Equals((string?)element.Attribute(nameAttribute), name, StringComparison.Ordinal));

        var liveMapButton = Control("SteamLoginButton");
        var liveMapTitle = Control("SteamLoginTitleLabel");
        var logoutSteamButton = Control("LogoutSteamButton");
        var logoutProButton = Control("LogoutProButton");
        Assert.NotEqual("Collapsed", (string?)liveMapButton.Attribute("Visibility"));
        Assert.Equal("SteamLoginButton_Click", (string?)liveMapButton.Attribute("Click"));
        Assert.Equal(
            "SteamLoginPanel_Loaded",
            (string?)liveMapButton.Parent?.Attribute("Loaded"));
        Assert.Equal("MỞ LIVE MAP", (string?)liveMapTitle.Attribute("Text"));
        Assert.Equal("ĐĂNG XUẤT STEAM", (string?)logoutSteamButton.Attribute("Content"));
        Assert.Equal("LogoutSteamButton_Click", (string?)logoutSteamButton.Attribute("Click"));
        Assert.Equal("XÓA KÍCH HOẠT PRO", (string?)logoutProButton.Attribute("Content"));
        Assert.DoesNotContain(
            document.Descendants(),
            element => new[] { "EraSourceButton", "PandoraSourceButton" }
                .Contains((string?)element.Attribute(nameAttribute), StringComparer.Ordinal));
        Assert.DoesNotContain(
            document.Descendants(),
            element => new[] { "DinoSourceButton", "PremiumSourceButton", "HoHoSourceButton" }
                .Contains((string?)element.Attribute(nameAttribute), StringComparer.Ordinal));

        var text = document.Descendants()
            .Select(element => (string?)element.Attribute("Text"))
            .Where(value => value is not null)
            .ToArray();
        Assert.Contains("KÍCH HOẠT LIVE MAP", text);
        Assert.Contains("KÍCH HOẠT PRO · KEY MIỄN PHÍ", text);
        Assert.DoesNotContain(text, value => value?.Contains("28K", StringComparison.OrdinalIgnoreCase) == true);
        Assert.Contains("GPS trực tiếp · Tự chọn Origin, Gacha hoặc IslePilot theo server", text);
        Assert.DoesNotContain("SERVER DÙNG WEBSITE RIÊNG", text);
    }

    [Fact]
    public void ProActivationModal_OffersKeyWithoutSteam()
    {
        var document = XDocument.Load(Path.Combine(
            AppContext.BaseDirectory,
            "TestAssets",
            "ProKeyActivationWindow.xaml"));

        var allCopy = string.Join(
            " ",
            document.Descendants().SelectMany(element => new[]
            {
                (string?)element.Attribute("Text"),
                (string?)element.Attribute("Content"),
                (string?)element.Attribute("Title")
            }));

        Assert.Contains("không cần đăng nhập Steam.", allCopy, StringComparison.Ordinal);
        Assert.Contains("KÍCH HOẠT PRO", allCopy, StringComparison.Ordinal);
        Assert.Contains("LẤY KEY FREE", allCopy, StringComparison.Ordinal);
        Assert.DoesNotContain("SteamID64", allCopy, StringComparison.Ordinal);
        Assert.Equal(
            "https://modundo.com/islevip",
            ProKeyActivationWindow.FreeKeyPageUri.AbsoluteUri);
    }

    [Fact]
    public void HomeStartup_DoesNotShowDonateModal()
    {
        var source = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "TestAssets",
            "HomeWindow.xaml.cs"));

        Assert.DoesNotContain("TryMarkDonatePromptShown", source, StringComparison.Ordinal);
        Assert.DoesNotContain("new DonateWindow", source, StringComparison.Ordinal);
        Assert.Null(typeof(HomeWindow).Assembly.GetType("TheIsleOverlay.App.DonateWindow"));
    }

    [Fact]
    public void HomeStartup_ShowsDedicatedProPromotionOnlyWithoutCurrentAccess()
    {
        var source = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "TestAssets",
            "HomeWindow.xaml.cs"));

        Assert.Contains("ShowProPromotionIfNeeded", source, StringComparison.Ordinal);
        Assert.Contains("new ProPromotionWindow", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ProPromotion_OffersKeyActivation()
    {
        var document = XDocument.Load(Path.Combine(
            AppContext.BaseDirectory,
            "TestAssets",
            "ProPromotionWindow.xaml"));
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

        Assert.Contains("KEY PRO MIỄN PHÍ", allCopy, StringComparison.Ordinal);
        Assert.Contains("LẤY KEY FREE", allCopy, StringComparison.Ordinal);
        Assert.Contains("FULL TẤT CẢ SERVER", allCopy, StringComparison.Ordinal);
        Assert.Contains("LIVE SKIN · ALT + S", allCopy, StringComparison.Ordinal);
        Assert.Contains("GARAGE · ALT + G", allCopy, StringComparison.Ordinal);
        Assert.Contains("Không phải hack", allCopy, StringComparison.Ordinal);
        Assert.DoesNotContain("28K", allCopy, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(
            "NHẬP KEY",
            (string?)Control("ActivateProButton").Attribute("Content"));
        Assert.Equal(
            "GetFreeKeyButton_Click",
            (string?)Control("GetFreeKeyButton").Attribute("Click"));
        Assert.Contains(
            document.Descendants(),
            element => string.Equals(
                (string?)element.Attribute("Source"),
                "Assets/ProMapPreview.png",
                StringComparison.Ordinal));
        Assert.Equal(
            "https://modundo.com/islevip",
            ProPromotionWindow.FreeKeyPageUri.AbsoluteUri);
    }

    [Fact]
    public void LiveMapHandler_ComposesIslePilotStatsWithLocalPositionAndProEntities()
    {
        var source = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "TestAssets",
            "HomeWindow.IslePilot.cs"));

        Assert.Contains("IslePilotRealtimeSession.Create", source, StringComparison.Ordinal);
        Assert.Contains("new LocalPositionTelemetrySession(", source, StringComparison.Ordinal);
        Assert.Contains("App.CurrentApp.TakeLocalTelemetrySource()", source, StringComparison.Ordinal);
        Assert.Contains("TakeProPlayerSource()", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Pandora_SourceCapturesTheCompleteHostSessionForItsExpressApi()
    {
        var source = TelemetrySourceDefinition.Pandora;

        Assert.Equal("https://islapandora.eu/", source.BaseUri.AbsoluteUri);
        Assert.Equal("https://islapandora.eu/live-map", source.LoginUri.AbsoluteUri);
        Assert.Equal(TelemetrySourceKind.Pandora, source.Kind);
        Assert.True(source.CaptureAllHostCookies);
        Assert.Same(source, TelemetrySourceDefinition.FromId("pandora"));
    }
}
