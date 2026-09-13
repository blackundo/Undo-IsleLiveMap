using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using TheIsleOverlay.ProClient;

namespace TheIsleOverlay.ProClient.Tests;

public sealed class ProReleaseManagerTests
{
    [Theory]
    [InlineData("1.5.2", true, true)]
    [InlineData("1.5.1", true, false)]
    [InlineData("2.0.0", true, false)]
    [InlineData("1.5.2", false, false)]
    public async Task EnsureAvailableAsync_UsesCompatibleLocalAgentWithoutNetwork(
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
                Assert.Equal(0, handler.RequestCount);
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
    public async Task EnsureLatestAsync_InstallsSignedCompatibleArtifact()
    {
        var archive = CreateArchive(("IsleLiveMap.Pro.Agent.exe", "agent-binary"));
        using var key = RSA.Create(2048);
        var manifest = SignManifest(key, archive);
        using var httpClient = new HttpClient(new ReleaseHandler(manifest, archive));
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
            Assert.True(File.Exists(installation.ExecutablePath));
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
    public async Task EnsureLatestAsync_RejectsSignedArchiveWithTraversalPath()
    {
        var archive = CreateArchive(
            ("IsleLiveMap.Pro.Agent.exe", "agent-binary"),
            ("../escaped.txt", "must-not-extract"));
        using var key = RSA.Create(2048);
        var manifest = SignManifest(key, archive);
        using var httpClient = new HttpClient(new ReleaseHandler(manifest, archive));
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
            Assert.False(File.Exists(Path.Combine(root, "versions", "escaped.txt")));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static byte[] CreateArchive(params (string Path, string Content)[] entries)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var item in entries)
            {
                var entry = archive.CreateEntry(item.Path);
                using var writer = new StreamWriter(entry.Open());
                writer.Write(item.Content);
            }
        }

        return stream.ToArray();
    }

    private static ProReleaseManifest SignManifest(RSA key, byte[] archive)
    {
        var unsigned = new ProReleaseManifest(
            "0.1.0",
            ProReleaseManager.IpcApiMajor,
            "1.4.0",
            "2.0.0",
            archive.Length,
            Convert.ToHexString(SHA256.HashData(archive)).ToLowerInvariant(),
            string.Empty,
            "https://isle.test/artifact.zip",
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
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.RequestUri?.AbsolutePath == "/api/v1/pro/manifest")
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(manifest)
                });
            }

            if (request.RequestUri?.AbsolutePath == "/artifact.zip")
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(artifact)
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}
