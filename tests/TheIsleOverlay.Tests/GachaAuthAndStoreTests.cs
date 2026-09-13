using System.Net;
using System.Text;
using TheIsleOverlay.Gacha;

namespace TheIsleOverlay.Tests;

public sealed class GachaAuthAndStoreTests
{
    [Fact]
    public void CallbackParser_ParsesOfficialAliasesAndRejectsDuplicateFields()
    {
        var callback =
            "isle-overlay://callback?steamId=76561198000000000" +
            "&access_token=access%20token" +
            "&refresh_token=refresh%20token" +
            "&device_id=device-a&expires_in=900" +
            "&refresh_expires_at=1790000000&name=Test%20Player";

        Assert.True(GachaOverlayAuthService.TryParseCallback(
            callback,
            out var result,
            out var error));
        Assert.Null(error);
        Assert.NotNull(result);
        Assert.Equal("76561198000000000", result!.SteamId);
        Assert.Equal("access token", result.AccessToken);
        Assert.Equal("refresh token", result.RefreshToken);
        Assert.Equal("device-a", result.DeviceId);
        Assert.Equal("Test Player", result.PersonaName);
        Assert.True(result.AccessExpiresAt > DateTimeOffset.UtcNow.AddMinutes(14));

        Assert.False(GachaOverlayAuthService.TryParseCallback(
            callback + "&steamId=76561198000000001",
            out _,
            out error));
        Assert.Contains("trùng", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RefreshAsync_SendsPinnedRequestAndReturnsRotatedPair()
    {
        var handler = new RecordingHandler(
            """{"data":{"accessToken":"access-next","refreshToken":"refresh-next","deviceId":"device-b","expiresIn":600,"refreshExpiresAt":1790000000}}""");
        using var http = new HttpClient(handler);
        var original = new GachaOverlayCredentials(
            "access-current",
            steamId: "76561198000000000",
            deviceId: "device-a",
            refreshToken: "refresh-current",
            refreshExpiresAt: DateTimeOffset.UtcNow.AddDays(1));

        var rotated = await GachaOverlayAuthService.RefreshAsync(http, original);

        Assert.Equal(HttpMethod.Post, handler.Request?.Method);
        Assert.Equal(
            "https://player.isle.vn/api/overlay/auth/refresh",
            handler.Request?.RequestUri?.ToString());
        Assert.Null(handler.Request?.Headers.Authorization);
        Assert.Contains("refresh-current", handler.Body, StringComparison.Ordinal);
        Assert.Contains("device-a", handler.Body, StringComparison.Ordinal);
        Assert.Equal("access-next", rotated.AccessToken);
        Assert.Equal("refresh-next", rotated.RefreshToken);
        Assert.Equal("device-b", rotated.DeviceId);
        Assert.Equal(original.SteamId, rotated.SteamId);
    }

    [Fact]
    public async Task CredentialStore_RoundTripsEncryptedCredentialOnWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var directory = Path.Combine(
            Path.GetTempPath(),
            "IsleLiveMap-GachaTests",
            Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "credential.bin");
        try
        {
            var store = new GachaOverlayCredentialStore(path);
            var credentials = new GachaOverlayCredentials(
                "access-secret",
                steamId: "76561198000000000",
                deviceId: "device-a",
                personaName: "Test Player",
                refreshToken: "refresh-secret",
                accessExpiresAt: DateTimeOffset.UtcNow.AddMinutes(10),
                refreshExpiresAt: DateTimeOffset.UtcNow.AddDays(1));

            await store.SaveAsync(credentials);

            Assert.True(File.Exists(path));
            var raw = await File.ReadAllTextAsync(path);
            Assert.DoesNotContain("access-secret", raw, StringComparison.Ordinal);
            Assert.DoesNotContain("refresh-secret", raw, StringComparison.Ordinal);

            var loaded = await store.LoadAsync();
            Assert.NotNull(loaded);
            Assert.Equal(credentials.AccessToken, loaded!.AccessToken);
            Assert.Equal(credentials.RefreshToken, loaded.RefreshToken);
            Assert.Equal(credentials.SteamId, loaded.SteamId);
            Assert.Equal(credentials.DeviceId, loaded.DeviceId);
            Assert.Equal(credentials.PersonaName, loaded.PersonaName);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    private sealed class RecordingHandler(string responseJson) : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }
        public string Body { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Request = request;
            Body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new StringContent(responseJson, Encoding.UTF8, "application/json")
            };
        }
    }
}
