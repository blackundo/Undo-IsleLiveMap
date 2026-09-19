using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TheIsleOverlay.Gacha;

/// <summary>
/// Stores the optional Gacha overlay session for the current Windows user.
/// Tokens are protected with Windows DPAPI and are never written in plain
/// text.  The host still owns when to load/clear this store; the adapter does
/// not inspect the standalone Gacha process or its profile.
/// </summary>
public sealed class GachaOverlayCredentialStore
{
    private const int MaximumCredentialBytes = 1024 * 1024;
    private static readonly byte[] FileHeader = "ILG1"u8.ToArray();
    private static readonly byte[] OptionalEntropy = Encoding.UTF8.GetBytes(
        "Undo-Isle.IsleLiveMap.GachaOverlay.v1");
    private static readonly byte[] LegacyOptionalEntropy =
    [
        75, 76, 111, 110, 103, 68, 101, 118, 46, 73, 115, 108, 101, 76, 105, 118, 101,
        77, 97, 112, 46, 71, 97, 99, 104, 97, 79, 118, 101, 114, 108, 97, 121, 46, 118, 49
    ];

    private readonly string _credentialPath;

    public GachaOverlayCredentialStore(string credentialPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(credentialPath);
        _credentialPath = Path.GetFullPath(credentialPath);
    }

