using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TheIsleOverlay.IslePilot;

namespace TheIsleOverlay.Tests;

public sealed class IslePilotCredentialStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "IsleLiveMap.Tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task SaveAndLoad_RoundTripsForTheCurrentWindowsUser()
    {
        var path = Path.Combine(_directory, "islepilot.credential");
        var store = new IslePilotCredentialStore(path);
        var expected = new IslePilotOverlayAuthResult(
            "76561198000000000",
            "header.payload.signature",
            "signed-player-cookie");

        await store.SaveAsync(expected);
        var actual = await store.LoadAsync();

        Assert.Equal(expected, actual);
    }

    [Fact]
    public async Task Save_DoesNotWriteTheTokenOrSteamIdInPlaintext()
    {
        var path = Path.Combine(_directory, "islepilot.credential");
        var store = new IslePilotCredentialStore(path);
        const string steamId = "76561198000000000";
        const string token = "plain-text-token-must-not-leak";
        const string playerCookie = "plain-text-cookie-must-not-leak";

        await store.SaveAsync(new IslePilotOverlayAuthResult(steamId, token, playerCookie));
        var stored = await File.ReadAllBytesAsync(path);

        Assert.Equal(-1, stored.AsSpan().IndexOf(Encoding.UTF8.GetBytes(token)));
        Assert.Equal(-1, stored.AsSpan().IndexOf(Encoding.Unicode.GetBytes(token)));
        Assert.Equal(-1, stored.AsSpan().IndexOf(Encoding.UTF8.GetBytes(steamId)));
        Assert.Equal(-1, stored.AsSpan().IndexOf(Encoding.UTF8.GetBytes(playerCookie)));
    }

    [Fact]
    public async Task Clear_RemovesTheSavedCredential()
    {
        var path = Path.Combine(_directory, "islepilot.credential");
        var store = new IslePilotCredentialStore(path);
        await store.SaveAsync(new IslePilotOverlayAuthResult(
            "76561198000000000",
            "secret"));

        store.Clear();

        Assert.False(File.Exists(path));
        Assert.Null(await store.LoadAsync());
    }

    [Fact]
    public async Task Load_ReturnsNullForCorruptData()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "islepilot.credential");
        await File.WriteAllBytesAsync(path, [0x49, 0x4C, 0x4D, 0x31, 0x01]);
        var store = new IslePilotCredentialStore(path);

        Assert.Null(await store.LoadAsync());
    }

    [Fact]
    public async Task Load_AcceptsLegacyEntropy()
    {
        var path = Path.Combine(_directory, "islepilot.credential");
        Directory.CreateDirectory(_directory);
        var expected = new IslePilotOverlayAuthResult(
            "76561198000000000",
            "header.payload.signature",
            "signed-player-cookie");
        byte[] legacyEntropy =
        [
            75, 76, 111, 110, 103, 68, 101, 118, 46, 73, 115, 108, 101, 76, 105, 118, 101,
            77, 97, 112, 46, 73, 115, 108, 101, 80, 105, 108, 111, 116, 79, 118, 101, 114,
            108, 97, 121, 46, 118, 49
        ];
        var cleartext = JsonSerializer.SerializeToUtf8Bytes(new
        {
            SteamId = expected.SteamId,
            OverlayToken = expected.OverlayToken,
            PlayerCookie = expected.PlayerCookie
        });
        var protectedData = WindowsDataProtection.Protect(cleartext, legacyEntropy);
        try
        {
            await File.WriteAllBytesAsync(path, [.. "ILM1"u8, .. protectedData]);

            Assert.Equal(expected, await new IslePilotCredentialStore(path).LoadAsync());
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
