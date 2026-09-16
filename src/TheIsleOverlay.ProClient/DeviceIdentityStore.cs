using System.Security.Cryptography;
using System.Text;

namespace TheIsleOverlay.ProClient;

public sealed class DeviceIdentity : IDisposable
{
    private readonly ECDsa _key;
    internal DeviceIdentity(ECDsa key)
    {
        _key = key;
        PublicKeyPem = key.ExportSubjectPublicKeyInfoPem();
        DeviceId = Convert.ToHexString(SHA256.HashData(key.ExportSubjectPublicKeyInfo())).ToLowerInvariant();
    }

    public string DeviceId { get; }
    public string PublicKeyPem { get; }
    public string Sign(string value) => Base64Url.Encode(_key.SignData(
        Encoding.UTF8.GetBytes(value), HashAlgorithmName.SHA256,
        DSASignatureFormat.Rfc3279DerSequence));
    internal byte[] ExportPrivateKey() => _key.ExportPkcs8PrivateKey();
    public void Dispose() => _key.Dispose();
}

internal sealed class DeviceIdentityStore(string path)
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Undo.IsleLiveMap.DeviceIdentity.v1");

    public async Task<DeviceIdentity> LoadOrCreateAsync(CancellationToken cancellationToken)
    {
        if (File.Exists(path))
        {
            var protectedBytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
            byte[]? privateBytes = null;
            try
            {
                privateBytes = WindowsDataProtection.Unprotect(protectedBytes, Entropy);
                var key = ECDsa.Create(); key.ImportPkcs8PrivateKey(privateBytes, out _);
                return new DeviceIdentity(key);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(protectedBytes);
                if (privateBytes is not null) CryptographicOperations.ZeroMemory(privateBytes);
            }
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var created = new DeviceIdentity(ECDsa.Create(ECCurve.NamedCurves.nistP256));
        var clear = created.ExportPrivateKey(); byte[]? encrypted = null;
        try
        {
            encrypted = WindowsDataProtection.Protect(clear, Entropy);
            await File.WriteAllBytesAsync(path, encrypted, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(clear);
            if (encrypted is not null) CryptographicOperations.ZeroMemory(encrypted);
        }
        var persisted = ECDsa.Create(); persisted.ImportPkcs8PrivateKey(created.ExportPrivateKey(), out _);
        return new DeviceIdentity(persisted);
    }
}

internal static class Base64Url
{
    public static string Encode(byte[] value) => Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
