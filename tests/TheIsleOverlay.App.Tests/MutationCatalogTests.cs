using TheIsleOverlay.App;

namespace TheIsleOverlay.App.Tests;

public sealed class MutationCatalogTests
{
    [Fact]
    public void EmbeddedCatalog_IsCompleteAndUnique()
    {
        var catalog = MutationCatalog.LoadDefault();

        Assert.Equal(1, catalog.SchemaVersion);
        Assert.Equal("vi-VN", catalog.Locale);
        Assert.Equal(MutationCatalog.ExpectedEntryCount, catalog.Entries.Count);
        Assert.Equal(
            catalog.Entries.Count,
            catalog.Entries.Select(entry => entry.SourceName).Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void Search_IsAccentInsensitiveAcrossEnglishAndVietnameseFields()
    {
        var entries = MutationCatalog.LoadDefault().Entries;

        var result = MutationCatalog.Search(entries, "quang hop", vietnamesePrimary: true);

        Assert.Contains(result, mutation => mutation.Source.SourceName == "Photosynthetic Regeneration");
        Assert.Contains(result, mutation => mutation.Source.SourceName == "Photosynthetic Tissue");
    }

    [Fact]
    public void Localize_SwitchesPrimaryAndSecondaryCopyWithoutDroppingSource()
    {
        var entry = MutationCatalog.LoadDefault().Entries[0];

        var vietnamese = MutationCatalog.Localize(entry, vietnamesePrimary: true);
        var english = MutationCatalog.Localize(entry, vietnamesePrimary: false);

        Assert.Equal(entry.ViName, vietnamese.Name);
        Assert.Equal(entry.SourceName, vietnamese.SecondaryName);
        Assert.Equal(entry.SourceName, english.Name);
        Assert.Equal(entry.ViName, english.SecondaryName);
        Assert.Same(entry, vietnamese.Source);
        Assert.Same(entry, english.Source);
    }
}
