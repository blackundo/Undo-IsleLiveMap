using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace TheIsleOverlay.Localization;

public sealed record TheIsleInstallation(string GameRoot, string BuildId);

public interface ITheIsleInstallationLocator
{
    TheIsleInstallation? Locate();
}

public sealed partial class SteamTheIsleInstallationLocator : ITheIsleInstallationLocator
{
    private const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Steam App 376210";

    public TheIsleInstallation? Locate()
    {
        foreach (var root in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                try
                {
                    using var baseKey = RegistryKey.OpenBaseKey(root.Name == Registry.CurrentUser.Name
                        ? RegistryHive.CurrentUser
                        : RegistryHive.LocalMachine, view);
                    using var key = baseKey.OpenSubKey(UninstallKey);
                    var installLocation = key?.GetValue("InstallLocation") as string;
                    var installation = FromGameRoot(installLocation);
                    if (installation is not null)
                    {
                        return installation;
                    }
                }
                catch (Exception exception) when (
                    exception is UnauthorizedAccessException
                        or IOException
                        or System.Security.SecurityException
                        or PlatformNotSupportedException)
                {
                    // Try the next registry hive/view.
                }
            }
        }

        return null;
    }

    public static TheIsleInstallation? FromGameRoot(string? gameRoot)
    {
        if (string.IsNullOrWhiteSpace(gameRoot) || !Directory.Exists(gameRoot))
        {
            return null;
        }

        var canonicalRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(gameRoot));
        var commonDirectory = Directory.GetParent(canonicalRoot);
        var steamAppsDirectory = commonDirectory?.Parent;
        var appManifestPath = steamAppsDirectory is null
            ? null
            : Path.Combine(steamAppsDirectory.FullName, "appmanifest_376210.acf");
        if (appManifestPath is null || !File.Exists(appManifestPath))
        {
            return null;
        }

        var manifest = File.ReadAllText(appManifestPath);
        var match = BuildIdRegex().Match(manifest);
        return match.Success
            ? new TheIsleInstallation(canonicalRoot, match.Groups[1].Value)
            : null;
    }

    [GeneratedRegex("\\\"buildid\\\"\\s+\\\"([0-9]+)\\\"", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BuildIdRegex();
}

public interface ITheIsleProcessProbe
{
    bool IsGameOrBootstrapRunning();
}

public sealed class TheIsleProcessProbe : ITheIsleProcessProbe
{
    private static readonly string[] ProcessNames =
    [
        "TheIsleClient-Win64-Shipping",
        "TheIsle",
        "start_protected_game"
    ];

    public bool IsGameOrBootstrapRunning()
    {
        foreach (var processName in ProcessNames)
        {
            Process[] processes;
            try
            {
                processes = Process.GetProcessesByName(processName);
            }
            catch
            {
                return true;
            }

            try
            {
                if (processes.Any(process => !process.HasExited))
                {
                    return true;
                }
            }
            finally
            {
                foreach (var process in processes)
                {
                    process.Dispose();
                }
            }
        }

        return false;
    }
}
