using System.Net;
using IsleLiveMap.Activation;
using TheIsleOverlay.Core;

namespace TheIsleOverlay.ProClient;

public sealed class ProAccessService : IDisposable
{
    private readonly object _stateGate = new();
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly ProApiClient _apiClient;
    private readonly ProCredentialStore _credentialStore;
    private readonly DeviceIdentityStore _deviceIdentityStore;
    private readonly KeyLeaseStore _keyLeaseStore;
    private readonly string? _localAgentPath;
#if DEBUG
    private readonly bool _localDevelopmentEnabled;
    private bool _localDevelopmentActive;
#endif
    private StoredKeyLease? _keyLease;
    private readonly ProReleaseManager _releaseManager;
    private readonly TimeProvider _timeProvider;
    private StoredProSession? _session;
    private ProAgentInstallation? _installation;
    private ProAccessSnapshot _current = ProAccessSnapshot.SignedOut;
    private int _disposed;

    public ProAccessService(
        ProClientOptions? options = null,
        HttpClient? httpClient = null,
        TimeProvider? timeProvider = null,
        string? updatePublicKeyPem = null)
    {
        options ??= new ProClientOptions();
        _httpClient = httpClient ?? ProHttpTransport.Create();
        _ownsHttpClient = httpClient is null;
        _apiClient = new ProApiClient(_httpClient, options.BaseUri);
        _credentialStore = new ProCredentialStore(options.CredentialPath);
        _deviceIdentityStore = new DeviceIdentityStore(options.CredentialPath + ".device-v1");
        _keyLeaseStore = new KeyLeaseStore(options.CredentialPath + ".key-lease-v1");
        _localAgentPath = string.IsNullOrWhiteSpace(options.LocalAgentPath)
            ? null
            : Path.GetFullPath(options.LocalAgentPath);
#if DEBUG
        _localDevelopmentEnabled = options.EnableLocalDevelopment && _localAgentPath is not null;
#endif
        _releaseManager = new ProReleaseManager(
            _apiClient,
            options.InstallationRoot,
            updatePublicKeyPem);
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public ProAccessSnapshot Current
    {
        get
        {
            lock (_stateGate)
            {
                return _current;
            }
        }
    }

    public ProLoginAttempt CreateLoginAttempt() => _apiClient.CreateLoginAttempt();

    public async Task<ProAccessSnapshot> InitializeAsync(
        string hostVersion,
        CancellationToken cancellationToken = default)
    {
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            ValidateHostVersion(hostVersion);
#if DEBUG
            if (_localDevelopmentEnabled)
            {
                return await InitializeLocalDevelopmentAsync(hostVersion, cancellationToken)
                    .ConfigureAwait(false);
            }
#endif
            var lease = await _keyLeaseStore.LoadAsync(cancellationToken).ConfigureAwait(false);
            if (lease is null)
            {
                _keyLeaseStore.Clear();
                return SetState(null, null, ProAccessSnapshot.SignedOut);
            }
            try
            {
                var status = await _apiClient.GetLeaseStatusAsync(lease.LeaseToken, cancellationToken)
                    .ConfigureAwait(false);
                if (!status.Active || status.ExpiresAt <= status.ServerTime || lease.ExpiresAt <= _timeProvider.GetUtcNow())
                {
                    _keyLeaseStore.Clear();
                    return SetState(null, null, ProAccessSnapshot.SignedOut with { StatusCode = "lease_expired" });
                }
            }
            catch (ProApiException exception) when (exception.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                _keyLeaseStore.Clear();
                return SetState(null, null, ProAccessSnapshot.SignedOut with { StatusCode = "lease_revoked" });
            }
            var (snapshot, installation) = await VerifyKeyLeaseAsync(
                    lease, hostVersion, cancellationToken)
                .ConfigureAwait(false);
            return SetKeyLeaseState(lease, installation, snapshot, hostVersion);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task<ProAccessSnapshot> CompleteLoginAsync(
        ProLoginAttempt attempt,
        string callbackUri,
        string hostVersion,
        CancellationToken cancellationToken = default)
    {
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            var tokens = await _apiClient.ExchangeAsync(attempt, callbackUri, cancellationToken)
                .ConfigureAwait(false);
            var account = await _apiClient.GetEntitlementAsync(tokens.AccessToken, cancellationToken)
                .ConfigureAwait(false);
            var normalized = tokens with
            {
                Entitlement = account.Entitlement,
                OfflineLicenseToken = account.OfflineLicenseToken,
                OfflineLicenseExpiresAt = account.OfflineLicenseExpiresAt
            };
            return await ApplyOnlineTokensAsync(
                    account.SteamId64,
                    normalized,
                    hostVersion,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task<ProAccessSnapshot> ActivateKeyAsync(
        string key, string hostVersion, CancellationToken cancellationToken = default)
    {
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            ValidateHostVersion(hostVersion);
            if (!LocalProKeyStore.Accepts(key)) throw new ArgumentException("The activation key is invalid.", nameof(key));
            key = key.Trim();
            using var identity = await _deviceIdentityStore.LoadOrCreateAsync(cancellationToken).ConfigureAwait(false);
            var activation = await _apiClient.ActivateKeyAsync(key, identity, cancellationToken).ConfigureAwait(false);
            var lease = new StoredKeyLease(activation.LeaseToken, activation.ActivationId, activation.ExpiresAt);
            try
            {
                // The backend consumes a one-time code before the Agent is downloaded or probed.
                // Persist the returned lease first so a later Agent failure never loses the activation.
                await _keyLeaseStore.SaveAsync(lease, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                System.Security.Cryptography.CryptographicException)
            {
                throw new ProActivationPersistenceException(
                    "The key was accepted, but the activation could not be stored on this device.", exception);
            }
            var (snapshot, installation) = await VerifyKeyLeaseAsync(
                    lease, hostVersion, cancellationToken)
                .ConfigureAwait(false);
            return SetKeyLeaseState(lease, installation, snapshot, hostVersion);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    private async Task<(ProAccessSnapshot Snapshot, ProAgentInstallation? Installation)> VerifyKeyLeaseAsync(
        StoredKeyLease lease, string hostVersion, CancellationToken cancellationToken)
    {
        string? version = null;
        var status = "local_agent_unavailable";
        ProAgentInstallation? installation = null;
        var agentPath = _localAgentPath;
        try
        {
            if (agentPath is null)
            {
                installation = await _releaseManager.EnsureAvailableAsync(
                        hostVersion,
                        lease.LeaseToken,
                        cancellationToken)
                    .ConfigureAwait(false);
                agentPath = installation.ExecutablePath;
            }

            if (File.Exists(agentPath))
            {
                await using var source = ProAgentRemotePlayerSource.ForDeviceLease(
                    agentPath, hostVersion, lease.ActivationId, lease.LeaseToken);
                version = await source.ProbeAsync(cancellationToken).ConfigureAwait(false);
                status = "local_key_active";
            }
        }
        catch (Exception exception) when (exception is ProAgentException or ProApiException or IOException or
            InvalidDataException or UnauthorizedAccessException or
            System.ComponentModel.Win32Exception)
        {
            status = exception switch
            {
                ProAgentException => "local_agent_rejected",
                ProApiException { StatusCode: HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden } =>
                    "local_key_rejected",
                _ => "local_agent_unavailable"
            };
        }

        var snapshot = new ProAccessSnapshot(lease.ActivationId,
            version is not null ? new ProEntitlement("pro", "active", lease.ExpiresAt) : ProAccessSnapshot.SignedOut.Entitlement,
            false, version is not null, version, lease.ExpiresAt, status, lease.LeaseToken);
        return (snapshot, installation);
    }

    private ProAccessSnapshot SetKeyLeaseState(
        StoredKeyLease lease,
        ProAgentInstallation? installation,
        ProAccessSnapshot snapshot,
        string hostVersion)
    {
        lock (_stateGate)
        {
            var result = SetState(null, installation, snapshot, hostVersion);
            _keyLease = snapshot.AgentReady ? lease : null;
            return result;
        }
    }
    public async Task LogoutAsync(CancellationToken cancellationToken = default)
    {
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            _credentialStore.Clear();
            _keyLeaseStore.Clear();
            SetState(null, null, ProAccessSnapshot.SignedOut);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public IRemotePlayerTelemetrySource? CreateRemotePlayerSource()
    {
        lock (_stateGate)
        {
#if DEBUG
            if (_localDevelopmentActive && _localAgentPath is not null)
            {
                return ProAgentRemotePlayerSource.ForLocalDevelopment(
                    _localAgentPath,
                    _currentHostVersion);
            }
#endif
            if (_current.StatusCode == "local_key_active" && _current.AgentReady && _current.IsPro && _keyLease is not null)
            {
                var agentPath = _installation?.ExecutablePath ?? _localAgentPath;
                return agentPath is null
                    ? null
                    : ProAgentRemotePlayerSource.ForDeviceLease(
                        agentPath, _currentHostVersion, _keyLease.ActivationId, _keyLease.LeaseToken);
            }
            if (_session is null ||
                _installation is null ||
                !_current.Entitlement.IsProAt(_timeProvider.GetUtcNow()) ||
                !_session.HasUsableOfflineLicense(_timeProvider.GetUtcNow()))
            {
                return null;
            }

            return new ProAgentRemotePlayerSource(
                _installation.ExecutablePath,
                _currentHostVersion,
                _session.SteamId64,
                _session.OfflineLicenseToken!);
        }
    }

    private string _currentHostVersion = "0.0.0";

#if DEBUG
    private async Task<ProAccessSnapshot> InitializeLocalDevelopmentAsync(
        string hostVersion,
        CancellationToken cancellationToken)
    {
        if (_localAgentPath is null || !File.Exists(_localAgentPath))
        {
            return SetState(null, null, ProAccessSnapshot.SignedOut with
            {
                StatusCode = "local_dev_agent_unavailable"
            }, hostVersion);
        }

        try
        {
            await using var source = ProAgentRemotePlayerSource.ForLocalDevelopment(
                _localAgentPath,
                hostVersion);
            var version = await source.ProbeAsync(cancellationToken).ConfigureAwait(false);
            var snapshot = new ProAccessSnapshot(
                LocalProActivation.DevelopmentIdentity,
                new ProEntitlement("pro", "active", null),
                true,
                true,
                version,
                null,
                "local_dev_active");
            lock (_stateGate)
            {
                _keyLease = null;
                _session = null;
                _installation = null;
                _localDevelopmentActive = true;
                _currentHostVersion = hostVersion;
                _current = snapshot;
                return snapshot;
            }
        }
        catch (Exception exception) when (exception is ProAgentException or IOException or
            InvalidDataException or UnauthorizedAccessException or
            System.ComponentModel.Win32Exception)
        {
            return SetState(null, null, ProAccessSnapshot.SignedOut with
            {
                StatusCode = "local_dev_agent_rejected"
            }, hostVersion);
        }
    }
#endif

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _operationGate.Dispose();
        _releaseManager.Dispose();
        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }
    }

    private async Task<ProAccessSnapshot> RefreshStoredSessionAsync(
        StoredProSession stored,
        string hostVersion,
        CancellationToken cancellationToken)
    {
        try
        {
            if (stored.RefreshTokenExpiresAt <= _timeProvider.GetUtcNow())
            {
                throw new ProApiException("The saved Steam session has expired.", HttpStatusCode.BadRequest);
            }

            var tokens = await _apiClient.RefreshAsync(stored.RefreshToken, cancellationToken)
                .ConfigureAwait(false);
            return await ApplyOnlineTokensAsync(
                    stored.SteamId64,
                    tokens,
                    hostVersion,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (ProApiException exception) when (
            exception.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized)
        {
            _credentialStore.Clear();
            return SetState(null, null, ProAccessSnapshot.SignedOut with
            {
                StatusCode = "session_expired"
            });
        }
        catch (ProApiException)
        {
            return await ApplyOfflineFallbackAsync(stored, hostVersion, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private async Task<ProAccessSnapshot> ApplyOnlineTokensAsync(
        string steamId64,
        ProTokenResponse tokens,
        string hostVersion,
        CancellationToken cancellationToken)
    {
        ValidateHostVersion(hostVersion);
        var stored = new StoredProSession(
            steamId64,
            tokens.RefreshToken,
            tokens.RefreshTokenExpiresAt,
            tokens.OfflineLicenseToken,
            tokens.OfflineLicenseExpiresAt,
            tokens.Entitlement);
        await _credentialStore.SaveAsync(stored, cancellationToken).ConfigureAwait(false);

        ProAgentInstallation? installation = null;
        string? statusCode = null;
        if (stored.HasUsableOfflineLicense(_timeProvider.GetUtcNow()))
        {
            try
            {
                installation = await _releaseManager.EnsureAvailableAsync(
                        hostVersion,
                        tokens.AccessToken,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception exception) when (
                exception is ProApiException or IOException or InvalidDataException or UnauthorizedAccessException)
            {
                installation = await _releaseManager.LoadInstalledAsync(hostVersion, cancellationToken)
                    .ConfigureAwait(false);
                statusCode = installation is null ? "agent_unavailable" : "agent_update_unavailable";
            }
        }

        var snapshot = BuildSnapshot(
            stored,
            installation,
            isOffline: false,
            statusCode);
        return SetState(stored, installation, snapshot, hostVersion);
    }

    private async Task<ProAccessSnapshot> ApplyOfflineFallbackAsync(
        StoredProSession stored,
        string hostVersion,
        CancellationToken cancellationToken)
    {
        ValidateHostVersion(hostVersion);
        var installation = stored.HasUsableOfflineLicense(_timeProvider.GetUtcNow())
            ? await _releaseManager.LoadInstalledAsync(hostVersion, cancellationToken).ConfigureAwait(false)
            : null;
        var statusCode = installation is not null
            ? "offline_license"
            : stored.Entitlement.IsPro
                ? "offline_agent_unavailable"
                : "license_service_unavailable";
        var snapshot = BuildSnapshot(stored, installation, isOffline: true, statusCode);
        return SetState(stored, installation, snapshot, hostVersion);
    }

    private static ProAccessSnapshot BuildSnapshot(
        StoredProSession session,
        ProAgentInstallation? installation,
        bool isOffline,
        string? statusCode) => new(
        session.SteamId64,
        session.Entitlement,
        isOffline,
        installation is not null,
        installation?.Version,
        session.OfflineLicenseExpiresAt,
        statusCode,
        session.OfflineLicenseToken);

    private ProAccessSnapshot SetState(
        StoredProSession? session,
        ProAgentInstallation? installation,
        ProAccessSnapshot snapshot,
        string? hostVersion = null)
    {
        lock (_stateGate)
        {
#if DEBUG
            _localDevelopmentActive = false;
#endif
            _keyLease = null;
            _session = session;
            _installation = installation;
            _current = snapshot;
            if (hostVersion is not null)
            {
                _currentHostVersion = hostVersion;
            }

            return snapshot;
        }
    }

    private static void ValidateHostVersion(string hostVersion)
    {
        if (!SemanticVersion.TryParse(hostVersion, out _))
        {
            throw new ArgumentException("The host version is invalid.", nameof(hostVersion));
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed != 0, this);
}
