using System.Text;
using TheIsleOverlay.ProClient;

namespace TheIsleOverlay.ProClient.Tests;

public sealed class LocalProActivationTests
{
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
        await Assert.ThrowsAsync<ProAgentException>(() => service.ActivateKeyAsync("example-key", "1.5.2", ct));
        Assert.False(service.Current.IsPro);
        Assert.Null(service.CreateRemotePlayerSource());
        Assert.False(Directory.Exists(root));
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

    private sealed class NoNetworkHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new InvalidOperationException("Local activation must not access the network.");
    }
}
