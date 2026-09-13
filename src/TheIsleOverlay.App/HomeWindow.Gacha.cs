using System.Net.Http;
using System.Windows;
using TheIsleOverlay.Core;
using TheIsleOverlay.Gacha;
using TheIsleOverlay.LocalTelemetry;

namespace TheIsleOverlay.App;

public partial class HomeWindow
{
    private readonly GachaOverlayCredentialStore _gachaCredentialStore = new(
        AppPaths.GachaOverlayCredential);
    private GachaOverlayCredentials? _gachaCredentials;
    private bool _gachaConnecting;

    private async Task InitializeGachaCredentialsAsync()
    {
        if (_gachaCredentials is not null)
        {
            ApplyGachaLoginState();
            return;
        }

        try
        {
            _gachaCredentials = await _gachaCredentialStore.LoadAsync(_shutdown.Token);
            if (_gachaCredentials is { } stored
                && stored.RefreshExpiresAt is { } refreshExpiry
                && refreshExpiry <= DateTimeOffset.UtcNow)
            {
                _gachaCredentialStore.Clear();
                _gachaCredentials = null;
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
            return;
        }
        catch
        {
            _gachaCredentials = null;
        }

        ApplyGachaLoginState();
    }

    private async void GachaStatsButton_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureMapLaunchAvailable()
            || _gachaConnecting
            || _islePilotConnecting
            || _connecting)
        {
            return;
        }

        if (!EnsureLocalCaptureAvailable())
        {
            return;
        }

        _gachaConnecting = true;
        ApplyGachaLoginState();
        try
        {
            await InitializeGachaCredentialsAsync();
            var credentials = _gachaCredentials;
            if (credentials is not null)
            {
                GachaStatsStatusLabel.Text = "GACHA · ĐANG XÁC MINH PHIÊN…";
                using var validationClient = CreateGachaHttpClient();
                var validation = await GachaOverlayAuthService.ValidateAsync(
                    validationClient,
                    credentials,
                    _shutdown.Token);
                if (validation == GachaOverlayAuthValidationState.Invalid
                    && credentials.CanRefresh)
                {
                    try
                    {
                        credentials = await GachaOverlayAuthService.RefreshAsync(
                            validationClient,
                            credentials,
                            _shutdown.Token);
                        await _gachaCredentialStore.SaveAsync(credentials, _shutdown.Token);
                        _gachaCredentials = credentials;
                        validation = GachaOverlayAuthValidationState.Valid;
                    }
                    catch (GachaOverlayAuthenticationException)
                    {
                        validation = GachaOverlayAuthValidationState.Invalid;
                    }
                }

                if (validation == GachaOverlayAuthValidationState.Invalid)
                {
                    _gachaCredentialStore.Clear();
                    _gachaCredentials = null;
                    credentials = null;
                }
            }

            if (credentials is null)
            {
                var loginWindow = new GachaSteamLoginWindow { Owner = this };
                if (loginWindow.ShowDialog() != true || loginWindow.Credentials is null)
                {
                    GachaStatsStatusLabel.Text = "Gacha chưa đăng nhập; Live Map vẫn dùng nguồn mặc định.";
                    return;
                }

                credentials = loginWindow.Credentials;
                using var validationClient = CreateGachaHttpClient();
                var validation = await GachaOverlayAuthService.ValidateAsync(
                    validationClient,
                    credentials,
                    _shutdown.Token);
                if (validation == GachaOverlayAuthValidationState.Invalid)
                {
                    GachaStatsStatusLabel.Text = "Gacha từ chối phiên vừa đăng nhập. Hãy thử lại.";
                    return;
                }

                await _gachaCredentialStore.SaveAsync(credentials, _shutdown.Token);
                _gachaCredentials = credentials;
            }

            await OpenGachaOverlayAsync(credentials);
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            GachaStatsStatusLabel.Text = $"Không kết nối được Gacha stats: {FriendlyError(exception)}";
        }
        finally
        {
            _gachaConnecting = false;
            ApplyGachaLoginState();
            RefreshMapLaunchControls();
        }
    }

    private void LogoutGachaButton_Click(object sender, RoutedEventArgs e)
    {
        if (_gachaConnecting)
        {
            return;
        }

        _gachaCredentialStore.Clear();
        _gachaCredentials = null;
        GachaStatsStatusLabel.Text = "Đã xóa phiên Gacha trên máy này.";
        ApplyGachaLoginState();
    }

    private async Task OpenGachaOverlayAsync(GachaOverlayCredentials credentials)
    {
        var options = new GachaOverlayOptions();
        var client = new GachaOverlayClient(
            credentials,
            options,
            credentialsRefreshed: async (refreshed, cancellationToken) =>
            {
                await _gachaCredentialStore.SaveAsync(refreshed, cancellationToken)
                    .ConfigureAwait(false);
                _gachaCredentials = refreshed;
            });
        var statsSession = new GachaStatsSession(
            client,
            new GachaStatsReducer(options.LiveDataLifetime, options.ApiDataLifetime));
        var remoteSession = new GachaTelemetrySession(statsSession);
        try
        {
            var overlay = new MainWindow(
                new LocalPositionTelemetrySession(
                    remoteSession,
                    App.CurrentApp.TakeLocalTelemetrySource(),
                    "GACHA",
                    TakeProPlayerSource()),
                "GACHA",
                ProFeatureAccessGrant.FromSnapshot(_proAccess, DateTimeOffset.UtcNow));
            Application.Current.MainWindow = overlay;
            overlay.Show();
            Close();
        }
        catch
        {
            await remoteSession.DisposeAsync();
            throw;
        }
    }

    private void ApplyGachaLoginState()
    {
        if (!IsLoaded || GachaStatsButton is null)
        {
            return;
        }

        var authenticated = _gachaCredentials is not null;
        GachaStatsButton.Content = _gachaConnecting
            ? "GACHA STATS · ĐANG KẾT NỐI…"
            : authenticated
                ? "GACHA STATS · MỞ MAP"
                : "GACHA STATS · KẾT NỐI";
        GachaStatsButton.IsEnabled = !_gachaConnecting
                                     && !_connecting
                                     && !_islePilotConnecting
                                     && MapLaunchGatePolicy.AllowsMap(_mapLaunchGateState);
        LogoutGachaButton.Visibility = authenticated
            ? Visibility.Visible
            : Visibility.Collapsed;
        LogoutGachaButton.IsEnabled = !_gachaConnecting;
        if (authenticated && !_gachaConnecting)
        {
            GachaStatsStatusLabel.Text =
                $"Gacha đã xác minh Steam ••••{_gachaCredentials!.SteamId![^4..]}; bấm GACHA STATS để mở map.";
        }
    }

    private static HttpClient CreateGachaHttpClient() => new(
        new HttpClientHandler { AllowAutoRedirect = false })
    {
        Timeout = TimeSpan.FromSeconds(8)
    };
}
