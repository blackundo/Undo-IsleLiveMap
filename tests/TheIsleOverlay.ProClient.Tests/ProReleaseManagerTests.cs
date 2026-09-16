using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using TheIsleOverlay.ProClient;

namespace TheIsleOverlay.ProClient.Tests;

public sealed class ProReleaseManagerTests
{
    [Theory]
    [InlineData("1.5.2", true, true)]
    [InlineData("1.5.1", true, false)]
    [InlineData("2.0.0", true, false)]
    [InlineData("1.5.2", false, false)]
    public async Task EnsureAvailableAsync_ChecksForUpdatesAndFallsBackToCompatibleLocalAgent(
        string hostVersion, bool executableExists, bool expectedLocal)
    {
        var root = TemporaryDirectory();
        var handler = new UnavailableReleaseHandler();
        using var httpClient = new HttpClient(handler);
        using var key = RSA.Create(2048);
        try
        {
            var versionRoot = Path.Combine(root, "versions", "0.3.22");
            Directory.CreateDirectory(versionRoot);
            var executable = Path.Combine(versionRoot, "IsleLiveMap.Pro.Agent.exe");
            if (executableExists)
            {
                await File.WriteAllTextAsync(executable, "local-agent", TestContext.Current.CancellationToken);
            }

            await File.WriteAllTextAsync(Path.Combine(root, "current.json"),
                System.Text.Json.JsonSerializer.Serialize(new
                {
                    version = "0.3.22",
                    ipcApiMajor = ProReleaseManager.IpcApiMajor,
                    minHostVersion = "1.5.2",
                    maxHostVersionExclusive = "2.0.0",
                    artifactSha256 = new string('a', 64),
                    artifactSignature = "installed-signature"
                }), TestContext.Current.CancellationToken);
            using var manager = new ProReleaseManager(
                new ProApiClient(httpClient, new Uri("https://isle.test/")),
                root, key.ExportSubjectPublicKeyInfoPem());

            if (expectedLocal)
            {
                var installation = await manager.EnsureAvailableAsync(
                    hostVersion, "access-token", TestContext.Current.CancellationToken);
                Assert.Equal("0.3.22", installation.Version);
                Assert.Equal(executable, installation.ExecutablePath);
                Assert.Equal(1, handler.RequestCount);
            }
            else
            {
                await Assert.ThrowsAsync<ProApiException>(() => manager.EnsureAvailableAsync(
                    hostVersion, "access-token", TestContext.Current.CancellationToken));
                Assert.Equal(1, handler.RequestCount);
            }
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private sealed class UnavailableReleaseHandler : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        }
    }