    public async Task SaveAsync(
        GachaOverlayCredentials credentials,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        if (!GachaOverlayAuthService.IsSteamId(credentials.SteamId)
            || !GachaOverlayAuthService.IsToken(credentials.AccessToken)
            || !GachaOverlayAuthService.IsToken(credentials.RefreshToken)
            || !GachaOverlayAuthService.IsOptionalToken(credentials.DeviceId))
        {
            throw new ArgumentException(
                "The Gacha overlay credentials are invalid.",
                nameof(credentials));
        }

        var cleartext = JsonSerializer.SerializeToUtf8Bytes(new StoredCredential(
            credentials.SteamId!,
            credentials.AccessToken,
            credentials.RefreshToken!,
            credentials.DeviceId!,
            credentials.AccessExpiresAt,
            credentials.RefreshExpiresAt,
            credentials.PersonaName));
        byte[]? protectedData = null;
        try
        {
            protectedData = WindowsGachaDataProtection.Protect(cleartext, OptionalEntropy);
            var fileData = new byte[FileHeader.Length + protectedData.Length];
            FileHeader.CopyTo(fileData, 0);
            protectedData.CopyTo(fileData, FileHeader.Length);

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

    public async Task<GachaOverlayCredentials?> LoadAsync(
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
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }

        byte[]? cleartext = null;
        try
        {
            if (fileData.Length <= FileHeader.Length
                || !fileData.AsSpan(0, FileHeader.Length).SequenceEqual(FileHeader))
            {
                return null;
            }

            cleartext = UnprotectCredential(fileData.AsSpan(FileHeader.Length));
            var stored = JsonSerializer.Deserialize<StoredCredential>(cleartext);
            if (stored is null
                || !GachaOverlayAuthService.IsSteamId(stored.SteamId)
                || !GachaOverlayAuthService.IsToken(stored.AccessToken)
                || !GachaOverlayAuthService.IsToken(stored.RefreshToken)
                || !GachaOverlayAuthService.IsOptionalToken(stored.DeviceId))
            {
                return null;
            }

            return new GachaOverlayCredentials(
                stored.AccessToken,
                stored.SteamId,
                stored.DeviceId,
                stored.PersonaName,
                stored.RefreshToken,
                stored.AccessExpiresAt,
                stored.RefreshExpiresAt);
        }
        catch (CryptographicException)
        {
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
        catch (ArgumentException)
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
        try
        {
            File.Delete(_credentialPath);
        }
        catch (FileNotFoundException)
        {
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    private static byte[] UnprotectCredential(ReadOnlySpan<byte> protectedData)
    {
        try
        {
            return WindowsGachaDataProtection.Unprotect(protectedData, OptionalEntropy);
        }
        catch (CryptographicException)
        {
            return WindowsGachaDataProtection.Unprotect(protectedData, LegacyOptionalEntropy);
        }
    }

    private sealed record StoredCredential(
        string SteamId,
        string AccessToken,
        string RefreshToken,
        string DeviceId,
        DateTimeOffset? AccessExpiresAt,
        DateTimeOffset? RefreshExpiresAt,
        string? PersonaName);
}

internal static class WindowsGachaDataProtection
{
    private const uint CryptProtectUiForbidden = 0x1;

    public static byte[] Protect(
        ReadOnlySpan<byte> cleartext,
        ReadOnlySpan<byte> optionalEntropy) =>
        Transform(cleartext, optionalEntropy, protect: true);

    public static byte[] Unprotect(
        ReadOnlySpan<byte> protectedData,
        ReadOnlySpan<byte> optionalEntropy) =>
        Transform(protectedData, optionalEntropy, protect: false);

    private static byte[] Transform(
        ReadOnlySpan<byte> input,
        ReadOnlySpan<byte> optionalEntropy,
        bool protect)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Windows DPAPI is required for Gacha credentials.");
        }

        var inputBytes = input.ToArray();
        var entropyBytes = optionalEntropy.ToArray();
        var inputBlob = AllocateBlob(inputBytes);
        var entropyBlob = AllocateBlob(entropyBytes);
        DataBlob outputBlob = default;
        IntPtr description = IntPtr.Zero;
        try
        {
            var succeeded = protect
                ? CryptProtectData(
                    ref inputBlob,
                    null,
                    ref entropyBlob,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    CryptProtectUiForbidden,
                    out outputBlob)
                : CryptUnprotectData(
                    ref inputBlob,
                    out description,
                    ref entropyBlob,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    CryptProtectUiForbidden,
                    out outputBlob);
            if (!succeeded)
            {
                var error = Marshal.GetLastWin32Error();
                throw new CryptographicException(new Win32Exception(error).Message);
            }

            if (outputBlob.Data == IntPtr.Zero || outputBlob.Length <= 0)
            {
                throw new CryptographicException("Windows DPAPI returned an empty result.");
            }

            var result = new byte[outputBlob.Length];
            Marshal.Copy(outputBlob.Data, result, 0, result.Length);
            return result;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(inputBytes);
            CryptographicOperations.ZeroMemory(entropyBytes);
            ZeroAndFreeHGlobal(ref inputBlob);
            ZeroAndFreeHGlobal(ref entropyBlob);
            ZeroAndLocalFree(ref outputBlob);
            if (description != IntPtr.Zero)
            {
                LocalFree(description);
            }
        }
    }

    private static DataBlob AllocateBlob(byte[] data)
    {
        var blob = new DataBlob
        {
            Length = data.Length,
            Data = Marshal.AllocHGlobal(data.Length)
        };
        Marshal.Copy(data, 0, blob.Data, data.Length);
        return blob;
    }

    private static void ZeroAndFreeHGlobal(ref DataBlob blob)
    {
        if (blob.Data == IntPtr.Zero)
        {
            return;
        }

        var zeros = new byte[blob.Length];
        Marshal.Copy(zeros, 0, blob.Data, blob.Length);
        Marshal.FreeHGlobal(blob.Data);
        blob = default;
    }

    private static void ZeroAndLocalFree(ref DataBlob blob)
    {
        if (blob.Data == IntPtr.Zero)
        {
            return;
        }

        var zeros = new byte[blob.Length];
        Marshal.Copy(zeros, 0, blob.Data, blob.Length);
        LocalFree(blob.Data);
        blob = default;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int Length;
        public IntPtr Data;
    }

    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(
        ref DataBlob dataIn,
        string? dataDescription,
        ref DataBlob optionalEntropy,
        IntPtr reserved,
        IntPtr promptStructure,
        uint flags,
        out DataBlob dataOut);

    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(
        ref DataBlob dataIn,
        out IntPtr dataDescription,
        ref DataBlob optionalEntropy,
        IntPtr reserved,
        IntPtr promptStructure,
        uint flags,
        out DataBlob dataOut);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}
