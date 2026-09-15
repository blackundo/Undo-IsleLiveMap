using System.Net;
using System.Net.Http;
using System.Text;
using TheIsleOverlay.Origin;

namespace TheIsleOverlay.Tests;

public sealed class OriginStatsClientTests
{
    [Fact]
    public async Task HealthCommandUsesServerIdAndParsesDashboardResult()
    {
        var handler = new QueueHandler(
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = Json("{\"id\":\"cmd-1\"}")
            },
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = Json("{\"status\":\"completed\",\"result\":{\"success\":true,\"species\":\"T-Rex\",\"growth\":0.75,\"hp\":100,\"maxHp\":200}}")
            });
        using var client = new OriginStatsClient(
            "origin_session=redacted",
            new HttpClient(handler));

        var result = await client.ExecuteHealthAsync(OriginServer.All[0]);

        Assert.True(result.IsCompletedSuccessfully);
        Assert.Equal("completed", result.Status);
        Assert.Equal("T-Rex", result.Result!.Value.GetProperty("species").GetString());
        Assert.Contains("\"server\":\"main\"", handler.Requests[0].Content!, StringComparison.Ordinal);
        Assert.Equal("origin_session=redacted", handler.Requests[0].Cookie);
    }

    [Fact]
    public async Task RedirectToAnotherHostIsRejected()
    {
        var handler = new QueueHandler(new HttpResponseMessage(HttpStatusCode.Redirect)
        {
            Headers = { Location = new Uri("https://evil.example/") }
        });
        using var client = new OriginStatsClient(
            "origin_session=redacted",
            new HttpClient(handler));

        await Assert.ThrowsAsync<OriginProtocolException>(() =>
            client.ExecuteHealthAsync(OriginServer.All[0]));
    }

    [Fact]
    public void ActiveServerCookieIsUsedAsProbeHint()
    {
        using var client = new OriginStatsClient(
            "session=redacted; origin_server=voice");

        Assert.Equal("voice", client.PreferredServer?.ApiId);
    }

    [Fact]
    public async Task DetectActiveServerChecksDashboardSelectionFirst()
    {
        var handler = new QueueHandler(
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = Json("{\"id\":\"cmd-voice\"}")
            },
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = Json("{\"status\":\"completed\",\"result\":{\"success\":true,\"species\":\"Triceratops\"}}")
            });
        using var client = new OriginStatsClient(
            "session=redacted; origin_server=voice",
            new HttpClient(handler));

        var server = await client.DetectActiveServerAsync();

        Assert.Equal("voice", server?.ApiId);
        Assert.Contains("\"server\":\"voice\"", handler.Requests[0].Content!, StringComparison.Ordinal);
    }

    private static StringContent Json(string value) =>
        new(value, Encoding.UTF8, "application/json");

    private sealed class QueueHandler(params HttpResponseMessage[] responses) : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses = new(responses);
        public List<RequestInfo> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = request.Content?.ReadAsStringAsync(cancellationToken).GetAwaiter().GetResult();
            Requests.Add(new RequestInfo(
                body,
                request.Headers.TryGetValues("Cookie", out var values)
                    ? values.Single()
                    : null));
            var response = _responses.Dequeue();
            response.RequestMessage = request;
            return Task.FromResult(response);
        }
    }

    private sealed record RequestInfo(string? Content, string? Cookie);
}
