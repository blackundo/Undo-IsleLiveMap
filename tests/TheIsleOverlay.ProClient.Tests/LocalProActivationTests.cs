using System.Net;
using System.Net.Sockets;
using System.Text;
using TheIsleOverlay.ProClient;

namespace TheIsleOverlay.ProClient.Tests;

public sealed class LocalProActivationTests
{
    [Fact]
    public void ProTransport_PrefersIpv4BeforeIpv6()
    {
        var ordered = ProHttpTransport.InConnectionOrder([
            IPAddress.Parse("2001:db8::1"),
            IPAddress.Parse("192.0.2.20"),
            IPAddress.Parse("192.0.2.10")
        ]).ToArray();

        Assert.All(ordered.Take(2), address => Assert.Equal(AddressFamily.InterNetwork, address.AddressFamily));
        Assert.Equal(AddressFamily.InterNetworkV6, ordered[2].AddressFamily);
    }

    [Fact]
    public void ProductionOptions_UseVersionedProDirectoryInLocalAppData()
    {
        var options = new ProClientOptions();
        var expectedRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Undo-Isle",
            "IsleLiveMap",
            "Pro");

        Assert.Equal(expectedRoot, options.InstallationRoot);
        Assert.Null(options.LocalAgentPath);
    }

    [Theory]
    [InlineData("example-key", true)]
    [InlineData("  example-key  ", true)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData(null, false)]
    public void KeyInput_OnlyChecksFormat(string? key, bool expected) =>
        Assert.Equal(expected, LocalProKeyStore.Accepts(key));

    [Fact]
    public async Task MissingAgent_DoesNotGrantProOrSaveKey()
    {
        var root = Path.Combine(Path.GetTempPath(), "isle-key-" + Guid.NewGuid().ToString("N"));
        var options = new ProClientOptions
        {
            CredentialPath = Path.Combine(root, "credential"),
            InstallationRoot = Path.Combine(root, "Pro"),
            LocalAgentPath = Path.Combine(root, "missing.exe")
        };
        using var http = new HttpClient(new NoNetworkHandler());
        using var service = new ProAccessService(options, http);
        var ct = TestContext.Current.CancellationToken;
        Assert.False((await service.InitializeAsync("1.5.2", ct)).IsPro);
        await Assert.ThrowsAsync<ArgumentException>(() => service.ActivateKeyAsync("", "1.5.2", ct));
        await Assert.ThrowsAsync<ProApiException>(() => service.ActivateKeyAsync("example-key", "1.5.2", ct));
        Assert.False(service.Current.IsPro);
        Assert.Null(service.CreateRemotePlayerSource());
        Assert.False(File.Exists(options.CredentialPath + ".key-lease-v1"));
    }

    [Fact]
    public async Task SavedKey_IsEncrypted_Restored_AndCleared()
    {
        var root = Path.Combine(Path.GetTempPath(), "isle-key-store-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(root, "activation");
        var store = new LocalProKeyStore(path);
        var ct = TestContext.Current.CancellationToken;
        try
        {
            Assert.Null(await store.LoadAsync(ct));
            await store.ActivateAsync(" example-key ", ct);
            Assert.Equal("example-key", await store.LoadAsync(ct));
            Assert.DoesNotContain("example-key", Encoding.UTF8.GetString(await File.ReadAllBytesAsync(path, ct)));
            store.Clear();
            Assert.Null(await store.LoadAsync(ct));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task DeviceIdentity_IsStableAndProducesOpenSslCompatibleDerSignature()
    {
        var root = Path.Combine(Path.GetTempPath(), "isle-device-" + Guid.NewGuid().ToString("N"));
        var store = new DeviceIdentityStore(Path.Combine(root, "device"));
        try
        {
            using var first = await store.LoadOrCreateAsync(TestContext.Current.CancellationToken);
            using var second = await store.LoadOrCreateAsync(TestContext.Current.CancellationToken);
            Assert.Equal(first.DeviceId, second.DeviceId);
            using var verifier = System.Security.Cryptography.ECDsa.Create();
            verifier.ImportFromPem(first.PublicKeyPem);
            var encoded = first.Sign("activation-proof").Replace('-', '+').Replace('_', '/');
            encoded += new string('=', (4 - encoded.Length % 4) % 4);
            Assert.True(verifier.VerifyData(
                Encoding.UTF8.GetBytes("activation-proof"),
                Convert.FromBase64String(encoded),
                System.Security.Cryptography.HashAlgorithmName.SHA256,
                System.Security.Cryptography.DSASignatureFormat.Rfc3279DerSequence));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task AcceptedKey_IsPersistedBeforeAgentProbeFails()
    {
        var root = Path.Combine(Path.GetTempPath(), "isle-accepted-key-" + Guid.NewGuid().ToString("N"));
        var options = new ProClientOptions
        {
            CredentialPath = Path.Combine(root, "credential"),
            InstallationRoot = Path.Combine(root, "Pro"),
            LocalAgentPath = Path.Combine(root, "missing-agent.exe")
        };
        using var http = new HttpClient(new AcceptedActivationHandler());
        using var service = new ProAccessService(options, http);
        try
        {
            var result = await service.ActivateKeyAsync("ISLE-RECOVERABLE", "2.1.4", TestContext.Current.CancellationToken);

            Assert.False(result.AgentReady);
            Assert.Equal("local_agent_unavailable", result.StatusCode);
            Assert.True(result.IsAuthenticated);
            Assert.True(File.Exists(options.CredentialPath + ".key-lease-v1"));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private sealed class NoNetworkHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.Unauthorized)
            {
                Content = new StringContent("{\"error\":\"code_invalid_or_used\"}")
            });
    }

    private sealed class AcceptedActivationHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = System.Net.Http.Json.JsonContent.Create(new ProActivationResponse(
                    "Bearer", "signed-lease", "11111111-2222-4333-8444-555555555555",
                    DateTimeOffset.UtcNow.AddHours(5), 900, 120))
            });
    }
}
