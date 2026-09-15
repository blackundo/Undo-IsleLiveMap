using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;

namespace TheIsleOverlay.App;

public sealed record MutationCatalogDocument
{
    public int SchemaVersion { get; init; }
    public string Locale { get; init; } = string.Empty;
    public IReadOnlyList<MutationEntry> Entries { get; init; } = [];
}

public sealed record MutationEntry
{
    public string SourceName { get; init; } = string.Empty;
    public string ViName { get; init; } = string.Empty;
    public string SourceDescription { get; init; } = string.Empty;
    public string ViDescription { get; init; } = string.Empty;
}

public sealed record LocalizedMutation(
    string Name,
    string SecondaryName,
    string Description,
    string SecondaryDescription,
    string SecondaryLabel,
    MutationEntry Source);

public static class MutationCatalog
{
    public const int ExpectedEntryCount = 43;

    public static MutationCatalogDocument LoadDefault()
    {
        var assembly = typeof(MutationCatalog).Assembly;
        var resourceName = assembly.GetManifestResourceNames().Single(name =>
            name.EndsWith("Assets.MutationCatalog.vi.json", StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException("Không tìm thấy catalog Mutation.");
        var document = JsonSerializer.Deserialize<MutationCatalogDocument>(
            stream,
            new JsonSerializerOptions(JsonSerializerDefaults.Web))
            ?? throw new InvalidDataException("Catalog Mutation rỗng.");
        Validate(document);
        return document;
    }

    public static void Validate(MutationCatalogDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (document.SchemaVersion != 1
            || !document.Locale.Equals("vi-VN", StringComparison.OrdinalIgnoreCase)
            || document.Entries.Count != ExpectedEntryCount)
        {
            throw new InvalidDataException("Catalog Mutation không đúng schema hoặc thiếu mục.");
        }

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in document.Entries)
        {
            if (string.IsNullOrWhiteSpace(entry.SourceName)
                || string.IsNullOrWhiteSpace(entry.ViName)
                || string.IsNullOrWhiteSpace(entry.SourceDescription)
                || string.IsNullOrWhiteSpace(entry.ViDescription)
                || !names.Add(entry.SourceName.Trim()))
            {
                throw new InvalidDataException("Catalog Mutation có mục rỗng hoặc trùng tên.");
            }
        }
    }

    public static IReadOnlyList<LocalizedMutation> Search(
        IEnumerable<MutationEntry> entries,
        string? query,
        bool vietnamesePrimary)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var needle = Normalize(query);
        return entries
            .Where(entry => needle.Length == 0 || Normalize(string.Join(' ',
                entry.SourceName,
                entry.ViName,
                entry.SourceDescription,
                entry.ViDescription)).Contains(needle, StringComparison.Ordinal))
            .Select(entry => Localize(entry, vietnamesePrimary))
            .ToArray();
    }

    public static LocalizedMutation Localize(MutationEntry entry, bool vietnamesePrimary) =>
        vietnamesePrimary
            ? new LocalizedMutation(
                entry.ViName,
                entry.SourceName,
                entry.ViDescription,
                entry.SourceDescription,
                "ENGLISH ORIGINAL",
                entry)
            : new LocalizedMutation(
                entry.SourceName,
                entry.ViName,
                entry.SourceDescription,
                entry.ViDescription,
                "BẢN DỊCH TIẾNG VIỆT",
                entry);

    public static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var decomposed = value.Normalize(NormalizationForm.FormD);
        var output = new StringBuilder(decomposed.Length);
        foreach (var character in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            output.Append(character switch
            {
                'đ' => 'd',
                'Đ' => 'D',
                _ => character
            });
        }

        return output.ToString().Normalize(NormalizationForm.FormC).ToLowerInvariant().Trim();
    }
}
