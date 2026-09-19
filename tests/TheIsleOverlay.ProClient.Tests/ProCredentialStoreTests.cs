using System.Security.Cryptography;
using System.Text.Json;
using TheIsleOverlay.ProClient;

namespace TheIsleOverlay.ProClient.Tests;

public sealed class ProCredentialStoreTests
{
    [Theory]
    [InlineData("free", "active", 60, false)]
    [InlineData("pro", "revoked", 60, false)]
    [InlineData("pro", "active", 2, false)]
    [InlineData("pro", "active", 3, true)]
    public void HasUsableOfflineLicense_RequiresActiveProEntitlementAndSafeLifetime(
        string tier,
        string status,
        int expiresInMinutes,
        bool expected)
    {
        var now = DateTimeOffset.Parse("2026-08-29T00:00:00Z");
        var session = new StoredProSession(
            "76561198000000000",
            new string('r', 64),
            now.AddDays(30),
            "header.payload.signature",
            now.AddMinutes(expiresInMinutes),
            new ProEntitlement(tier, status, null));

        Assert.Equal(expected, session.HasUsableOfflineLicense(now));
    }

    [Fact]
    public async Task Load_AcceptsLegacyEntropy()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var directory = Path.Combine(Path.GetTempPath(), $"isle-pro-legacy-credential-{Guid.NewGuid():N}");
        var path = Path.Combine(directory, "credential.bin");
        Directory.CreateDirectory(directory);
        var expected = new StoredProSession(
            "76561198000000000",
            new string('r', 64),
            DateTimeOffset.Parse("2026-09-26T00:00:00Z"),
            "header.payload.signature",
            DateTimeOffset.Parse("2026-08-29T00:00:00Z"),
            new ProEntitlement("pro", "active", null));
        byte[] legacyEntropy =
        [
            75, 76, 111, 110, 103, 68, 101, 118, 46, 73, 115, 108, 101, 76, 105, 118, 101,
            77, 97, 112, 46, 80, 114, 111, 65, 99, 99, 101, 115, 115, 46, 118, 49
        ];
        var cleartext = JsonSerializer.SerializeToUtf8Bytes(
            expected,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var protectedData = WindowsDataProtection.Protect(cleartext, legacyEntropy);
        try
        {
            await File.WriteAllBytesAsync(
                path,
                [.. "ILMP1"u8, .. protectedData],
                TestContext.Current.CancellationToken);

            Assert.Equal(expected, await new ProCredentialStore(path).LoadAsync(
                TestContext.Current.CancellationToken));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(cleartext);
            CryptographicOperations.ZeroMemory(protectedData);
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task SaveAndLoad_RoundTripsThroughWindowsDpapi()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var directory = Path.Combine(Path.GetTempPath(), $"isle-pro-credential-{Guid.NewGuid():N}");
        var path = Path.Combine(directory, "credential.bin");
        var store = new ProCredentialStore(path);
        var expected = new StoredProSession(
            "76561198000000000",
            new string('r', 64),
            DateTimeOffset.Parse("2026-09-26T00:00:00Z"),
            "header.payload.signature",
            DateTimeOffset.Parse("2026-08-29T00:00:00Z"),
            new ProEntitlement("pro", "active", null));

        try
        {
            await store.SaveAsync(expected, TestContext.Current.CancellationToken);
            var actual = await store.LoadAsync(TestContext.Current.CancellationToken);

            Assert.Equal(expected, actual);
            Assert.DoesNotContain("header.payload.signature", await File.ReadAllTextAsync(
                path,
                TestContext.Current.CancellationToken));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }
}
