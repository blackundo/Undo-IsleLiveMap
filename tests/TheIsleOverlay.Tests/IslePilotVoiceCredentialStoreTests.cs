using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TheIsleOverlay.IslePilot;

namespace TheIsleOverlay.Tests;

public sealed class IslePilotVoiceCredentialStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "IsleLiveMap.Voice.Tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task SaveAndLoad_RoundTripsWithoutWritingIdentityInPlaintext()
    {
        var path = Path.Combine(_directory, "voice.credential");
        var store = new IslePilotVoiceCredentialStore(path);
        var expected = new IslePilotVoiceAuthResult(
            "76561198000000000",
            "voice-token-must-remain-private");

        await store.SaveAsync(expected);
        var bytes = await File.ReadAllBytesAsync(path);
        var actual = await store.LoadAsync();

        Assert.Equal(expected, actual);
        Assert.Equal(-1, bytes.AsSpan().IndexOf(Encoding.UTF8.GetBytes(expected.SteamId64)));
        Assert.Equal(-1, bytes.AsSpan().IndexOf(Encoding.UTF8.GetBytes(expected.AccessToken)));
    }

    [Fact]
    public async Task Load_ReturnsNullForCorruptOrWrongPurposeData()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "voice.credential");
        await File.WriteAllBytesAsync(path, "ILM1not-a-voice-credential"u8.ToArray());

        Assert.Null(await new IslePilotVoiceCredentialStore(path).LoadAsync());
    }

    [Fact]
    public async Task Load_AcceptsLegacyEntropy()
    {
        var path = Path.Combine(_directory, "voice.credential");
        Directory.CreateDirectory(_directory);
        var expected = new IslePilotVoiceAuthResult(
            "76561198000000000",
            "voice-token-must-remain-private");
        byte[] legacyEntropy =
        [
            75, 76, 111, 110, 103, 68, 101, 118, 46, 73, 115, 108, 101, 76, 105, 118, 101,
            77, 97, 112, 46, 73, 115, 108, 101, 80, 105, 108, 111, 116, 86, 111, 105, 99,
            101, 46, 118, 49
        ];
        var cleartext = JsonSerializer.SerializeToUtf8Bytes(new
        {
            expected.SteamId64,
            expected.AccessToken
        });
        var protectedData = WindowsDataProtection.Protect(cleartext, legacyEntropy);
        try
        {
            await File.WriteAllBytesAsync(path, [.. "ILV1"u8, .. protectedData]);

            Assert.Equal(expected, await new IslePilotVoiceCredentialStore(path).LoadAsync());
        }
        finally
        {
            CryptographicOperations.ZeroMemory(cleartext);
            CryptographicOperations.ZeroMemory(protectedData);
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
