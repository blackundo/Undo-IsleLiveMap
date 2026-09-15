using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;

namespace TheIsleOverlay.App;

public partial class KLongServicesAdWindow : Window
{
    private const string YouTubeResourceFilter = "*://*.youtube-nocookie.com/*";
    public static readonly Uri FacebookUri = new("https://www.facebook.com/klong.dev/");
    public static readonly Uri YouTubeUri = new("https://youtu.be/8mMaXM2Y-EQ");
    public static readonly Uri YouTubeEmbedUri = new("https://www.youtube-nocookie.com/embed/8mMaXM2Y-EQ?autoplay=0&rel=0&playsinline=1");
    public static readonly Uri YouTubeWatchUri = new("https://www.youtube.com/watch?v=8mMaXM2Y-EQ&embed=1&autoplay=0&rel=0");
    public static readonly TimeSpan RequiredDisplayDuration = TimeSpan.FromSeconds(5);

    private readonly MandatoryModalDelay _closeDelay = new(RequiredDisplayDuration);
    private readonly Stopwatch _displayClock = new();
    private readonly DispatcherTimer _countdownTimer;
    private bool _allowClose;
    private bool _browserReady;
    private bool _usingWatchFallback;

    public KLongServicesAdWindow()
    {
        InitializeComponent();
        _countdownTimer = new DispatcherTimer(
            TimeSpan.FromMilliseconds(100),
            DispatcherPriority.Background,
            CountdownTimer_Tick,
            Dispatcher);
        _countdownTimer.Stop();
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        _displayClock.Restart();
        _countdownTimer.Start();
        UpdateCountdown();

        if (!SystemParameters.ClientAreaAnimation)
        {
            AdContent.Opacity = 1d;
            AdEntranceTransform.Y = 0d;
        }
        else
        {
            AdContent.BeginAnimation(OpacityProperty, new DoubleAnimation(0d, 1d, TimeSpan.FromMilliseconds(220)));
            AdEntranceTransform.BeginAnimation(
                System.Windows.Media.TranslateTransform.YProperty,
                new DoubleAnimation(10d, 0d, TimeSpan.FromMilliseconds(260))
                {
                    EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
                });
        }

        await InitializeVideoAsync();
    }

