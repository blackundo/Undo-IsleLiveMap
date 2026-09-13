using System.Text;

namespace TheIsleOverlay.Localization;

public static class GameLanguageSettings
{
    private const string SectionName = "[Internationalization]";

    public static string DefaultConfigPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "TheIsle",
        "Saved",
        "Config",
        "WindowsClient",
        "GameUserSettings.ini");

    public static string Rewrite(string original, GameLocale locale)
    {
        ArgumentNullException.ThrowIfNull(original);
        var output = new List<string>();
        var insideInternationalization = false;

        foreach (var line in original.TrimStart('\uFEFF').Replace("\r\n", "\n").Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith('[') && trimmed.EndsWith(']'))
            {
                insideInternationalization = trimmed.Equals(
                    SectionName,
                    StringComparison.OrdinalIgnoreCase);
                if (insideInternationalization)
                {
                    continue;
                }
            }

            if (!insideInternationalization)
            {
                output.Add(line.TrimEnd('\r'));
            }
        }

        while (output.Count > 0 && string.IsNullOrWhiteSpace(output[^1]))
        {
            output.RemoveAt(output.Count - 1);
        }

        var culture = locale == GameLocale.Vietnamese ? "vi" : "en";
        output.Add(string.Empty);
        output.Add(SectionName);
        output.Add($"Culture={culture}");
        output.Add($"Language={culture}");
        output.Add($"Locale={culture}");
        output.Add(string.Empty);
        return string.Join("\r\n", output);
    }

    public static GameLocale Read(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var insideInternationalization = false;
        foreach (var line in text.Replace("\r\n", "\n").Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith('[') && trimmed.EndsWith(']'))
            {
                insideInternationalization = trimmed.Equals(
                    SectionName,
                    StringComparison.OrdinalIgnoreCase);
                continue;
            }

            if (!insideInternationalization
                || !trimmed.StartsWith("Culture", StringComparison.OrdinalIgnoreCase)
                || !trimmed.Contains('='))
            {
                continue;
            }

            var value = trimmed[(trimmed.IndexOf('=') + 1)..].Trim();
            return value.Equals("vi", StringComparison.OrdinalIgnoreCase)
                   || value.Equals("vi-VN", StringComparison.OrdinalIgnoreCase)
                ? GameLocale.Vietnamese
                : GameLocale.English;
        }

        return GameLocale.English;
    }

    public static async Task SetAsync(
        string configPath,
        GameLocale locale,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configPath);
        var parent = Path.GetDirectoryName(Path.GetFullPath(configPath))
            ?? throw new InvalidOperationException("Đường dẫn cấu hình game không có thư mục cha.");
        Directory.CreateDirectory(parent);

        var original = File.Exists(configPath)
            ? await File.ReadAllTextAsync(configPath, cancellationToken)
            : string.Empty;
        var backupPath = configPath + ".language-backup";
        if (File.Exists(configPath) && !File.Exists(backupPath))
        {
            File.Copy(configPath, backupPath);
        }

        var stagedPath = configPath + ".new";
        await File.WriteAllTextAsync(
            stagedPath,
            Rewrite(original, locale),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            cancellationToken);

        try
        {
            File.Move(stagedPath, configPath, overwrite: true);
        }
        catch
        {
            if (File.Exists(backupPath))
            {
                File.Copy(backupPath, configPath, overwrite: true);
            }

            throw;
        }
        finally
        {
            if (File.Exists(stagedPath))
            {
                File.Delete(stagedPath);
            }
        }
    }
}
