using System.Net.Http;
using System.Windows;
using TheIsleOverlay.LocalTelemetry;
using TheIsleOverlay.Origin;

namespace TheIsleOverlay.App;

public partial class HomeWindow
{
    private bool _originConnecting;

    private async void OriginStatsButton_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureMapLaunchAvailable()
            || _originConnecting
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

        _originConnecting = true;
        OriginStatsButton.Content = "ORIGIN x5 · ĐANG KẾT NỐI…";
        RefreshMapLaunchControls();
        try
        {
            var source = TelemetrySourceDefinition.Origin;
            var loginWindow = new LoginWindow(
                source,
                ValidateOriginSessionAsync)
            {
                Owner = this
            };
            if (loginWindow.ShowDialog() != true
                || string.IsNullOrWhiteSpace(loginWindow.CookieValue))
            {
                SourceStatusLabel.Text =
                    "Chưa nhận được phiên Origin. Hãy đăng nhập Steam trên playorigin.gg rồi thử lại.";
                return;
            }

            SourceStatusLabel.Text =
                "ĐANG TÌM DINO TRÊN MAIN ORIGIN VÀ VOICE CHAT SERVER…";
            var originSession = new OriginStatsSession(
                new OriginStatsClient(loginWindow.CookieValue));
            try
            {
                var overlay = new MainWindow(
                    new LocalPositionTelemetrySession(
                        originSession,
                        App.CurrentApp.TakeLocalTelemetrySource(),
                        "ORIGIN x5",
                        TakeProPlayerSource()),
                    "ORIGIN x5",
                    ProFeatureAccessGrant.FromSnapshot(
                        _proAccess,
                        DateTimeOffset.UtcNow));
                Application.Current.MainWindow = overlay;
                overlay.Show();
                Close();
            }
            catch
            {
                await originSession.DisposeAsync();
                throw;
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            SourceStatusLabel.Text = $"Không kết nối được Origin stats: {FriendlyError(exception)}";
        }
        finally
        {
            _originConnecting = false;
            if (OriginStatsButton is not null)
            {
                OriginStatsButton.Content = "ORIGIN x5 · STATS + PRIME";
            }

            RefreshMapLaunchControls();
        }
    }

    private static async Task<LoginSessionValidationState> ValidateOriginSessionAsync(
        string cookieHeader,
        CancellationToken cancellationToken)
    {
        using var client = new HttpClient(new HttpClientHandler
        {
            AllowAutoRedirect = false
        })
        {
            Timeout = TimeSpan.FromSeconds(8)
        };
        return await OriginAuthService.ValidateAsync(client, cookieHeader, cancellationToken)
            switch
            {
                OriginAuthValidationState.Valid => LoginSessionValidationState.Valid,
                OriginAuthValidationState.Invalid => LoginSessionValidationState.Invalid,
                _ => LoginSessionValidationState.Unavailable
            };
    }
}
