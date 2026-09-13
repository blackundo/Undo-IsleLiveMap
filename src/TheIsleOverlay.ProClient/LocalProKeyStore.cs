using System.Security.Cryptography;
using System.Text;

namespace TheIsleOverlay.ProClient;

internal sealed class LocalProKeyStore(string path)
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Undo.IsleLiveMap.LocalKey.v2");
    public static bool Accepts(string? key) => !string.IsNullOrWhiteSpace(key) && key.Trim().Length <= 128;

    public async Task<string?> LoadAsync(CancellationToken cancellationToken)
    {
        var file = new FileInfo(path);
        if (!file.Exists || file.Length is <= 0 or > 4096) return null;
        var protectedData = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        byte[]? cleartext = null;
        try
        {
            cleartext = WindowsDataProtection.Unprotect(protectedData, Entropy);
            var key = Encoding.UTF8.GetString(cleartext);
            return Accepts(key) ? key : null;
        }
        catch (CryptographicException) { return null; }
        finally
        {
            CryptographicOperations.ZeroMemory(protectedData);
            if (cleartext is not null) CryptographicOperations.ZeroMemory(cleartext);
        }
    }

    public async Task ActivateAsync(string key, CancellationToken cancellationToken)
    {
        if (!Accepts(key)) throw new ArgumentException("Key must contain 1 to 128 characters.", nameof(key));
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        var cleartext = Encoding.UTF8.GetBytes(key.Trim());
        byte[]? protectedData = null;
        try
        {
            protectedData = WindowsDataProtection.Protect(cleartext, Entropy);
            await File.WriteAllBytesAsync(temporary, protectedData, cancellationToken).ConfigureAwait(false);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(cleartext);
            if (protectedData is not null) CryptographicOperations.ZeroMemory(protectedData);
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    public void Clear() => File.Delete(path);
}