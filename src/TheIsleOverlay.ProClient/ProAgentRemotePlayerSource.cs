using System.Diagnostics;
using System.IO.Pipes;
using System.Collections.Concurrent;
using System.Threading.Channels;
using IsleLiveMap.Activation;
using TheIsleOverlay.Core;

namespace TheIsleOverlay.ProClient;

public sealed class ProAgentException(string message) : Exception(message);

public sealed class ProAgentRemotePlayerSource :
    IRemotePlayerTelemetrySource,
    IRemotePlayerTelemetryHealthSource,
    IProFeatureController,
    IProRealtimeConnectionBridge
{
    private static readonly TimeSpan ConnectionTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan MaximumRestartDelay = TimeSpan.FromSeconds(30);

    private readonly string _agentExecutablePath;
    private readonly string _hostVersion;
    private readonly string _steamId64;
    private readonly string _offlineLicenseToken;
    private readonly bool _localActivation;
    private readonly CancellationTokenSource _disposeCancellation = new();
    private readonly Channel<HostCommand> _featureCommands =
        Channel.CreateUnbounded<HostCommand>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });
    private readonly ConcurrentDictionary<string, TaskCompletionSource<ProFeatureCommandResult>>
        _pendingFeatureCommands = new(StringComparer.Ordinal);
    private int _watchStarted;
    private int _disposed;
    private IRealtimeConnectionControl? _realtimeControl;
    private int _realtimePausedByAgent;
    private RemotePlayerCaptureHealth _captureHealth = RemotePlayerCaptureHealth.Starting;

    public RemotePlayerCaptureHealth CaptureHealth =>
        Volatile.Read(ref _captureHealth);

    public ProAgentRemotePlayerSource(
        string agentExecutablePath,
        string hostVersion,
        string steamId64,
        string offlineLicenseToken,
        bool localActivation = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentExecutablePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(hostVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(steamId64);
        ArgumentException.ThrowIfNullOrWhiteSpace(offlineLicenseToken);
        _agentExecutablePath = Path.GetFullPath(agentExecutablePath);
        _hostVersion = hostVersion;
        _steamId64 = steamId64;
        _offlineLicenseToken = offlineLicenseToken;
        _localActivation = localActivation;
    }

    internal static ProAgentRemotePlayerSource ForDeviceLease(string path, string hostVersion, string activationId, string lease) =>
        new(path, hostVersion, activationId, lease, localActivation: true);

    public Task<ProFeatureCommandResult> ToggleSkinEditorAsync(
        ProSkinEditorContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        return SendFeatureCommandAsync(
            "skin-editor",
            context.Server,
            context.Species,
            context.Female,
            null,
            cancellationToken);
    }

    public Task<ProFeatureCommandResult> ToggleGarageAsync(
        ProGarageContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        return SendFeatureCommandAsync(
            "garage",
            context.Server,
            context.Species,
            null,
            context.Growth,
            cancellationToken);
    }

    private HostHello CreateHello(bool probeOnly = false) => _localActivation
        ? new(ProAgentProtocol.IpcApiMajor, _hostVersion, string.Empty,
            LocalProActivation.Mode, _offlineLicenseToken, probeOnly,
            SupportsRealtimeControl: true)
        : new(ProAgentProtocol.IpcApiMajor, _hostVersion, _offlineLicenseToken,
            SupportsRealtimeControl: true);

    public void AttachRealtimeConnectionControl(IRealtimeConnectionControl control)
    {
        ArgumentNullException.ThrowIfNull(control);
        Volatile.Write(ref _realtimeControl, control);
    }

    internal async Task<string> ProbeAsync(CancellationToken cancellationToken)
    {
        var pipeName = ProAgentProtocol.PipePrefix + Guid.NewGuid().ToString("N");
        await using var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        using var process = StartAgent(pipeName);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(ConnectionTimeout + HandshakeTimeout);
            await pipe.WaitForConnectionAsync(timeout.Token).ConfigureAwait(false);
            await using var ipc = new IpcJsonStream(pipe);
            await ipc.WriteAsync(CreateHello(probeOnly: true), timeout.Token).ConfigureAwait(false);
            var response = await ipc.ReadAsync<AgentMessage>(timeout.Token).ConfigureAwait(false);
            ValidateHandshake(response);
            return response.Hello!.AgentVersion;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ProAgentException("The Pro Agent activation timed out.");
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
    }

    public async IAsyncEnumerable<RemotePlayerTelemetryFrame> WatchAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation]
        CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _watchStarted, 1) != 0)
        {
            throw new InvalidOperationException("A Pro Agent source can only be watched once.");
        }

        if (!OperatingSystem.IsWindows() || !File.Exists(_agentExecutablePath))
        {
            throw new ProAgentException("The installed Pro Agent is unavailable.");
        }

        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _disposeCancellation.Token);
        var restartAttempt = 0;
        while (!linkedCancellation.IsCancellationRequested)
        {
            await using var session = WatchAgentSessionAsync(linkedCancellation.Token)
                .GetAsyncEnumerator(linkedCancellation.Token);
            var restart = false;
            while (!linkedCancellation.IsCancellationRequested)
            {
                RemotePlayerTelemetryFrame? frame = null;
                try
                {
                    if (!await session.MoveNextAsync().ConfigureAwait(false))
                    {
                        restart = true;
                        break;
                    }

                    frame = session.Current;
                }
                catch (OperationCanceledException) when (linkedCancellation.IsCancellationRequested)
                {
                    yield break;
                }
                catch (Exception exception)
                {
                    SetFaultedHealth(UserFacingAgentFailure(exception));
                    restart = true;
                    break;
                }

                restartAttempt = 0;
                yield return frame;
            }

            if (!restart || linkedCancellation.IsCancellationRequested)
            {
                yield break;
            }

            var restartDelay = TimeSpan.FromSeconds(Math.Min(
                MaximumRestartDelay.TotalSeconds,
                2d * Math.Pow(2d, Math.Min(restartAttempt++, 4))));
            try
            {
                await Task.Delay(restartDelay, linkedCancellation.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (linkedCancellation.IsCancellationRequested)
            {
                yield break;
            }

            Volatile.Write(ref _captureHealth, CaptureHealth with
            {
                State = RemotePlayerCaptureState.Starting,
                Message = "Đang tự khởi động lại Pro Agent."
            });
        }
    }

    private async IAsyncEnumerable<RemotePlayerTelemetryFrame> WatchAgentSessionAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation]
        CancellationToken cancellationToken)
    {
        var pipeName = ProAgentProtocol.PipePrefix + Guid.NewGuid().ToString("N");
        await using var pipe = new NamedPipeServerStream(
            pipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.WriteThrough | PipeOptions.CurrentUserOnly);
        using var process = StartAgent(pipeName);
        try
        {
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                timeout.CancelAfter(ConnectionTimeout);
                try
                {
                    await pipe.WaitForConnectionAsync(timeout.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    throw new ProAgentException("The Pro Agent did not connect in time.");
                }
            }

            await using var ipc = new IpcJsonStream(pipe);
            await ipc.WriteAsync(
                    CreateHello(),
                    cancellationToken)
                .ConfigureAwait(false);

            AgentMessage response;
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                timeout.CancelAfter(HandshakeTimeout);
                try
                {
                    response = await ipc.ReadAsync<AgentMessage>(timeout.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    throw new ProAgentException("The Pro Agent handshake timed out.");
                }
            }

            ValidateHandshake(response);
            using var commandCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var commandTask = PublishFeatureCommandsAsync(ipc, commandCancellation.Token);
            long lastSequence = 0;
            var hasCaptureStatus = false;
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    var message = await ipc.ReadAsync<AgentMessage>(cancellationToken).ConfigureAwait(false);
                    if (message.Error is { Fatal: true } error)
                    {
                        SetFaultedHealth(error.Message);
                        throw new ProAgentException($"Pro Agent stopped: {error.Code}.");
                    }

                    if (message.CaptureStatus is { } captureStatus)
                    {
                        hasCaptureStatus = true;
                        Volatile.Write(ref _captureHealth, MapCaptureHealth(captureStatus));
                    }

                    if (message.FeatureResult is { } featureResult
                        && _pendingFeatureCommands.TryRemove(
                            featureResult.CommandId,
                            out var completion))
                    {
                        completion.TrySetResult(new ProFeatureCommandResult(
                            featureResult.Success,
                            featureResult.ErrorMessage));
                    }

                    if (message.RealtimeControl is { } realtimeControl)
                    {
                        var result = await HandleRealtimeControlAsync(
                                realtimeControl,
                                cancellationToken)
                            .ConfigureAwait(false);
                        if (!_featureCommands.Writer.TryWrite(new HostCommand(
                                "realtime-control-result",
                                "realtime",
                                realtimeControl.RequestId,
                                RealtimeControlResult: result)))
                        {
                            throw new ProAgentException(
                                "Không thể phản hồi yêu cầu điều khiển realtime của Pro Agent.");
                        }
                    }

                    if (message.Telemetry is not { } telemetry || telemetry.Sequence <= lastSequence)
                    {
                        continue;
                    }

                    lastSequence = telemetry.Sequence;
                    if (!hasCaptureStatus)
                    {
                        Volatile.Write(ref _captureHealth, CaptureHealth with
                        {
                            State = RemotePlayerCaptureState.Receiving,
                            GameProcessFound = true,
                            LastGamePacketAt = DateTimeOffset.UtcNow,
                            Message = null
                        });
                    }
                    yield return MapFrame(telemetry);
                }
            }
            finally
            {
                commandCancellation.Cancel();
                try
                {
                    await commandTask.ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (commandCancellation.IsCancellationRequested)
                {
                }
            }
        }
        finally
        {
            ReleaseRealtimeControl();
            StopAgent(process);
        }
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _disposeCancellation.Cancel();
            foreach (var pending in _pendingFeatureCommands.Values)
            {
                pending.TrySetResult(new ProFeatureCommandResult(false, "Pro Agent đã dừng."));
            }
            _pendingFeatureCommands.Clear();
            ReleaseRealtimeControl();
            _disposeCancellation.Dispose();
        }

        return ValueTask.CompletedTask;
    }

    internal async Task<HostRealtimeControlResult> HandleRealtimeControlAsync(
        AgentRealtimeControlRequest request,
        CancellationToken cancellationToken)
    {
        var control = Volatile.Read(ref _realtimeControl);
        if (control is null)
        {
            return new HostRealtimeControlResult(
                request.RequestId,
                false,
                "Phiên dino stats chưa sẵn sàng để chuyển quyền WebSocket.");
        }

        try
        {
            if (request.Pause)
            {
                if (Volatile.Read(ref _realtimePausedByAgent) == 0)
                {
                    try
                    {
                        await control.PauseRealtimeAsync(cancellationToken).ConfigureAwait(false);
                    }
                    catch
                    {
                        // PauseRealtimeAsync marks the session paused before it waits
                        // for the active socket to finish. If that wait is cancelled or
                        // fails, undo the pause even though no ACK will be sent.
                        control.ResumeRealtime();
                        throw;
                    }
                    Volatile.Write(ref _realtimePausedByAgent, 1);
                }
            }
            else if (Interlocked.Exchange(ref _realtimePausedByAgent, 0) != 0)
            {
                control.ResumeRealtime();
            }

            return new HostRealtimeControlResult(request.RequestId, true);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new HostRealtimeControlResult(
                request.RequestId,
                false,
                $"Không chuyển được quyền WebSocket: {exception.Message}");
        }
    }

    private void ReleaseRealtimeControl()
    {
        if (Interlocked.Exchange(ref _realtimePausedByAgent, 0) != 0)
        {
            Volatile.Read(ref _realtimeControl)?.ResumeRealtime();
        }
    }

    private async Task PublishFeatureCommandsAsync(
        IpcJsonStream ipc,
        CancellationToken cancellationToken)
    {
        await foreach (var command in _featureCommands.Reader
                           .ReadAllAsync(cancellationToken)
                           .ConfigureAwait(false))
        {
            try
            {
                await ipc.WriteAsync(command, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                if (_pendingFeatureCommands.TryRemove(command.CommandId, out var completion))
                {
                    completion.TrySetResult(new ProFeatureCommandResult(
                        false,
                        $"Không gửi được lệnh tới Pro Agent: {exception.Message}"));
                }
                throw;
            }
        }
    }

    private async Task<ProFeatureCommandResult> SendFeatureCommandAsync(
        string feature,
        string? server,
        string? species,
        bool? female,
        double? growth,
        CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return new ProFeatureCommandResult(false, "Pro Agent đã dừng.");
        }

        var commandId = Guid.NewGuid().ToString("N");
        var completion = new TaskCompletionSource<ProFeatureCommandResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pendingFeatureCommands.TryAdd(commandId, completion))
        {
            return new ProFeatureCommandResult(false, "Không thể tạo lệnh Pro.");
        }

        if (!_featureCommands.Writer.TryWrite(new HostCommand(
                "feature",
                feature,
                commandId,
                server,
                species,
                female,
                growth)))
        {
            _pendingFeatureCommands.TryRemove(commandId, out _);
            return new ProFeatureCommandResult(false, "Pro Agent chưa sẵn sàng nhận lệnh.");
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _disposeCancellation.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            return await completion.Task.WaitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return new ProFeatureCommandResult(
                false,
                cancellationToken.IsCancellationRequested
                    ? "Lệnh Pro đã bị hủy."
                    : "Pro Agent không phản hồi lệnh trong 10 giây.");
        }
        finally
        {
            _pendingFeatureCommands.TryRemove(commandId, out _);
        }
    }

    private Process StartAgent(string pipeName)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = _agentExecutablePath,
            WorkingDirectory = Path.GetDirectoryName(_agentExecutablePath)!,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("--pipe");
        startInfo.ArgumentList.Add(pipeName);
        startInfo.ArgumentList.Add("--parent-pid");
        startInfo.ArgumentList.Add(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return Process.Start(startInfo)
               ?? throw new ProAgentException("Windows could not start the Pro Agent.");
    }

    private void ValidateHandshake(AgentMessage response)
    {
        var hello = response.Hello;
        if (!string.Equals(response.Type, "hello", StringComparison.Ordinal) ||
            hello is null ||
            !hello.Accepted ||
            hello.IpcApiMajor != ProAgentProtocol.IpcApiMajor ||
            (_localActivation
                ? hello.ActivationMode != LocalProActivation.Mode || !string.Equals(hello.SteamId64, _steamId64, StringComparison.Ordinal)
                : !string.Equals(hello.SteamId64, _steamId64, StringComparison.Ordinal)))
        {
            throw new ProAgentException(
                hello?.ErrorCode is { Length: > 0 } code
                    ? $"Pro Agent rejected the session: {code}."
                    : "The Pro Agent handshake is invalid.");
        }
    }

    private static RemotePlayerTelemetryFrame MapFrame(ProTelemetryFrame frame)
    {
        if (!IsFinite(frame.LocalLocation))
        {
            throw new ProAgentException("The Pro Agent returned an invalid local position.");
        }

        var hasValidLocalSpecies = HasValidSpeciesIdentity(
            frame.LocalSpeciesId,
            frame.LocalSpeciesShortName);

        var entities = (frame.RemoteEntities ?? [])
            .Where(IsValidEntity)
            .Select(entity => new VerifiedRemoteEntityTelemetry(
                entity.TrackId,
                MapKind(entity.Kind),
                entity.Kind == MapEntityKind.Player
                    ? string.IsNullOrWhiteSpace(entity.PlayerProofName)
                        ? null
                        : entity.PlayerProofName.Trim()
                    : null,
                entity.SpeciesId?.Trim() ?? string.Empty,
                entity.SpeciesShortName?.Trim() ?? string.Empty,
                MapDiet(entity.Diet),
                entity.MassKg,
                new WorldLocation
                {
                    X = entity.Location.X,
                    Y = entity.Location.Y,
                    Z = entity.Location.Z
                },
                entity.DistanceFromLocal,
                entity.ConfirmationHits,
                entity.ObservedAt,
                entity.IsProvisional))
            .ToArray();

        return new RemotePlayerTelemetryFrame(
            frame.Sequence,
            frame.ObservedAt,
            frame.ServerEndpoint,
            new WorldLocation
            {
                X = frame.LocalLocation.X,
                Y = frame.LocalLocation.Y,
                Z = frame.LocalLocation.Z
            },
            frame.MapHeadingDegrees,
            entities,
            hasValidLocalSpecies ? frame.LocalSpeciesId!.Trim() : null,
            hasValidLocalSpecies ? frame.LocalSpeciesShortName!.Trim() : null,
            DateTimeOffset.UtcNow,
            frame.PlayerSync is null
                ? null
                : new RemotePlayerSyncState(
                    frame.PlayerSync.IsSynchronizing,
                    frame.PlayerSync.VerifiedPlayers,
                    frame.PlayerSync.ProvisionalPlayers,
                    frame.PlayerSync.CandidateActors,
                    frame.PlayerSync.SpeciesEvidenceActors,
                    frame.PlayerSync.LocatedActors,
                    frame.PlayerSync.QueueDroppedPackets,
                    frame.PlayerSync.QueueDepth));
    }

    private static string UserFacingAgentFailure(Exception exception) => exception switch
    {
        ProAgentException when exception.Message.Contains(
            "unavailable",
            StringComparison.OrdinalIgnoreCase) =>
            "Không tìm thấy Pro Agent đã cài đặt.",
        ProAgentException when exception.Message.Contains(
            "connect",
            StringComparison.OrdinalIgnoreCase) =>
            "Pro Agent không kết nối được với Live Map.",
        _ => "Pro Agent đã dừng; hệ thống sẽ tự thử lại."
    };

    private static RemotePlayerCaptureHealth MapCaptureHealth(AgentCaptureStatus status) => new(
        ParseCaptureState(status.State),
        status.GameProcessFound,
        Math.Max(0, status.OwnedPortCount),
        Math.Max(0, status.OpenedAdapterCount),
        Math.Max(0, status.MatchedGamePackets),
        status.LastGamePacketAt,
        string.IsNullOrWhiteSpace(status.Message) ? null : status.Message.Trim());

    private static RemotePlayerCaptureState ParseCaptureState(string? state) =>
        state?.Trim().ToLowerInvariant() switch
        {
            "waiting-game" => RemotePlayerCaptureState.WaitingForGame,
            "waiting-port" => RemotePlayerCaptureState.WaitingForPort,
            "opening-adapters" => RemotePlayerCaptureState.OpeningAdapters,
            "capturing" => RemotePlayerCaptureState.Capturing,
            "receiving" => RemotePlayerCaptureState.Receiving,
            "faulted" => RemotePlayerCaptureState.Faulted,
            _ => RemotePlayerCaptureState.Starting
        };

    private void SetFaultedHealth(string? message) =>
        Volatile.Write(ref _captureHealth, FaultedHealth(CaptureHealth, message));

    private static RemotePlayerCaptureHealth FaultedHealth(
        RemotePlayerCaptureHealth current,
        string? message) => current with
    {
        State = RemotePlayerCaptureState.Faulted,
        Message = current.State == RemotePlayerCaptureState.Faulted
                  && !string.IsNullOrWhiteSpace(current.Message)
            ? current.Message
            : string.IsNullOrWhiteSpace(message)
                ? "Pro Agent đã dừng."
                : message.Trim()
    };

    private static bool IsValidEntity(VerifiedMapEntity entity) =>
        entity.TrackId > 0
        && Enum.IsDefined(entity.Kind)
        && Enum.IsDefined(entity.Diet)
        && (entity.MassKg is null
            || entity.MassKg is > 0d and < 100_000d
            && double.IsFinite(entity.MassKg.Value))
        && (entity.Kind == MapEntityKind.Ai
            && HasValidSpecies(entity)
            || entity.Kind == MapEntityKind.Player
            && (entity.IsProvisional
                && !HasValidPlayerProof(entity)
                && HasValidSpecies(entity)
                || !entity.IsProvisional
                && HasValidPlayerProof(entity)
                && HasValidOptionalSpecies(entity)))
        && entity.ConfirmationHits > 0
        && double.IsFinite(entity.DistanceFromLocal)
        && entity.DistanceFromLocal >= 0
        && IsFinite(entity.Location);

    private static bool HasValidPlayerProof(VerifiedMapEntity entity) =>
        entity.PlayerProofName is { Length: > 0 and <= 64 }
        && !string.IsNullOrWhiteSpace(entity.PlayerProofName);

    private static bool HasValidSpecies(VerifiedMapEntity entity) =>
        HasValidSpeciesIdentity(entity.SpeciesId, entity.SpeciesShortName);

    private static bool HasValidSpeciesIdentity(
        string? speciesId,
        string? speciesShortName) =>
        speciesId is { Length: > 0 and <= 64 }
        && !string.IsNullOrWhiteSpace(speciesId)
        && speciesShortName is { Length: > 0 and <= 32 }
        && !string.IsNullOrWhiteSpace(speciesShortName);

    private static bool HasValidOptionalSpecies(VerifiedMapEntity entity) =>
        string.IsNullOrWhiteSpace(entity.SpeciesId)
        && string.IsNullOrWhiteSpace(entity.SpeciesShortName)
        || HasValidSpecies(entity);

    private static RemoteEntityKind MapKind(MapEntityKind kind) => kind switch
    {
        MapEntityKind.Player => RemoteEntityKind.Player,
        MapEntityKind.Ai => RemoteEntityKind.Ai,
        _ => throw new ProAgentException("The Pro Agent returned an invalid entity kind.")
    };

    private static CreatureDiet MapDiet(MapCreatureDiet diet) => diet switch
    {
        MapCreatureDiet.Carnivore => CreatureDiet.Carnivore,
        MapCreatureDiet.Herbivore => CreatureDiet.Herbivore,
        MapCreatureDiet.Omnivore => CreatureDiet.Omnivore,
        _ => CreatureDiet.Unknown
    };

    private static bool IsFinite(WorldPosition position) =>
        double.IsFinite(position.X) &&
        double.IsFinite(position.Y) &&
        (position.Z is null || double.IsFinite(position.Z.Value));

    private static void StopAgent(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(2_000);
            }
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
        }
    }
}
