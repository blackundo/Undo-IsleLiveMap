using System.Net;
using System.Runtime.CompilerServices;
using TheIsleOverlay.Gacha;

namespace TheIsleOverlay.Tests;

public sealed class GachaHostLifecycleTests
{
    [Fact]
    public async Task Validation_DoesNotConsumeOrRotateAnExpiredCredential()
    {
        var handler = new RecordingHandler(request => request.Method == HttpMethod.Get
            ? Response(HttpStatusCode.Unauthorized)
            : Response(
                HttpStatusCode.OK,
                """{"accessToken":"rotated","refreshToken":"rotated-refresh"}"""));
        using var http = new HttpClient(handler);
        var credentials = new GachaOverlayCredentials(
            "expired-access",
            steamId: "76561198000000000",
            deviceId: "device-a",
            refreshToken: "refresh-a",
            accessExpiresAt: DateTimeOffset.UtcNow.AddMinutes(-1));

        var result = await GachaOverlayAuthService.ValidateAsync(http, credentials);

        Assert.Equal(GachaOverlayAuthValidationState.Invalid, result);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("/api/overlay/me", request.Uri.AbsolutePath);
        Assert.Equal("expired-access", request.BearerToken);
    }

    [Fact]
    public async Task NormalWebSocketClose_IsPublishedAsReconnectingAndStale()
    {
        await using var client = new ClosingClient();
        await using var session = new GachaStatsSession(client, ownsClient: false);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var snapshots = session.WatchWithReconnectAsync(
                initialBackoff: TimeSpan.FromMilliseconds(10),
                maximumBackoff: TimeSpan.FromMilliseconds(20),
                cancellationToken: cancellation.Token)
            .GetAsyncEnumerator(cancellation.Token);

        Assert.True(await snapshots.MoveNextAsync()); // /me bootstrap
        Assert.True(await snapshots.MoveNextAsync()); // ready
        Assert.True(await snapshots.MoveNextAsync()); // dino
        Assert.True(await snapshots.MoveNextAsync()); // clean close

        Assert.True(snapshots.Current.IsStale);
        Assert.Equal("Gacha · ĐANG KẾT NỐI LẠI", snapshots.Current.StatusMessage);
        Assert.Equal("Deinosuchus", snapshots.Current.Species);
    }

    private static HttpResponseMessage Response(
        HttpStatusCode statusCode,
        string content = "{}") => new(statusCode)
    {
        Content = new StringContent(content)
    };

    private sealed class RecordingHandler(
        Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        public List<RecordedRequest> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(new RecordedRequest(
                request.Method,
                request.RequestUri!,
                request.Headers.Authorization?.Parameter));
            var response = responseFactory(request);
            response.RequestMessage = request;
            return Task.FromResult(response);
        }
    }

    private sealed record RecordedRequest(
        HttpMethod Method,
        Uri Uri,
        string? BearerToken);

    private sealed class ClosingClient : IGachaOverlayClient
    {
        private int _connection;

        public Task<GachaOverlayMeDto> GetMeAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new GachaOverlayMeDto());

        public async IAsyncEnumerable<GachaOverlayFrameDto> ReadLiveAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var connection = Interlocked.Increment(ref _connection);
            if (connection == 1)
            {
                yield return new GachaOverlayFrameDto
                {
                    Type = "overlay.ready",
                    ServerId = "gacha-01",
                    Sequence = 1
                };
                yield return new GachaOverlayFrameDto
                {
                    Type = "overlay.dino",
                    ServerId = "gacha-01",
                    Sequence = 2,
                    Stats = new GachaDinoStatsDto
                    {
                        Found = true,
                        DinoName = "Deinosuchus",
                        Health = 80,
                        MaxHealth = 100
                    }
                };
                yield break;
            }

            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            yield break;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
