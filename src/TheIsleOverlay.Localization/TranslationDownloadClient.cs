using System.Net.Http.Json;
using System.Security.Cryptography;

namespace TheIsleOverlay.Localization;

public sealed class TranslationDownloadClient
{
    private readonly HttpClient _httpClient;
    private readonly LocalizationOptions _options;
    private readonly TranslationManifestValidator _validator;

    public TranslationDownloadClient(HttpClient httpClient, LocalizationOptions? options = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options ?? new LocalizationOptions();
        _validator = new TranslationManifestValidator(_options);
    }

    public async Task<TranslationManifest> GetManifestAsync(
        string? gameBuildId,
        CancellationToken cancellationToken = default)
    {
        ValidatePinnedUri(_options.ManifestUri, requireArtifactPrefix: false);
        using var response = await _httpClient.GetAsync(
            _options.ManifestUri,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        response.EnsureSuccessStatusCode();
        var manifest = await response.Content.ReadFromJsonAsync<TranslationManifest>(
            cancellationToken: cancellationToken)
            ?? throw new InvalidDataException("Máy chủ trả về manifest Việt hóa rỗng.");
        _validator.Validate(manifest, gameBuildId);
        return manifest;
    }

    public async Task<byte[]> DownloadArchiveAsync(
        TranslationManifest manifest,
        CancellationToken cancellationToken = default)
    {
        _validator.Validate(manifest);
        var artifactUri = new Uri(manifest.Url, UriKind.Absolute);
        ValidatePinnedUri(artifactUri, requireArtifactPrefix: true);
        using var response = await _httpClient.GetAsync(
            artifactUri,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is > 0 and var declaredLength
            && declaredLength > _options.MaximumArchiveBytes)
        {
            throw new InvalidDataException("Gói Việt hóa vượt giới hạn kích thước.");
        }

        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var output = new MemoryStream();
        var buffer = new byte[64 * 1024];
        while (true)
        {
            var read = await input.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                break;
            }

            if (output.Length + read > _options.MaximumArchiveBytes)
            {
                throw new InvalidDataException("Gói Việt hóa vượt giới hạn kích thước.");
            }

            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }

        var archive = output.ToArray();
        var actualHash = Convert.ToHexString(SHA256.HashData(archive));
        if (!actualHash.Equals(manifest.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("SHA-256 của gói Việt hóa không khớp manifest.");
        }

        return archive;
    }

    private void ValidatePinnedUri(Uri uri, bool requireArtifactPrefix)
    {
        if (!uri.IsAbsoluteUri
            || uri.Scheme != Uri.UriSchemeHttps
            || !uri.Host.Equals(_options.AllowedHost, StringComparison.OrdinalIgnoreCase)
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment)
            || requireArtifactPrefix
            && !uri.AbsolutePath.StartsWith(_options.ArtifactPathPrefix, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Địa chỉ tải Việt hóa nằm ngoài HTTPS allowlist.");
        }
    }
}
