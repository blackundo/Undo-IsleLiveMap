namespace TheIsleOverlay.ProClient;

public sealed record ProClientOptions
{
    public static Uri ProductionBaseUri { get; } = new("https://isle.klong.dev/");

    public Uri BaseUri { get; init; } = ProductionBaseUri;

    // Optional development/test override. Production resolves the installed Agent
    // from InstallationRoot/current.json and InstallationRoot/versions/<version>.
    public string? LocalAgentPath { get; init; }

    public string InstallationRoot { get; init; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Undo-Isle",
        "IsleLiveMap",
        "Pro");

    public string CredentialPath { get; init; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Undo-Isle",
        "IsleLiveMap",
        "pro-access.credential");
}
