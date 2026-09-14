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
    private const string SteamKey = @"SOFTWARE\Valve\Steam";
    private const string AppManifestFileName = "appmanifest_376210.acf";
    private const string LibraryFoldersFileName = "libraryfolders.vdf";

    public TheIsleInstallation? Locate()
    {
        var steamRoots = new List<string>();

        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        {
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                try
                {
                    using var baseKey = RegistryKey.OpenBaseKey(hive, view);

                    // The uninstall entry is the quickest path, but it is not
                    // guaranteed to exist (portable Steam installs, registry
                    // cleanup tools and some per-user installs remove it).
                    using var uninstallKey = baseKey.OpenSubKey(UninstallKey);
                    var installation = FromGameRoot(uninstallKey?.GetValue("InstallLocation") as string);
                    if (installation is not null)
                    {
                        return installation;
                    }

                    using var steamKey = baseKey.OpenSubKey(SteamKey);
                    AddPath(steamRoots, steamKey?.GetValue("InstallPath") as string);
                    AddPath(steamRoots, steamKey?.GetValue("SteamPath") as string);
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

        // A Steam library can be moved to another drive without changing the
        // game's uninstall entry. Resolve every library advertised by Steam's
        // own config instead of assuming the primary Steam directory.
        AddPath(steamRoots, Environment.GetEnvironmentVariable("SteamPath"));
        AddPath(steamRoots, Environment.GetEnvironmentVariable("STEAM_PATH"));
        AddPath(steamRoots, Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            "Steam"));
        AddPath(steamRoots, Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "Steam"));
        AddPath(steamRoots, Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Steam"));

        foreach (var steamRoot in steamRoots)
        {
            foreach (var libraryRoot in EnumerateLibraryRoots(steamRoot))
            {
                var installation = FromSteamLibraryRoot(libraryRoot);
                if (installation is not null)
                {
                    return installation;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Locates The Isle from a Steam library root. This is public so the
    /// installer and diagnostics can verify a discovered Steam library without
    /// touching the Windows registry.
    /// </summary>
    public static TheIsleInstallation? FromSteamLibraryRoot(string? libraryRoot)
    {
        if (string.IsNullOrWhiteSpace(libraryRoot) || !Directory.Exists(libraryRoot))
        {
            return null;
        }

        try
        {
            var canonicalLibrary = Path.TrimEndingDirectorySeparator(Path.GetFullPath(libraryRoot));
            var manifestPath = Path.Combine(canonicalLibrary, "steamapps", AppManifestFileName);
            if (!File.Exists(manifestPath))
            {
                return null;
            }

            var manifest = File.ReadAllText(manifestPath);
            var installDirMatch = InstallDirectoryRegex().Match(manifest);
            var installDirectory = installDirMatch.Success
                ? DecodeVdfString(installDirMatch.Groups[1].Value)
                : "The Isle";
            if (!IsSafeInstallDirectory(installDirectory))
            {
                return null;
            }

            return FromGameRoot(Path.Combine(canonicalLibrary, "steamapps", "common", installDirectory));
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or System.Security.SecurityException
                or ArgumentException
                or NotSupportedException)
        {
            return null;
        }
    }

    public static TheIsleInstallation? FromGameRoot(string? gameRoot)
    {
        if (string.IsNullOrWhiteSpace(gameRoot))
        {
            return null;
        }

        try
        {
            if (!Directory.Exists(gameRoot))
            {
                return null;
            }

            var canonicalRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(gameRoot));
            var commonDirectory = Directory.GetParent(canonicalRoot);
            var steamAppsDirectory = commonDirectory?.Parent;
            var appManifestPath = steamAppsDirectory is null
                ? null
                : Path.Combine(steamAppsDirectory.FullName, AppManifestFileName);
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
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or System.Security.SecurityException
                or ArgumentException
                or NotSupportedException)
        {
            return null;
        }
    }

    private static IEnumerable<string> EnumerateLibraryRoots(string steamRoot)
    {
        var candidates = new List<string>();
        AddPath(candidates, steamRoot);

        foreach (var configPath in new[]
        {
            Path.Combine(steamRoot, "config", LibraryFoldersFileName),
            Path.Combine(steamRoot, "steamapps", LibraryFoldersFileName)
        })
        {
            try
            {
                if (!File.Exists(configPath))
                {
                    continue;
                }

                var content = File.ReadAllText(configPath);
                var matchedPath = false;
                foreach (Match match in LibraryPathRegex().Matches(content))
                {
                    matchedPath = true;
                    AddPath(candidates, DecodeVdfString(match.Groups[1].Value));
                }

                // Steam versions that predate the nested libraryfolders
                // schema stored libraries as numeric key/value pairs:
                // "1" "D:\\SteamLibrary". Keep this fallback for users who
                // have an old config file that Steam has not rewritten yet.
                if (!matchedPath)
                {
                    foreach (Match match in LegacyLibraryPathRegex().Matches(content))
                    {
                        AddPath(candidates, DecodeVdfString(match.Groups[1].Value));
                    }
                }
            }
            catch (Exception exception) when (
                exception is IOException
                    or UnauthorizedAccessException
                    or System.Security.SecurityException
                    or ArgumentException
                    or NotSupportedException)
            {
                // A stale or unreadable library file must not prevent trying
                // the other Steam roots and registry entries.
            }
        }

        return candidates;
    }

    private static void AddPath(ICollection<string> paths, string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            var canonical = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path.Trim().Trim('"')));
            if (Directory.Exists(canonical)
                && !paths.Contains(canonical, StringComparer.OrdinalIgnoreCase))
            {
                paths.Add(canonical);
            }
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException)
        {
            // Ignore malformed paths from registry/environment/config files.
        }
    }

    private static bool IsSafeInstallDirectory(string? installDirectory) =>
        !string.IsNullOrWhiteSpace(installDirectory)
        && !Path.IsPathRooted(installDirectory)
        && installDirectory is not "."
        && installDirectory is not ".."
        && !installDirectory.Contains('/')
        && !installDirectory.Contains('\\')
        && !installDirectory.Contains('\0');

    private static string DecodeVdfString(string value)
    {
        var builder = new System.Text.StringBuilder(value.Length);
        var escaped = false;
        foreach (var character in value)
        {
            if (escaped)
            {
                builder.Append(character is '\\' or '"' ? character : '\\');
                if (character is not '\\' and not '"')
                {
                    builder.Append(character);
                }

                escaped = false;
            }
            else if (character == '\\')
            {
                escaped = true;
            }
            else
            {
                builder.Append(character);
            }
        }

        if (escaped)
        {
            builder.Append('\\');
        }

        return builder.ToString();
    }

    [GeneratedRegex("\\\"buildid\\\"\\s+\\\"([0-9]+)\\\"", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BuildIdRegex();

    [GeneratedRegex("\\\"installdir\\\"\\s+\\\"((?:\\\\.|[^\\\"\\\\])*)\\\"", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex InstallDirectoryRegex();

    [GeneratedRegex("\\\"path\\\"\\s+\\\"((?:\\\\.|[^\\\"\\\\])*)\\\"", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LibraryPathRegex();

    [GeneratedRegex("\\\"[0-9]+\\\"\\s+\\\"((?:\\\\.|[^\\\"\\\\])*)\\\"", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LegacyLibraryPathRegex();
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
