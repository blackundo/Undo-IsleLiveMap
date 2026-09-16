using System.Security.Cryptography;
using System.Text.Json;

namespace TheIsleOverlay.ProClient;

public sealed class ProReleaseManager : IDisposable
{
    public const int IpcApiMajor = 2;

    private const long MaximumArtifactBytes = 256L * 1024L * 1024L;
    private const string AgentExecutableName = "IsleLiveMap.Pro.Agent.exe";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly ProApiClient _apiClient;
    private readonly ProReleaseSignatureVerifier _signatureVerifier;
    private readonly string _installationRoot;
    private readonly string _versionsRoot;
    private readonly string _currentDescriptorPath;

    public ProReleaseManager(
        ProApiClient apiClient,
        string installationRoot,
        string? publicKeyPem = null)
    {
        _apiClient = apiClient ?? throw new ArgumentNullException(nameof(apiClient));
        ArgumentException.ThrowIfNullOrWhiteSpace(installationRoot);
        _installationRoot = Path.GetFullPath(installationRoot);
        _versionsRoot = Path.Combine(_installationRoot, "versions");
        _currentDescriptorPath = Path.Combine(_installationRoot, "current.json");
        _signatureVerifier = new ProReleaseSignatureVerifier(
            publicKeyPem ?? EmbeddedProUpdatePublicKey.Load());
    }

