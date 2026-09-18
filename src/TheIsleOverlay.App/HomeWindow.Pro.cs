using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using TheIsleOverlay.Core;
using TheIsleOverlay.ProClient;

namespace TheIsleOverlay.App;

public partial class HomeWindow
{
    private readonly ProAccessService _proAccessService = new();
    private ProAccessSnapshot _proAccess = ProAccessSnapshot.SignedOut;
    private bool _proAccessLoading;
    private bool _proAccessInitialized;
    private Task<ProAccessSnapshot>? _proAccessInitializationTask;
    private PrewarmedRemotePlayerTelemetrySource? _warmProTelemetry;
    private bool _premiumHomeTheme;
    private DispatcherTimer? _proExpiryTimer;

    private void InitializeProPresentation()
    {
        _proExpiryTimer = new DispatcherTimer(DispatcherPriority.Background);
        _proExpiryTimer.Tick += ProExpiryTimer_Tick;
        ApplyHomePresentationTheme(premium: false);
    }

    private void StopProPresentationMonitoring()
    {
        if (_proExpiryTimer is null)
        {
            return;
        }

        _proExpiryTimer.Stop();
        _proExpiryTimer.Tick -= ProExpiryTimer_Tick;
    }

    private async void ProAccessPanel_Loaded(object sender, RoutedEventArgs e)
    {
        await EnsureProAccessInitializedAsync();
    }

    private Task<ProAccessSnapshot> EnsureProAccessInitializedAsync()
    {
        _proAccessInitializationTask ??= InitializeProAccessAsync();
        return _proAccessInitializationTask;
    }

    private async Task<ProAccessSnapshot> InitializeProAccessAsync()
    {
        _proAccessInitialized = true;
        _proAccessLoading = true;
        ApplyProAccessState(ProAccessSnapshot.SignedOut with { StatusCode = "checking" });
        RefreshMapLaunchControls();
        try
        {
            _proAccess = await _proAccessService.InitializeAsync(
                CurrentVersion(),
                _shutdown.Token);
            ApplyProAccessState(_proAccess);
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
        }
        catch (Exception)
        {
            _proAccess = ProAccessSnapshot.SignedOut with
            {
                IsOffline = true,
                StatusCode = "license_service_unavailable"
            };
            ApplyProAccessState(_proAccess);
        }
        finally
        {
            _proAccessLoading = false;
            await RefreshProTelemetryWarmupAsync();
            RefreshMapLaunchControls();
        }

        return _proAccess;
    }

    private async void ProAccessButton_Click(object sender, RoutedEventArgs e)
    {
        var presentation = HomeProPresentationPolicy.Evaluate(
            _proAccess,
            DateTimeOffset.UtcNow);
        if (_proAccessLoading
            || _connecting
            || _islePilotConnecting
            || presentation.IsVerified)
        {
            return;
        }

        var loginWindow = new ProKeyActivationWindow(_proAccessService, CurrentVersion())
        {
            Owner = this
        };
        loginWindow.ShowDialog();
        if (loginWindow.Access is { } access)
        {
            _proAccess = access;
            ApplyProAccessState(access);
            await RefreshProTelemetryWarmupAsync();
            SourceStatusLabel.Text = access.AgentReady ? "Key đã xác minh. Pro Agent đã kết nối; đang chờ dữ liệu game." : "Key đã lưu nhưng chưa kết nối được Pro Agent. Bấm NHẬP KEY để thử lại.";
        }

        RefreshMapLaunchControls();
    }

    private async void LogoutProButton_Click(object sender, RoutedEventArgs e)
    {
        if (_proAccessLoading)
        {
            return;
        }

        _proAccessLoading = true;
        RefreshMapLaunchControls();
        try
        {
            await StopProTelemetryWarmupAsync();
            await _proAccessService.LogoutAsync(_shutdown.Token);
            _islePilotVoiceCredentialStore.Clear();
            _proAccess = ProAccessSnapshot.SignedOut;
            ApplyProAccessState(_proAccess);
            SourceStatusLabel.Text = "Đã xóa phiên Isle Live Map Pro trên máy này.";
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
        }
        finally
        {
            _proAccessLoading = false;
            RefreshMapLaunchControls();
        }
    }

    private IRemotePlayerTelemetrySource? TakeProPlayerSource()
    {
        if (_warmProTelemetry is not null)
        {
            var source = _warmProTelemetry;
            _warmProTelemetry = null;
            return source;
        }

        return _proAccessService.CreateRemotePlayerSource();
    }

    private Task RefreshProTelemetryWarmupAsync()
    {
        var presentation = HomeProPresentationPolicy.Evaluate(
            _proAccess,
            DateTimeOffset.UtcNow);
        if (presentation.IsVerified)
        {
            if (_warmProTelemetry is null
                && _proAccessService.CreateRemotePlayerSource() is { } source)
            {
                _warmProTelemetry = new PrewarmedRemotePlayerTelemetrySource(source);
                _warmProTelemetry.Start();
            }

            return Task.CompletedTask;
        }

        return StopProTelemetryWarmupAsync();
    }

