using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TheIsleOverlay.ProClient;

internal sealed class KeyLeaseStore(string path)
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Undo.IsleLiveMap.KeyLease.v1");
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<StoredKeyLease?> LoadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(path) || new FileInfo(path).Length > 32768) return null;
        var encrypted = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false); byte[]? clear = null;
        try
        {
            clear = WindowsDataProtection.Unprotect(encrypted, Entropy);
            return JsonSerializer.Deserialize<StoredKeyLease>(clear, JsonOptions);
        }
        catch (Exception e) when (e is CryptographicException or JsonException) { return null; }
        finally { CryptographicOperations.ZeroMemory(encrypted); if (clear is not null) CryptographicOperations.ZeroMemory(clear); }
    }

    public async Task SaveAsync(StoredKeyLease lease, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var clear=JsonSerializer.SerializeToUtf8Bytes(lease,JsonOptions); byte[]? encrypted=null;
        try { encrypted=WindowsDataProtection.Protect(clear,Entropy); await File.WriteAllBytesAsync(path,encrypted,cancellationToken).ConfigureAwait(false); }
        finally { CryptographicOperations.ZeroMemory(clear); if(encrypted is not null)CryptographicOperations.ZeroMemory(encrypted); }
    }
    public void Clear() { if (File.Exists(path)) File.Delete(path); }
}