    public async Task<ProAgentInstallation> EnsureAvailableAsync(
        string hostVersion,
        string accessToken,
        CancellationToken cancellationToken = default)
    {
        var installed = await LoadInstalledAsync(hostVersion, cancellationToken).ConfigureAwait(false);
        try
        {
            return await EnsureLatestAsync(hostVersion, accessToken, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (
            installed is not null &&
            exception is ProApiException or IOException or InvalidDataException or UnauthorizedAccessException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return installed;
        }
    }

    public async Task<ProAgentInstallation> EnsureLatestAsync(
        string hostVersion,
        string accessToken,
        CancellationToken cancellationToken = default)
    {
        if (!SemanticVersion.TryParse(hostVersion, out _))
        {
            throw new ArgumentException("The host version is invalid.", nameof(hostVersion));
        }

        var installed = await LoadInstalledAsync(hostVersion, cancellationToken).ConfigureAwait(false);
        var manifest = await _apiClient.GetManifestAsync(
                hostVersion,
                IpcApiMajor,
                accessToken,
                cancellationToken)
            .ConfigureAwait(false);
        if (manifest is null)
        {
            return installed ?? throw new ProApiException(
                "No compatible Pro Agent release is available for this app version.");
        }

        ValidateManifest(manifest, hostVersion);
        if (installed is not null &&
            string.Equals(installed.Version, manifest.Version, StringComparison.Ordinal) &&
            string.Equals(installed.ArtifactSha256, manifest.Sha256, StringComparison.Ordinal) &&
            string.Equals(installed.ArtifactSignature, manifest.Signature, StringComparison.Ordinal))
        {
            return installed;
        }

        Directory.CreateDirectory(_installationRoot);
        Directory.CreateDirectory(_versionsRoot);
        var temporaryExecutable = Path.Combine(_installationRoot, $".download-{Guid.NewGuid():N}.exe");
        var staging = Path.Combine(_versionsRoot, $".{manifest.Version}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var destination = new FileStream(
                             temporaryExecutable,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             64 * 1024,
                             FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await _apiClient.DownloadArtifactAsync(
                        new Uri(manifest.DownloadUrl, UriKind.Absolute),
                        accessToken,
                        destination,
                        cancellationToken)
                    .ConfigureAwait(false);
                await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            await VerifyArtifactAsync(temporaryExecutable, manifest, cancellationToken).ConfigureAwait(false);
            Directory.CreateDirectory(staging);
            var stagedExecutable = Path.Combine(staging, AgentExecutableName);
            File.Move(temporaryExecutable, stagedExecutable);

            var target = Path.Combine(_versionsRoot, manifest.Version);
            string? backup = null;
            if (Directory.Exists(target))
            {
                backup = Path.Combine(_versionsRoot, $".{manifest.Version}.{Guid.NewGuid():N}.old");
                Directory.Move(target, backup);
            }

            try
            {
                Directory.Move(staging, target);
                var descriptor = ReleaseDescriptor.FromManifest(manifest);
                await SaveDescriptorAsync(descriptor, cancellationToken).ConfigureAwait(false);
                if (backup is not null)
                {
                    SafeDeleteDirectory(backup);
                }
            }
            catch
            {
                if (!Directory.Exists(target) && backup is not null && Directory.Exists(backup))
                {
                    Directory.Move(backup, target);
                }

                throw;
            }

            return ToInstallation(ReleaseDescriptor.FromManifest(manifest));
        }
        finally
        {
            if (File.Exists(temporaryExecutable))
            {
                File.Delete(temporaryExecutable);
            }

            if (Directory.Exists(staging))
            {
                SafeDeleteDirectory(staging);
            }
        }
    }

    public async Task<ProAgentInstallation?> LoadInstalledAsync(
        string hostVersion,
        CancellationToken cancellationToken = default)
    {
        if (!SemanticVersion.TryParse(hostVersion, out var host))
        {
            return null;
        }

        try
        {
            var file = new FileInfo(_currentDescriptorPath);
            if (!file.Exists || file.Length is <= 0 or > 32_768)
            {
                return null;
            }

            var json = await File.ReadAllTextAsync(_currentDescriptorPath, cancellationToken)
                .ConfigureAwait(false);
            var descriptor = JsonSerializer.Deserialize<ReleaseDescriptor>(json, JsonOptions);
            if (descriptor is null ||
                descriptor.IpcApiMajor != IpcApiMajor ||
                !SemanticVersion.TryParse(descriptor.Version, out _) ||
                !SemanticVersion.TryParse(descriptor.MinHostVersion, out var minimum) ||
                !SemanticVersion.TryParse(descriptor.MaxHostVersionExclusive, out var maximum) ||
                host.CompareTo(minimum) < 0 ||
                host.CompareTo(maximum) >= 0 ||
                !IsLowerHexSha256(descriptor.ArtifactSha256))
            {
                return null;
            }

            var installation = ToInstallation(descriptor);
            return File.Exists(installation.ExecutablePath) ? installation : null;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    public void Dispose() => _signatureVerifier.Dispose();

    private void ValidateManifest(ProReleaseManifest manifest, string hostVersion)
    {
        if (manifest.IpcApiMajor != IpcApiMajor ||
            !SemanticVersion.TryParse(manifest.Version, out _) ||
            !SemanticVersion.TryParse(manifest.MinHostVersion, out var minimum) ||
            !SemanticVersion.TryParse(manifest.MaxHostVersionExclusive, out var maximum) ||
            !SemanticVersion.TryParse(hostVersion, out var host) ||
            host.CompareTo(minimum) < 0 ||
            host.CompareTo(maximum) >= 0 ||
            manifest.Size is <= 0 or > MaximumArtifactBytes ||
            !IsLowerHexSha256(manifest.Sha256) ||
            !_signatureVerifier.Verify(manifest))
        {
            throw new InvalidDataException("The Pro release manifest is invalid or not compatible.");
        }
    }

    private static async Task VerifyArtifactAsync(
        string path,
        ProReleaseManifest manifest,
        CancellationToken cancellationToken)
    {
        var file = new FileInfo(path);
        if (file.Length != manifest.Size)
        {
            throw new InvalidDataException("The Pro artifact size does not match its signed manifest.");
        }

        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        if (!CryptographicOperations.FixedTimeEquals(
                hash,
                Convert.FromHexString(manifest.Sha256)))
        {
            throw new InvalidDataException("The Pro artifact hash does not match its signed manifest.");
        }
    }

    private async Task SaveDescriptorAsync(
        ReleaseDescriptor descriptor,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_installationRoot);
        var temporary = Path.Combine(_installationRoot, $".current-{Guid.NewGuid():N}.tmp");
        try
        {
            var json = JsonSerializer.Serialize(descriptor, JsonOptions);
            await File.WriteAllTextAsync(temporary, json, cancellationToken).ConfigureAwait(false);
            File.Move(temporary, _currentDescriptorPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private ProAgentInstallation ToInstallation(ReleaseDescriptor descriptor) => new(
        descriptor.Version,
        descriptor.IpcApiMajor,
        descriptor.MinHostVersion,
        descriptor.MaxHostVersionExclusive,
        Path.Combine(_versionsRoot, descriptor.Version, AgentExecutableName),
        descriptor.ArtifactSha256,
        descriptor.ArtifactSignature);

    private void SafeDeleteDirectory(string path)
    {
        var root = Path.GetFullPath(_versionsRoot) + Path.DirectorySeparatorChar;
        var target = Path.GetFullPath(path);
        if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Refusing to delete a directory outside the Pro versions root.");
        }

        Directory.Delete(target, recursive: true);
    }

    private static bool IsLowerHexSha256(string value) =>
        value is { Length: 64 } &&
        value.All(character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private sealed record ReleaseDescriptor(
        string Version,
        int IpcApiMajor,
        string MinHostVersion,
        string MaxHostVersionExclusive,
        string ArtifactSha256,
        string ArtifactSignature)
    {
        public static ReleaseDescriptor FromManifest(ProReleaseManifest manifest) => new(
            manifest.Version,
            manifest.IpcApiMajor,
            manifest.MinHostVersion,
            manifest.MaxHostVersionExclusive,
            manifest.Sha256,
            manifest.Signature);
    }
}