    private async Task StopProTelemetryWarmupAsync()
    {
        if (_warmProTelemetry is not { } source)
        {
            return;
        }

        _warmProTelemetry = null;
        await source.DisposeAsync();
    }

    private void ApplyProAccessState(ProAccessSnapshot access)
    {
        var now = DateTimeOffset.UtcNow;
        var presentation = HomeProPresentationPolicy.Evaluate(access, now);
        ApplyHomePresentationTheme(presentation.HasCurrentProAccess);
        ScheduleProExpiryRefresh(access, presentation, now);

        var checking = string.Equals(access.StatusCode, "checking", StringComparison.Ordinal);
        if (checking)
        {
            ProTierLabel.Text = "ACCESS / CHECKING";
            ProAccountLabel.Text = "  ·  ĐANG ĐỌC PHIÊN ĐÃ LƯU";
            ProAccessDetailLabel.Text = "Đang kiểm tra quyền và phiên bản Pro Agent…";
            ProAccessActionLabel.Text = "ĐỢI…";
            ProAccessStateBar.Fill = (Brush)FindResource("HomeAccent");
            ProAccessFootnoteLabel.Text = "Đang đọc quyền đã lưu an toàn trên thiết bị này.";
            LogoutProButton.Visibility = Visibility.Collapsed;
            return;
        }

        ProAccountLabel.Text = access.IsPro ? "  ·  KEY ĐÃ KÍCH HOẠT" : "  ·  CHƯA NHẬP KEY";
        LogoutProButton.Visibility = access.IsAuthenticated || access.IsPro
            ? Visibility.Visible
            : Visibility.Collapsed;

        if (access.StatusCode is "local_agent_unavailable" or "local_agent_rejected" or "local_key_rejected")
        {
            ProTierLabel.Text = "PRO / KEY ĐÃ LƯU";
            ProAccessDetailLabel.Text = "Chưa kết nối được Pro Agent; bấm để thử lại";
            ProAccessActionLabel.Text = "THỬ LẠI →";
            ProAccessStateBar.Fill = (Brush)FindResource("HomeAccent");
            ProAccessFootnoteLabel.Text = "Không tải hoặc khởi động được Pro Agent; hãy kiểm tra mạng rồi thử lại.";
            SourceStatusLabel.Text = "Key đã lưu; Pro Agent chưa kết nối được.";
            return;
        }
        if (presentation.IsVerified)
        {
            ProTierLabel.Text = access.IsOffline ? "PRO / OFFLINE LICENSE" : "PRO / ACTIVE";
            ProAccessDetailLabel.Text = access.Entitlement.ExpiresAt is { } activeUntil
                ? $"Player + AI Tracking · hết hạn {activeUntil.ToLocalTime():dd/MM/yyyy HH:mm}"
                : "Player + AI Tracking · quyền vĩnh viễn";
            ProAccessActionLabel.Text = "ĐÃ XÁC MINH  ✓";
            ProAccessStateBar.Fill = (Brush)FindResource("HomeAccent");
            ProAccessFootnoteLabel.Text = "Quyền Pro đã sẵn sàng: player, AI, loài và cân nặng được ghép vào Live Map.";
            SourceStatusLabel.Text = access.IsOffline
                ? "Pro đang dùng giấy phép offline còn hiệu lực. Player + AI Tracking đã sẵn sàng."
                : "Pro đã xác minh. Player + AI Tracking đã sẵn sàng trên mọi server.";
            return;
        }

        if (presentation.HasCurrentProAccess)
        {
            ProTierLabel.Text = "PRO / AGENT CHƯA SẴN SÀNG";
            ProAccessDetailLabel.Text = access.IsOffline
                ? "Không có mạng và chưa có Pro Agent tương thích trên máy"
                : "Chưa tải được Pro Agent tương thích; đăng xuất rồi đăng nhập lại để thử lại";
            ProAccessActionLabel.Text = "AGENT CHƯA SẴN SÀNG";
            ProAccessStateBar.Fill = (Brush)FindResource("HomeAccent");
            ProAccessFootnoteLabel.Text = "Quyền Pro còn hiệu lực; Agent cần hoàn tất trước khi ghép player và AI vào map.";
            SourceStatusLabel.Text = "Tài khoản có Pro nhưng Agent tương thích chưa sẵn sàng.";
            return;
        }

        ProTierLabel.Text = access.IsAuthenticated ? "FREE / VERIFIED" : "FREE / PUBLIC";
        var entitlementExpired = access.Entitlement.ExpiresAt is { } expiresAt
                                 && expiresAt <= now;
        ProAccessDetailLabel.Text = entitlementExpired
            ? "Quyền Pro đã hết hạn; Home đã trở về chế độ Free"
            : access.StatusCode switch
        {
            "session_expired" => "Phiên Steam đã hết hạn; đăng nhập lại để kiểm tra quyền",
            "license_service_unavailable" => "Chưa kết nối được dịch vụ cấp phép; Free vẫn hoạt động",
            _ when access.IsAuthenticated => "Tài khoản này chưa được cấp Isle Live Map Pro",
            _ => "Nhập key để kích hoạt Pro trên máy này"
        };
        ProAccessActionLabel.Text = "NHẬP KEY  →";
        ProAccessStateBar.Fill = new SolidColorBrush(Color.FromRgb(111, 109, 85));
        ProAccessFootnoteLabel.Text = "Lấy key Pro miễn phí để mở player, AI, Live Skin, Garage và Teleport. Free luôn hoạt động độc lập.";
        SourceStatusLabel.Text = entitlementExpired
            ? "Quyền Pro đã hết hạn. Free vẫn sẵn sàng trên mọi server."
            : "Free đã sẵn sàng cho mọi server. Pro chỉ được ghép thêm sau khi xác minh quyền.";
    }

