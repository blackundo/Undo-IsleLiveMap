using System.Net;
using System.Net.Sockets;

namespace TheIsleOverlay.ProClient;

internal static class ProHttpTransport
{
    public static HttpClient Create()
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(12),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            ConnectCallback = ConnectAsync
        };
        return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
    }

    internal static IEnumerable<IPAddress> InConnectionOrder(IEnumerable<IPAddress> addresses) =>
        addresses.OrderBy(address => address.AddressFamily == AddressFamily.InterNetwork ? 0 : 1);

    private static async ValueTask<Stream> ConnectAsync(
        SocketsHttpConnectionContext context,
        CancellationToken cancellationToken)
    {
        var addresses = InConnectionOrder(await Dns.GetHostAddressesAsync(
                context.DnsEndPoint.Host, cancellationToken)
            .ConfigureAwait(false)).ToArray();
        if (addresses.Length == 0)
        {
            throw new HttpRequestException($"No address was found for {context.DnsEndPoint.Host}.");
        }

        Exception? lastError = null;
        foreach (var address in addresses)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp)
            {
                NoDelay = true
            };
            using var attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            attempt.CancelAfter(TimeSpan.FromSeconds(5));
            try
            {
                await socket.ConnectAsync(
                        new IPEndPoint(address, context.DnsEndPoint.Port), attempt.Token)
                    .ConfigureAwait(false);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (Exception exception) when (exception is SocketException or OperationCanceledException)
            {
                socket.Dispose();
                if (cancellationToken.IsCancellationRequested) throw;
                lastError = exception;
            }
        }

        throw new HttpRequestException(
            $"Could not connect to {context.DnsEndPoint.Host}:{context.DnsEndPoint.Port}.",
            lastError);
    }
}
