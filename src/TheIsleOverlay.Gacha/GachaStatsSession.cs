using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace TheIsleOverlay.Gacha;

/// <summary>
/// Combines the initial official /me snapshot with sparse WebSocket frames.
/// The session deliberately does not reconnect forever: the host can restart
/// it with a fresh credential/session boundary and decide how to present a
/// reconnecting state in its own UI.
/// </summary>
public sealed class GachaStatsSession : IAsyncDisposable
{
    private readonly IGachaOverlayClient _client;
    private readonly GachaStatsReducer _reducer;
    private readonly bool _ownsClient;
    private int _disposed;
    private int _watchStarted;

    public GachaStatsSession(
        IGachaOverlayClient client,
        GachaStatsReducer? reducer = null,
        bool ownsClient = true)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _reducer = reducer ?? new GachaStatsReducer();
        _ownsClient = ownsClient;
    }

    public GachaStatsReducer Reducer => _reducer;

    public async IAsyncEnumerable<GachaStatsSnapshot> WatchAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (Interlocked.Exchange(ref _watchStarted, 1) != 0)
        {
            throw new InvalidOperationException("A Gacha stats session can only be watched once.");
        }

        var now = DateTimeOffset.UtcNow;
        GachaOverlayMeDto? me = null;
        try
        {
            me = await _client.GetMeAsync(cancellationToken).ConfigureAwait(false);
            _reducer.ApplyApi(me, now);
        }
        catch (GachaOverlayAuthenticationException)
        {
            throw;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (GachaOverlayProtocolException)
        {
            // The live feed can still provide a valid snapshot when /me is
            // temporarily unavailable. Keep the reducer empty and continue.
        }
        catch (HttpRequestException)
        {
        }

        yield return _reducer.BuildSnapshot(DateTimeOffset.UtcNow);

        await foreach (var frame in _client.ReadLiveAsync(cancellationToken).ConfigureAwait(false))
        {
            _reducer.ApplyFrame(frame, DateTimeOffset.UtcNow);
            yield return _reducer.BuildSnapshot(DateTimeOffset.UtcNow);
        }
    }

    /// <summary>
    /// Watches the official feed and reconnects after a normal close or a
    /// transient network failure.  Access tokens are rotated when the client
    /// supports the official refresh contract.  Authentication failures are
    /// surfaced as a stale snapshot and end the iterator so the host can ask
    /// the user to sign in again instead of retrying forever.
    /// </summary>
    public async IAsyncEnumerable<GachaStatsSnapshot> WatchWithReconnectAsync(
        TimeSpan? initialBackoff = null,
        TimeSpan? maximumBackoff = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (Interlocked.Exchange(ref _watchStarted, 1) != 0)
        {
            throw new InvalidOperationException("A Gacha stats session can only be watched once.");
        }

        var minimumBackoff = initialBackoff ?? TimeSpan.FromSeconds(1);
        var backoff = minimumBackoff;
        var maxBackoff = maximumBackoff ?? TimeSpan.FromSeconds(15);
        if (backoff <= TimeSpan.Zero || maxBackoff < backoff)
        {
            throw new ArgumentOutOfRangeException(nameof(initialBackoff));
        }

        var now = DateTimeOffset.UtcNow;
        var bootstrapAuthenticationFailed = false;
        try
        {
            var me = await _client.GetMeAsync(cancellationToken).ConfigureAwait(false);
            _reducer.ApplyApi(me, now);
        }
        catch (GachaOverlayAuthenticationException)
        {
            bootstrapAuthenticationFailed = true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (GachaOverlayProtocolException)
        {
        }
        catch (HttpRequestException)
        {
        }

        if (bootstrapAuthenticationFailed)
        {
            yield return _reducer.BuildSnapshot(now) with
            {
                IsStale = true,
                StatusMessage = "Gacha · CẦN ĐĂNG NHẬP LẠI"
            };
            yield break;
        }

        yield return _reducer.BuildSnapshot(DateTimeOffset.UtcNow);

        while (!cancellationToken.IsCancellationRequested)
        {
            var channel = Channel.CreateBounded<ConnectionEvent>(new BoundedChannelOptions(64)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = true
            });
            var connectionTask = PumpConnectionAsync(channel.Writer, cancellationToken);
            var authenticationFailed = false;
            var connectionEnded = false;
            var receivedFrame = false;
            try
            {
                await foreach (var item in channel.Reader.ReadAllAsync(cancellationToken)
                                   .ConfigureAwait(false))
                {
                    switch (item)
                    {
                        case FrameEvent frameEvent:
                            receivedFrame = true;
                            _reducer.ApplyFrame(frameEvent.Frame, DateTimeOffset.UtcNow);
                            yield return _reducer.BuildSnapshot(DateTimeOffset.UtcNow);
                            break;
                        case ConnectionEnded ended:
                            connectionEnded = true;
                            authenticationFailed = ended.AuthenticationFailed;
                            break;
                    }
                }
            }
            finally
            {
                await connectionTask.ConfigureAwait(false);
            }

            if (authenticationFailed)
            {
                yield return _reducer.BuildSnapshot(DateTimeOffset.UtcNow) with
                {
                    IsStale = true,
                    StatusMessage = "Gacha · CẦN ĐĂNG NHẬP LẠI"
                };
                yield break;
            }

            // A clean WebSocket close is still a loss of the live source.
            // Mark it reconnecting just like a transport failure so cached
            // stats cannot remain presented as LIVE during the backoff.
            if (connectionEnded)
            {
                yield return _reducer.BuildSnapshot(DateTimeOffset.UtcNow) with
                {
                    IsStale = true,
                    StatusMessage = "Gacha · ĐANG KẾT NỐI LẠI"
                };
            }

            // Once a connection delivered a valid protocol frame, a later
            // close starts again at the minimum delay.  Without this reset,
            // one burst of early failures permanently leaves every future
            // reconnect waiting at the maximum backoff.
            if (receivedFrame)
            {
                backoff = minimumBackoff;
            }

            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            if (_client is GachaOverlayClient concreteClient)
            {
                try
                {
                    _ = await concreteClient.TryRefreshAsync(cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
            }

            try
            {
                await Task.Delay(backoff, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            var nextMilliseconds = Math.Min(
                maxBackoff.TotalMilliseconds,
                Math.Max(backoff.TotalMilliseconds * 2d, backoff.TotalMilliseconds + 1d));
            backoff = TimeSpan.FromMilliseconds(nextMilliseconds);
        }
    }

    private async Task PumpConnectionAsync(
        ChannelWriter<ConnectionEvent> writer,
        CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var frame in _client.ReadLiveAsync(cancellationToken)
                               .ConfigureAwait(false))
            {
                await writer.WriteAsync(new FrameEvent(frame), cancellationToken)
                    .ConfigureAwait(false);
            }

            writer.TryWrite(new ConnectionEnded(null, false));
        }
        catch (GachaOverlayAuthenticationException)
        {
            writer.TryWrite(new ConnectionEnded(null, true));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            writer.TryWrite(new ConnectionEnded(exception, false));
        }
        finally
        {
            writer.TryComplete();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        if (_ownsClient)
        {
            await _client.DisposeAsync().ConfigureAwait(false);
        }
    }

    private abstract record ConnectionEvent;

    private sealed record FrameEvent(GachaOverlayFrameDto Frame) : ConnectionEvent;

    private sealed record ConnectionEnded(
        Exception? Error,
        bool AuthenticationFailed) : ConnectionEvent;
}
