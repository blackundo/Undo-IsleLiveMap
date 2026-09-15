using TheIsleOverlay.IslePilot;

var credentialPath = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
    "Undo-Isle",
    "IsleLiveMap",
    "islepilot-overlay.credential");
var credentials = await new IslePilotCredentialStore(credentialPath).LoadAsync();
if (credentials is null)
{
    Console.WriteLine("No valid saved IslePilot overlay session is available.");
    return 2;
}

using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
var options = new IslePilotOverlayOptions
{
    OverlayToken = credentials.OverlayToken
};
var client = new IslePilotOverlayApiClient(httpClient, options);
var me = await RetryAsync(() => client.GetMeAsync());
var map = await RetryAsync(() => client.GetMapAsync());
Console.WriteLine(
    $"allowed={map.Allowed} live_map={map.LiveMapEnabled} reason={map.Reason ?? "—"} " +
    $"self={me.PersonaName ?? me.Name ?? "—"} server={me.Server ?? "—"} " +
    $"markers={map.Markers?.Count ?? 0}");
Console.WriteLine(
    $"species={me.Species ?? "—"} growth={me.Growth?.ToString("R") ?? "—"} " +
    $"health={me.Health?.ToString("R") ?? "—"}/{me.MaxHealth?.ToString("R") ?? "—"} " +
    $"stamina={me.Stamina?.ToString("R") ?? "—"}/{me.MaxStamina?.ToString("R") ?? "—"} " +
    $"hunger={me.Hunger?.ToString("R") ?? "—"}/{me.MaxHunger?.ToString("R") ?? "—"} " +
    $"thirst={me.Thirst?.ToString("R") ?? "—"}/{me.MaxThirst?.ToString("R") ?? "—"}");
foreach (var marker in (map.Markers ?? [])
             .OrderByDescending(marker => marker.Self)
             .ThenBy(marker => marker.Label, StringComparer.OrdinalIgnoreCase))
{
    Console.WriteLine(
        $"{(marker.Self ? "SELF" : "OTHER")}\t{marker.Label ?? "—"}\t" +
        $"{marker.SteamId ?? "—"}\t{marker.X:F2}\t{marker.Y:F2}\t{marker.Z:F2}");
}

Console.WriteLine($"categories={map.Categories?.Count ?? 0} pois={map.Pois?.Count ?? 0}");
foreach (var category in map.Categories ?? [])
{
    Console.WriteLine($"CATEGORY\t{category.Id ?? "—"}\t{category.Name ?? "—"}");
}

foreach (var poi in map.Pois ?? [])
{
    Console.WriteLine(
        $"POI\t{poi.Id ?? "—"}\t{poi.CategoryId ?? "—"}\t{poi.Name ?? "—"}\t" +
        $"points={poi.Points?.Count ?? 0}\t" +
        string.Join(';', (poi.Points ?? []).Select(point => $"{point.X:R},{point.Y:R}")));
}

return 0;

static async Task<T> RetryAsync<T>(Func<Task<T>> operation)
{
    Exception? last = null;
    for (var attempt = 0; attempt < 4; attempt++)
    {
        try
        {
            return await operation();
        }
        catch (Exception exception) when (
            exception is HttpRequestException or TaskCanceledException)
        {
            last = exception;
            await Task.Delay(TimeSpan.FromMilliseconds(300 * (attempt + 1)));
        }
    }

    throw last ?? new InvalidOperationException("Probe retry failed.");
}
