using System.IO;

namespace TheIsleOverlay.App.Tests;

public sealed class MutationGuideShortcutTests
{
    [Fact]
    public void MutationGuide_IsLocalAndDoesNotRequireServerConfirmation()
    {
        var source = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "TestAssets",
            "MainWindow.Mutation.cs"));

        Assert.Contains("new MutationGuideWindow(", source, StringComparison.Ordinal);
        Assert.DoesNotContain("_hasVerifiedPlayingSession", source, StringComparison.Ordinal);
        Assert.DoesNotContain("chỉ mở khi Live Map đã xác nhận", source, StringComparison.OrdinalIgnoreCase);
    }
}
