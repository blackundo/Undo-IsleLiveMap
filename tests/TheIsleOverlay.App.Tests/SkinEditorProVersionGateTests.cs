using System.IO;

namespace TheIsleOverlay.App.Tests;

public sealed class SkinEditorProVersionGateTests
{
    [Fact]
    public void FreeBuild_KeepsSkinEditorHotkeyAsProVersionPromptOnly()
    {
        var source = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "TestAssets",
            "MainWindow.xaml.cs"));

        Assert.Contains(
            "_skinEditorHotkeyRegistered = RegisterHotKey(handle, SkinEditorHotkeyId, ModAlt, KeyS)",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "Skin Editor chỉ có trong phiên bản Undo-IsleLiveMap Pro",
            source,
            StringComparison.Ordinal);
        Assert.Contains("ProAgent", source, StringComparison.Ordinal);
        Assert.Contains("IsleLiveMap.Pro.SkinEditor.dll", source, StringComparison.Ordinal);
        Assert.Contains("LoadFromAssemblyPath", source, StringComparison.Ordinal);
    }
}