    [Fact]
    public async Task EnsureAvailableAsync_ReplacesOlderCompatibleLocalAgent()
    {
        var root = TemporaryDirectory();
        var executable = Encoding.UTF8.GetBytes("new-agent");
        using var key = RSA.Create(2048);
        var manifest = SignManifest(key, executable, "0.3.27");
        using var httpClient = new HttpClient(new ReleaseHandler(manifest, executable));
        try
        {
            var oldRoot = Path.Combine(root, "versions", "0.3.23");
            Directory.CreateDirectory(oldRoot);
            await File.WriteAllTextAsync(
                Path.Combine(oldRoot, "IsleLiveMap.Pro.Agent.exe"),
                "old-agent",
                TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(Path.Combine(root, "current.json"),
                System.Text.Json.JsonSerializer.Serialize(new
                {
                    version = "0.3.23",
                    ipcApiMajor = ProReleaseManager.IpcApiMajor,
                    minHostVersion = "1.4.0",
                    maxHostVersionExclusive = "2.0.0",
                    artifactSha256 = new string('a', 64),
                    artifactSignature = "old-signature"
                }), TestContext.Current.CancellationToken);

            using var manager = new ProReleaseManager(
                new ProApiClient(httpClient, new Uri("https://isle.test/")),
                root,
                key.ExportSubjectPublicKeyInfoPem());
            var installation = await manager.EnsureAvailableAsync(
                "1.4.0", "access-token", TestContext.Current.CancellationToken);

            Assert.Equal("0.3.27", installation.Version);
            Assert.Equal("new-agent", await File.ReadAllTextAsync(
                installation.ExecutablePath,
                TestContext.Current.CancellationToken));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task EnsureLatestAsync_InstallsSignedCompatibleArtifact()
    {
        var executable = Encoding.UTF8.GetBytes("agent-binary");
        using var key = RSA.Create(2048);
        var manifest = SignManifest(key, executable);
        var handler = new ReleaseHandler(manifest, executable);
        using var httpClient = new HttpClient(handler);
        var api = new ProApiClient(httpClient, new Uri("https://isle.test/"));
        var root = TemporaryDirectory();

        try
        {
            using var manager = new ProReleaseManager(api, root, key.ExportSubjectPublicKeyInfoPem());
            var installation = await manager.EnsureLatestAsync(
                "1.4.0",
                "access-token",
                TestContext.Current.CancellationToken);

            Assert.Equal("0.1.0", installation.Version);
            Assert.Equal(Path.Combine(
                root, "versions", "0.1.0", "IsleLiveMap.Pro.Agent.exe"),
                installation.ExecutablePath);
            Assert.True(File.Exists(installation.ExecutablePath));
            Assert.True(File.Exists(Path.Combine(root, "current.json")));
            Assert.Equal("access-token", handler.ManifestBearerToken);
            Assert.Equal("access-token", handler.ArtifactBearerToken);
            Assert.Equal("agent-binary", await File.ReadAllTextAsync(
                installation.ExecutablePath,
                TestContext.Current.CancellationToken));
            Assert.NotNull(await manager.LoadInstalledAsync(
                "1.9.9",
                TestContext.Current.CancellationToken));
            Assert.Null(await manager.LoadInstalledAsync(
                "2.0.0",
                TestContext.Current.CancellationToken));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task EnsureLatestAsync_RejectsArtifactWithMismatchedHash()
    {
        var expectedExecutable = Encoding.UTF8.GetBytes("agent-binary");
        var tamperedExecutable = Encoding.UTF8.GetBytes("tampered-agent");
        using var key = RSA.Create(2048);
        var manifest = SignManifest(key, expectedExecutable) with
        {
            Size = tamperedExecutable.Length
        };
        var signature = Convert.ToBase64String(key.SignData(
            ProReleaseSignatureVerifier.CreateCanonicalPayload(manifest),
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1));
        manifest = manifest with { Signature = signature };
        using var httpClient = new HttpClient(new ReleaseHandler(manifest, tamperedExecutable));
        var api = new ProApiClient(httpClient, new Uri("https://isle.test/"));
        var root = TemporaryDirectory();

        try
        {
            using var manager = new ProReleaseManager(api, root, key.ExportSubjectPublicKeyInfoPem());

            await Assert.ThrowsAsync<InvalidDataException>(async () =>
                await manager.EnsureLatestAsync(
                    "1.4.0",
                    "access-token",
                    TestContext.Current.CancellationToken));
            Assert.False(File.Exists(Path.Combine(
                root, "versions", manifest.Version, "IsleLiveMap.Pro.Agent.exe")));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static ProReleaseManifest SignManifest(
        RSA key,
        byte[] executable,
        string version = "0.1.0")
    {
        var unsigned = new ProReleaseManifest(
            version,
            ProReleaseManager.IpcApiMajor,
            "1.4.0",
            "2.0.0",
            executable.Length,
            Convert.ToHexString(SHA256.HashData(executable)).ToLowerInvariant(),
            string.Empty,
            "https://isle.test/IsleLiveMap.Pro.Agent.exe",
            DateTimeOffset.Parse("2026-08-26T00:00:00Z"));
        var signature = Convert.ToBase64String(key.SignData(
            ProReleaseSignatureVerifier.CreateCanonicalPayload(unsigned),
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1));
        return unsigned with { Signature = signature };
    }

    private static string TemporaryDirectory() => Path.Combine(
        Path.GetTempPath(),
        $"isle-pro-release-{Guid.NewGuid():N}");

    private sealed class ReleaseHandler(
        ProReleaseManifest manifest,
        byte[] artifact) : HttpMessageHandler
    {
        public string? ManifestBearerToken { get; private set; }

        public string? ArtifactBearerToken { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.RequestUri?.AbsolutePath == "/api/v1/pro/manifest")
            {
                ManifestBearerToken = request.Headers.Authorization?.Parameter;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(manifest)
                });
            }

            if (request.RequestUri?.AbsolutePath == "/IsleLiveMap.Pro.Agent.exe")
            {
                ArtifactBearerToken = request.Headers.Authorization?.Parameter;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(artifact)
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}
