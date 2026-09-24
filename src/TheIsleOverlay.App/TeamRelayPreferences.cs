using System.IO;
using System.Text.Json;

namespace TheIsleOverlay.App;

public enum TeamRelayProvider
{
    UndoIsle,
    KLongDev
}

public sealed record TeamRelayEndpoint(
    TeamRelayProvider Provider,
    string DisplayName,
    Uri BaseUri,
    int AdvertisedMaxMembers,
    bool IsRecommended);

public static class TeamRelayEndpoints
{
    public static TeamRelayEndpoint UndoIsle { get; } = new(
        TeamRelayProvider.UndoIsle,
        "Undo-Isle",
        new Uri("https://isle-relay.modundo.com/"),
        25,
        true);

    public static TeamRelayEndpoint KLongDev { get; } = new(
        TeamRelayProvider.KLongDev,
        "KLongDev (cũ)",
        new Uri("https://isle-relay.klong.dev/"),
        10,
        false);

    public static TeamRelayEndpoint Default => UndoIsle;

    public static TeamRelayEndpoint For(TeamRelayProvider provider) => provider switch
    {
        TeamRelayProvider.KLongDev => KLongDev,
        _ => UndoIsle
    };
}

public sealed record TeamRelayPreferences
{
    public TeamRelayProvider Provider { get; init; } = TeamRelayProvider.UndoIsle;
}

public sealed class TeamRelayPreferenceStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private readonly string _path;

    public TeamRelayPreferenceStore(string? path = null)
    {
        _path = string.IsNullOrWhiteSpace(path) ? AppPaths.TeamRelayPreferences : path;
    }

    public TeamRelayPreferences Load()
    {
        try
        {
            return File.Exists(_path)
                ? JsonSerializer.Deserialize<TeamRelayPreferences>(
                      File.ReadAllText(_path),
                      JsonOptions)
                  ?? new TeamRelayPreferences()
                : new TeamRelayPreferences();
        }
        catch (JsonException)
        {
            return new TeamRelayPreferences();
        }
        catch (IOException)
        {
            return new TeamRelayPreferences();
        }
        catch (UnauthorizedAccessException)
        {
            return new TeamRelayPreferences();
        }
    }

    public void Save(TeamRelayPreferences preferences)
    {
        var directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temporaryPath = $"{_path}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(preferences, JsonOptions));
            File.Move(temporaryPath, _path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }
}
