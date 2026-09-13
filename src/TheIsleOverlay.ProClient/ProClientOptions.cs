namespace TheIsleOverlay.ProClient;

public sealed record ProClientOptions
{
    public static Uri ProductionBaseUri { get; } = new("https://isle.klong.dev/");

    public Uri BaseUri { get; init; } = ProductionBaseUri;

    public string LocalAgentPath { get; init; } = Path.Combine(
        AppContext.BaseDirectory, "ProAgent", "IsleLiveMap.Pro.Agent.exe");

    public string InstallationRoot { get; init; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "KLongDev",
        "IsleLiveMap",
        "Pro");

    public string CredentialPath { get; init; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "KLongDev",
        "IsleLiveMap",
        "pro-access.credential");
}
