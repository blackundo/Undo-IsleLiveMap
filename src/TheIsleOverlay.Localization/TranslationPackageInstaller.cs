using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace TheIsleOverlay.Localization;

public sealed class TranslationPackageInstaller
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly LocalizationOptions _options;
    private readonly TranslationManifestValidator _validator;

    public TranslationPackageInstaller(LocalizationOptions? options = null)
    {
        _options = options ?? new LocalizationOptions();
        _validator = new TranslationManifestValidator(_options);
    }

    public async Task<TranslationInstallState> InstallAsync(
        TranslationManifest manifest,
        byte[] archiveBytes,
        string gameRoot,
        string stateRoot,
        string gameBuildId,
        GameLocale activeLocale,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(archiveBytes);
        _validator.Validate(manifest, gameBuildId);
        if (archiveBytes.LongLength > _options.MaximumArchiveBytes)
        {
            throw new InvalidDataException("Gói Việt hóa vượt giới hạn kích thước.");
        }

        var archiveHash = Convert.ToHexString(SHA256.HashData(archiveBytes));
        if (!archiveHash.Equals(manifest.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("SHA-256 của gói Việt hóa không khớp.");
        }

        var canonicalGameRoot = EnsureDirectoryRoot(gameRoot);
        var canonicalStateRoot = EnsureDirectoryRoot(stateRoot);
        Directory.CreateDirectory(canonicalStateRoot);
        var operationId = $"{DateTimeOffset.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid():N}";
        var stagingRoot = Path.Combine(canonicalStateRoot, "staging", operationId);
        var backupRoot = Path.Combine(canonicalStateRoot, "backups", operationId);
        Directory.CreateDirectory(stagingRoot);
        Directory.CreateDirectory(backupRoot);

        var prepared = new List<PreparedFile>();
        try
        {
            PrepareArchive(manifest, archiveBytes, stagingRoot, prepared);
            var backups = new List<BackupFile>();
            try
            {
                foreach (var file in prepared)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var target = ResolveInsideRoot(canonicalGameRoot, file.RelativePath);
                    RejectReparsePointParents(canonicalGameRoot, target);
                    var backup = ResolveInsideRoot(backupRoot, file.RelativePath);
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
                    var existed = File.Exists(target);
                    if (existed)
                    {
                        File.Copy(target, backup, overwrite: false);
                    }

                    backups.Add(new BackupFile(target, backup, existed));
                    File.Copy(file.StagedPath, target, overwrite: true);
                }

                var state = new TranslationInstallState
                {
                    ResourceVersion = manifest.Version,
                    ArchiveSha256 = archiveHash,
                    GameBuildId = gameBuildId,
                    InstalledAt = DateTimeOffset.UtcNow,
                    Files = prepared.Select(file => file.RelativePath).ToArray(),
                    BackupDirectory = backupRoot,
                    ActiveLocale = activeLocale == GameLocale.Vietnamese ? "vi" : "en"
                };
                var statePath = Path.Combine(canonicalStateRoot, "translation-state.json");
                var stagedStatePath = statePath + ".new";
                await File.WriteAllTextAsync(
                    stagedStatePath,
                    JsonSerializer.Serialize(state, JsonOptions),
                    cancellationToken);
                File.Move(stagedStatePath, statePath, overwrite: true);
                return state;
            }
            catch
            {
                RollBack(backups);
                throw;
            }
        }
        finally
        {
            if (Directory.Exists(stagingRoot))
            {
                Directory.Delete(stagingRoot, recursive: true);
            }
        }
    }

    private void PrepareArchive(
        TranslationManifest manifest,
        byte[] archiveBytes,
        string stagingRoot,
        ICollection<PreparedFile> prepared)
    {
        using var stream = new MemoryStream(archiveBytes, writable: false);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
        var entries = archive.Entries.Where(entry => !string.IsNullOrEmpty(entry.Name)).ToArray();
        if (entries.Length != TranslationManifestValidator.RequiredFileCount)
        {
            throw new InvalidDataException("ZIP không chứa đúng 10 resource.");
        }

        var manifestFiles = manifest.Files.ToDictionary(
            file => TranslationManifestValidator.NormalizeRelativePath(file.Source),
            StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            var source = TranslationManifestValidator.NormalizeRelativePath(entry.FullName);
            if (!seen.Add(source) || !manifestFiles.TryGetValue(source, out var file))
            {
                throw new InvalidDataException("ZIP chứa file lạ hoặc trùng lặp.");
            }

            if (entry.Length != file.Size || entry.Length > _options.MaximumFileBytes)
            {
                throw new InvalidDataException($"Kích thước {source} không khớp manifest.");
            }

            var relativeTarget = TranslationManifestValidator.NormalizeRelativePath(file.Path);
            var stagedPath = ResolveInsideRoot(stagingRoot, relativeTarget);
            Directory.CreateDirectory(Path.GetDirectoryName(stagedPath)!);
            using var input = entry.Open();
            using var output = new FileStream(stagedPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[32 * 1024];
            long written = 0;
            while (true)
            {
                var read = input.Read(buffer, 0, buffer.Length);
                if (read == 0)
                {
                    break;
                }

                written += read;
                if (written > file.Size || written > _options.MaximumFileBytes)
                {
                    throw new InvalidDataException($"Resource {source} vượt kích thước cho phép.");
                }

                hasher.AppendData(buffer, 0, read);
                output.Write(buffer, 0, read);
            }

            var actualHash = Convert.ToHexString(hasher.GetHashAndReset());
            if (written != file.Size || !actualHash.Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"Resource {source} không vượt qua kiểm tra SHA-256.");
            }

            prepared.Add(new PreparedFile(relativeTarget, stagedPath));
        }

        if (seen.Count != manifestFiles.Count)
        {
            throw new InvalidDataException("ZIP thiếu resource đã khai báo.");
        }
    }

    private static string EnsureDirectoryRoot(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }

    private static string ResolveInsideRoot(string root, string relativePath)
    {
        var normalized = TranslationManifestValidator.NormalizeRelativePath(relativePath);
        var candidate = Path.GetFullPath(Path.Combine(root, normalized.Replace('/', Path.DirectorySeparatorChar)));
        var prefix = root + Path.DirectorySeparatorChar;
        if (!candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Resource thoát khỏi thư mục cho phép.");
        }

        return candidate;
    }

    private static void RejectReparsePointParents(string root, string target)
    {
        var current = Path.GetDirectoryName(target);
        while (current is not null && current.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            if (Directory.Exists(current)
                && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException("Resource đi qua symlink/reparse point không được phép.");
            }

            if (current.Equals(root, StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            current = Path.GetDirectoryName(current);
        }
    }

    private static void RollBack(IEnumerable<BackupFile> backups)
    {
        foreach (var backup in backups.Reverse())
        {
            try
            {
                if (backup.Existed)
                {
                    File.Copy(backup.BackupPath, backup.TargetPath, overwrite: true);
                }
                else if (File.Exists(backup.TargetPath))
                {
                    File.Delete(backup.TargetPath);
                }
            }
            catch
            {
                // Continue reversing every already-applied file. The original
                // exception remains the primary failure surfaced to the UI.
            }
        }
    }

    private sealed record PreparedFile(string RelativePath, string StagedPath);

    private sealed record BackupFile(string TargetPath, string BackupPath, bool Existed);
}