    private void ScheduleProExpiryRefresh(
        ProAccessSnapshot access,
        HomeProPresentationState presentation,
        DateTimeOffset now)
    {
        if (_proExpiryTimer is null)
        {
            return;
        }

        _proExpiryTimer.Stop();
        if (!presentation.HasCurrentProAccess
            || access.Entitlement.ExpiresAt is not { } expiresAt
            || expiresAt <= now)
        {
            return;
        }

        var untilExpiry = expiresAt - now;
        _proExpiryTimer.Interval = untilExpiry < TimeSpan.FromSeconds(30)
            ? TimeSpan.FromMilliseconds(Math.Max(250, untilExpiry.TotalMilliseconds + 100))
            : TimeSpan.FromSeconds(30);
        _proExpiryTimer.Start();
    }

    private async void ProExpiryTimer_Tick(object? sender, EventArgs e)
    {
        var now = DateTimeOffset.UtcNow;
        if (_proAccess.Entitlement.ExpiresAt is not { } expiresAt)
        {
            _proExpiryTimer?.Stop();
            return;
        }

        if (expiresAt > now)
        {
            ScheduleProExpiryRefresh(
                _proAccess,
                HomeProPresentationPolicy.Evaluate(_proAccess, now),
                now);
            return;
        }

        _proExpiryTimer?.Stop();
        await StopProTelemetryWarmupAsync();
        ApplyProAccessState(_proAccess);
        RefreshMapLaunchControls();
        SourceStatusLabel.Text = "Quyền Pro đã hết hạn. Home đã tự chuyển về giao diện Free.";
        ShowProPromotionIfNeeded();
    }

    private void ApplyHomePresentationTheme(bool premium)
    {
        _premiumHomeTheme = premium;
        var palette = premium
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["HomeSurface"] = "#F20A060C",
                ["HomePanel"] = "#D00F0815",
                ["HomePanelSoft"] = "#B813081A",
                ["HomeInputSurface"] = "#AD0D0710",
                ["HomeBone"] = "#F3D8FF",
                ["HomeMuted"] = "#B684C9",
                ["HomeSubtle"] = "#7C5592",
                ["HomeLine"] = "#44205C",
                ["HomeLineStrong"] = "#67248E",
                ["HomeShellLine"] = "#8A4E3174",
                ["HomeAccent"] = "#B14CE6",
                ["HomeAccentBright"] = "#E0A0FF",
                ["HomeAccentDeep"] = "#27073B",
                ["HomeSelection"] = "#6AB14CE6",
                ["HomeButtonFill"] = "#D028083A",
                ["HomeHover"] = "#1E082B",
                ["HomePressed"] = "#300A47"
            }
            : new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["HomeSurface"] = "#F2070817",
                ["HomePanel"] = "#C50B0E24",
                ["HomePanelSoft"] = "#A50A0C21",
                ["HomeInputSurface"] = "#A5070817",
                ["HomeBone"] = "#E9EFF4",
                ["HomeMuted"] = "#9EA4B3",
                ["HomeSubtle"] = "#787F93",
                ["HomeLine"] = "#353B55",
                ["HomeLineStrong"] = "#48506C",
                ["HomeShellLine"] = "#6A585F73",
                ["HomeAccent"] = "#3745D4",
                ["HomeAccentBright"] = "#E8EDFF",
                ["HomeAccentDeep"] = "#161A3C",
                ["HomeSelection"] = "#6A3745D4",
                ["HomeButtonFill"] = "#C5191F3E",
                ["HomeHover"] = "#181D36",
                ["HomePressed"] = "#262E50"
            };

        foreach (var (key, color) in palette)
        {
            Resources[key] = HomeBrush(color);
        }

        HomeModeLabel.Text = premium ? "ISLE · PRO" : "ISLE";
        HomeClientModeLabel.Text = premium
            ? "  VERIFIED PREMIUM TELEMETRY"
            : "  OPEN TELEMETRY CLIENT";
        ProSectionHeading.Text = premium
            ? "PRO ACCESS · ĐÃ KÍCH HOẠT"
            : "KÍCH HOẠT PRO · KEY MIỄN PHÍ";
        ApplyMapLaunchAccent();
    }
}