    private async Task InitializeVideoAsync()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.KLongServicesWebView2Profile);
            var environment = await CoreWebView2Environment.CreateAsync(
                browserExecutableFolder: null,
                userDataFolder: AppPaths.KLongServicesWebView2Profile);
            await VideoBrowser.EnsureCoreWebView2Async(environment);

            var browser = VideoBrowser.CoreWebView2;
            browser.Settings.AreDevToolsEnabled = false;
            browser.Settings.AreDefaultContextMenusEnabled = false;
            browser.Settings.IsStatusBarEnabled = false;
            browser.Settings.IsPasswordAutosaveEnabled = false;
            browser.Settings.IsGeneralAutofillEnabled = false;
            browser.NavigationStarting += Browser_NavigationStarting;
            browser.NavigationCompleted += Browser_NavigationCompleted;
            browser.NewWindowRequested += Browser_NewWindowRequested;
            // The player performs follow-up requests from inside the iframe;
            // attach the referrer to those requests as well as the document
            // navigation, otherwise YouTube reports Error 153 in WebView2.
            browser.AddWebResourceRequestedFilter(
                YouTubeResourceFilter,
                CoreWebView2WebResourceContext.All,
                CoreWebView2WebResourceRequestSourceKinds.All);
            browser.WebResourceRequested += Browser_WebResourceRequested;
            _browserReady = true;
            VideoBrowser.Visibility = Visibility.Visible;
            VideoFallbackPanel.Visibility = Visibility.Collapsed;
            // WebView2 intentionally strips the referrer from cross-origin
            // iframe requests on some runtime builds. YouTube then shows Error
            // 153 even when Referer/Origin are added through the request API.
            // Load the first-party watch page at the top level instead: it has
            // a native youtube.com origin and the same video remains inside
            // this modal without requiring an external browser or a cookie.
            _usingWatchFallback = true;
            browser.Navigate(YouTubeWatchUri.AbsoluteUri);
        }
        catch (Exception exception)
        {
            VideoBrowser.Visibility = Visibility.Collapsed;
            VideoFallbackPanel.Visibility = Visibility.Visible;
            VideoFallbackDetail.Text = exception is WebView2RuntimeNotFoundException
                ? "Máy chưa có WebView2. Bạn vẫn có thể mở video bằng trình duyệt."
                : "Không tải được video lúc này. Bạn vẫn có thể mở bằng trình duyệt.";
        }
    }

    private void Browser_NavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        if (IsAllowedEmbeddedUri(e.Uri))
        {
            return;
        }

        e.Cancel = true;
        OpenExternalIfAllowed(e.Uri);
    }

    private void Browser_NavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (e.IsSuccess)
        {
            if (_usingWatchFallback)
            {
                _ = ConfigureWatchPlayerAsync();
                return;
            }

            // Some WebView2 builds strip the referrer despite
            // NavigateWithWebResourceRequest. YouTube then returns HTTP 200
            // with an in-page Error 153 instead of a failed navigation. Check
            // the settled document and recover to the first-party watch page.
            if (!_usingWatchFallback)
            {
                _ = DetectAndRecoverFromPlayerErrorAsync();
            }

            return;
        }

        VideoBrowser.Visibility = Visibility.Collapsed;
        VideoFallbackPanel.Visibility = Visibility.Visible;
        VideoFallbackDetail.Text = "Không tải được video lúc này. Bạn vẫn có thể mở bằng trình duyệt.";
    }

    private async Task ConfigureWatchPlayerAsync()
    {
        try
        {
            await Task.Delay(150);
            if (!_browserReady || VideoBrowser.CoreWebView2 is null)
            {
                return;
            }

            await VideoBrowser.CoreWebView2.ExecuteScriptAsync(
                """
                (() => {
                  if (document.getElementById('isle-live-map-video-style')) return;
                  const style = document.createElement('style');
                  style.id = 'isle-live-map-video-style';
                  style.textContent = `
                    html, body { overflow: hidden !important; background: #000 !important; }
                    ytd-masthead, #masthead-container, #secondary, #below,
                    #chat-container, #panels, #comments, #guide, #guide-content,
                    tp-yt-app-drawer { display: none !important; }
                    #movie_player {
                      position: fixed !important;
                      inset: 0 !important;
                      width: 100vw !important;
                      height: 100vh !important;
                      z-index: 2147483647 !important;
                      background: #000 !important;
                    }
                  `;
                  document.head.appendChild(style);
                })();
                """);
        }
        catch
        {
            // The regular watch layout remains usable if cosmetic isolation fails.
        }
    }

    private async Task DetectAndRecoverFromPlayerErrorAsync()
    {
        try
        {
            await Task.Delay(350);
            if (!_browserReady || VideoBrowser.CoreWebView2 is null)
            {
                return;
            }

            var rawText = await VideoBrowser.CoreWebView2.ExecuteScriptAsync(
                "document.body ? document.body.innerText : ''");
            var bodyText = JsonSerializer.Deserialize<string>(rawText) ?? rawText;
            if (!bodyText.Contains("Video player configuration error", StringComparison.OrdinalIgnoreCase)
                && !bodyText.Contains("Error 153", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            _usingWatchFallback = true;
            VideoFallbackDetail.Text = "Đang chuyển sang chế độ phát tương thích…";
            VideoBrowser.CoreWebView2.Navigate(YouTubeWatchUri.AbsoluteUri);
        }
        catch
        {
            // A failed diagnostic must never prevent the external YouTube fallback.
        }
    }

    private void Browser_WebResourceRequested(
        object? sender,
        CoreWebView2WebResourceRequestedEventArgs e)
    {
        if (!Uri.TryCreate(e.Request.Uri, UriKind.Absolute, out var uri)
            || !string.Equals(uri.Host, "www.youtube-nocookie.com", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        try
        {
            e.Request.Headers.SetHeader("Referer", "https://www.youtube.com/");
            e.Request.Headers.SetHeader("Origin", "https://www.youtube.com");
        }
        catch (ArgumentException)
        {
            // WebView2 can reject restricted headers on a particular
            // resource; the request still proceeds using its defaults.
        }
    }

    private void Browser_NewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        e.Handled = true;
        OpenExternalIfAllowed(e.Uri);
    }

    internal static bool IsAllowedEmbeddedUri(string rawUri)
    {
        if (string.Equals(rawUri, "about:blank", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!Uri.TryCreate(rawUri, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps)
        {
            return false;
        }

        if (string.Equals(uri.Host, "www.youtube-nocookie.com", StringComparison.OrdinalIgnoreCase))
        {
            return uri.AbsolutePath.StartsWith("/embed/", StringComparison.OrdinalIgnoreCase);
        }

        // Restrict the fallback to this campaign video so the embedded
        // browser cannot become a general-purpose navigation surface.
        return (string.Equals(uri.Host, "www.youtube.com", StringComparison.OrdinalIgnoreCase)
                || string.Equals(uri.Host, "youtube.com", StringComparison.OrdinalIgnoreCase))
               && string.Equals(uri.AbsolutePath, "/watch", StringComparison.OrdinalIgnoreCase)
               && uri.Query.Contains("v=8mMaXM2Y-EQ", StringComparison.OrdinalIgnoreCase);
    }

    private static void OpenExternalIfAllowed(string rawUri)
    {
        if (!Uri.TryCreate(rawUri, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || (!string.Equals(uri.Host, "www.youtube.com", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(uri.Host, "youtube.com", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(uri.Host, "youtu.be", StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        OpenExternal(uri);
    }

    private void CountdownTimer_Tick(object? sender, EventArgs e) => UpdateCountdown();

    private void UpdateCountdown()
    {
        var elapsed = _displayClock.Elapsed;
        var remaining = _closeDelay.Remaining(elapsed);
        CountdownProgress.Value = _closeDelay.Progress(elapsed) * 100d;

        if (_closeDelay.CanClose(elapsed))
        {
            _allowClose = true;
            _countdownTimer.Stop();
            CountdownLabel.Text = "BẠN CÓ THỂ ĐÓNG THÔNG BÁO";
            CloseButton.Content = "ĐÓNG";
            CloseButton.IsEnabled = true;
            CloseButton.Focus();
            return;
        }

        var seconds = Math.Max(1, (int)Math.Ceiling(remaining.TotalSeconds));
        CountdownLabel.Text = $"CÓ THỂ ĐÓNG SAU {seconds} GIÂY";
        CloseButton.Content = $"ĐÓNG SAU {seconds} GIÂY";
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (!_allowClose && !Application.Current.Dispatcher.HasShutdownStarted)
        {
            e.Cancel = true;
        }
    }

    private void Window_Closed(object? sender, EventArgs e)
    {
        _countdownTimer.Stop();
        _displayClock.Stop();

        if (_browserReady && VideoBrowser.CoreWebView2 is not null)
        {
            VideoBrowser.CoreWebView2.NavigationStarting -= Browser_NavigationStarting;
            VideoBrowser.CoreWebView2.NavigationCompleted -= Browser_NavigationCompleted;
            VideoBrowser.CoreWebView2.NewWindowRequested -= Browser_NewWindowRequested;
            VideoBrowser.CoreWebView2.WebResourceRequested -= Browser_WebResourceRequested;
            VideoBrowser.CoreWebView2.RemoveWebResourceRequestedFilter(
                YouTubeResourceFilter,
                CoreWebView2WebResourceContext.All,
                CoreWebView2WebResourceRequestSourceKinds.All);
        }

        VideoBrowser.Dispose();
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!_allowClose && (e.Key == Key.Escape || e.SystemKey == Key.F4))
        {
            e.Handled = true;
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        if (_allowClose)
        {
            Close();
        }
    }

    private void FacebookButton_Click(object sender, RoutedEventArgs e) => OpenExternal(FacebookUri);

    private void YouTubeButton_Click(object sender, RoutedEventArgs e) => OpenExternal(YouTubeUri);

    private void CopyPhoneButton_Click(object sender, RoutedEventArgs e)
    {
        Clipboard.SetText("0705878781");
        if (sender is System.Windows.Controls.Button button)
        {
            button.Content = "ĐÃ SAO CHÉP · 0705 878 781";
        }
    }

    private static void OpenExternal(Uri uri)
    {
        try
        {
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch
        {
            // Contact details remain visible if the browser cannot be opened.
        }
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }
}
