using System.IO;

namespace TheIsleOverlay.App.Tests;

public sealed class UndoDataPathTests
{
    [Theory]
    [InlineData("ShortcutSettings.cs")]
    [InlineData("LocalizationCoordinator.cs")]
    [InlineData("IslePilotProbe.Program.cs")]
    public void DataWriters_UseUndoIsleRoot(string fileName)
    {
        var source = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "TestAssets",
            fileName));

        Assert.Contains("\"Undo-Isle\"", source, StringComparison.Ordinal);
        Assert.DoesNotContain("\"KLongDev\",", source, StringComparison.Ordinal);
    }
}