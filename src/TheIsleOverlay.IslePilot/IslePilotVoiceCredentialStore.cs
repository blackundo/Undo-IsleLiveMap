using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TheIsleOverlay.IslePilot;

public sealed class IslePilotVoiceCredentialStore
{
    private const int MaximumCredentialBytes = 1024 * 1024;
    private static readonly byte[] FileHeader = "ILV1"u8.ToArray();
    private static readonly byte[] OptionalEntropy = Encoding.UTF8.GetBytes(
        "Undo-Isle.IsleLiveMap.IslePilotVoice.v1");
    private static readonly byte[] LegacyOptionalEntropy =
    [
        75, 76, 111, 110, 103, 68, 101, 118, 46, 73, 115, 108, 101, 76, 105, 118, 101,
        77, 97, 112, 46, 73, 115, 108, 101, 80, 105, 108, 111, 116, 86, 111, 105, 99,
        101, 46, 118, 49
    ];

    private readonly string _credentialPath;

    public IslePilotVoiceCredentialStore(string credentialPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(credentialPath);
        _credentialPath = Path.GetFullPath(credentialPath);
    }

    public async Task SaveAsync(
        IslePilotVoiceAuthResult credentials,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        if (!IslePilotVoiceAuthService.IsValidCredentials(
                credentials.SteamId64,
                credentials.AccessToken))
        {
            throw new ArgumentException("The IsleVOIP credentials are invalid.", nameof(credentials));
        }

        var cleartext = JsonSerializer.SerializeToUtf8Bytes(new StoredCredential(
            credentials.SteamId64,
            credentials.AccessToken));
        byte[]? protectedData = null;
        try
        {
            protectedData = WindowsDataProtection.Protect(cleartext, OptionalEntropy);
            var fileData = new byte[FileHeader.Length + protectedData.Length];
            FileHeader.CopyTo(fileData, 0);
            protectedData.CopyTo(fileData, FileHeader.Length);
            try
            {
                var directory = Path.GetDirectoryName(_credentialPath)
                    ?? throw new InvalidOperationException("The credential path has no parent directory.");
                Directory.CreateDirectory(directory);
                var temporaryPath = Path.Combine(
                    directory,
                    $".{Path.GetFileName(_credentialPath)}.{Guid.NewGuid():N}.tmp");
                try
                {
                    await File.WriteAllBytesAsync(temporaryPath, fileData, cancellationToken)
                        .ConfigureAwait(false);
                    File.Move(temporaryPath, _credentialPath, overwrite: true);
                }
                finally
                {
                    if (File.Exists(temporaryPath))
                    {
                        File.Delete(temporaryPath);
                    }
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(fileData);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(cleartext);
            if (protectedData is not null)
            {
                CryptographicOperations.ZeroMemory(protectedData);
            }
        }
    }

    public async Task<IslePilotVoiceAuthResult?> LoadAsync(
        CancellationToken cancellationToken = default)
    {
        byte[] fileData;
        try
        {
            var file = new FileInfo(_credentialPath);
            if (!file.Exists
                || file.Length <= FileHeader.Length
                || file.Length > MaximumCredentialBytes)
            {
                return null;
            }

            fileData = await File.ReadAllBytesAsync(_credentialPath, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is FileNotFoundException
                                           or DirectoryNotFoundException)
        {
            return null;
        }

        byte[]? cleartext = null;
        try
        {
            if (!fileData.AsSpan(0, FileHeader.Length).SequenceEqual(FileHeader))
            {
                return null;
            }

            cleartext = UnprotectCredential(fileData.AsSpan(FileHeader.Length));
            var stored = JsonSerializer.Deserialize<StoredCredential>(cleartext);
            return stored is not null
                   && IslePilotVoiceAuthService.IsValidCredentials(
                       stored.SteamId64,
                       stored.AccessToken)
                ? new IslePilotVoiceAuthResult(stored.SteamId64, stored.AccessToken)
                : null;
        }
        catch (Exception exception) when (exception is CryptographicException or JsonException)
        {
            return null;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(fileData);
            if (cleartext is not null)
            {
                CryptographicOperations.ZeroMemory(cleartext);
            }
        }
    }

    public void Clear()
    {
        if (File.Exists(_credentialPath))
        {
            File.Delete(_credentialPath);
        }
    }

    private static byte[] UnprotectCredential(ReadOnlySpan<byte> protectedData)
    {
        try
        {
            return WindowsDataProtection.Unprotect(protectedData, OptionalEntropy);
        }
        catch (CryptographicException)
        {
            return WindowsDataProtection.Unprotect(protectedData, LegacyOptionalEntropy);
        }
    }

    private sealed record StoredCredential(string SteamId64, string AccessToken);
}
