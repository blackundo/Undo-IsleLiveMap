using System.IO;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using TheIsleOverlay.Gacha;

namespace TheIsleOverlay.App;

public partial class GachaSteamLoginWindow : Window
{
    private bool _completed;
    private bool _completing;

    public GachaSteamLoginWindow()
    {
        InitializeComponent();
    }

    public GachaOverlayCredentials? Credentials { get; private set; }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.GachaWebView2Profile);
            var environment = await CoreWebView2Environment.CreateAsync(
                browserExecutableFolder: null,
                userDataFolder: AppPaths.GachaWebView2Profile);
            await LoginBrowser.EnsureCoreWebView2Async(environment);

            LoginBrowser.CoreWebView2.Settings.AreDevToolsEnabled = false;
            LoginBrowser.CoreWebView2.Settings.IsStatusBarEnabled = false;
            LoginBrowser.CoreWebView2.NavigationStarting += Browser_NavigationStarting;
            LoginBrowser.CoreWebView2.NavigationCompleted += Browser_NavigationCompleted;
            LoginBrowser.CoreWebView2.NewWindowRequested += Browser_NewWindowRequested;
            NavigateToLogin();
        }
        catch (Exception exception)
        {
            BrowserLoadingPanel.Visibility = Visibility.Visible;
            LoginStatusLabel.Text = $"Không mở được đăng nhập Steam: {FriendlyMessage(exception)}";
        }
    }

    private void Browser_NavigationStarting(
        object? sender,
        CoreWebView2NavigationStartingEventArgs e)
    {
        if (TryCompleteFromCallback(e.Uri))
        {
            e.Cancel = true;
            return;
        }

        if (!GachaOverlayLoginNavigationPolicy.IsAllowed(e.Uri))
        {
            e.Cancel = true;
            LoginStatusLabel.Text = "Đã chặn điều hướng nằm ngoài Gacha và Steam.";
        }
    }

    private void Browser_NavigationCompleted(
        object? sender,
        CoreWebView2NavigationCompletedEventArgs e)
    {
        BrowserLoadingPanel.Visibility = Visibility.Collapsed;
        if (!e.IsSuccess && !_completed)
        {
            LoginStatusLabel.Text = "Trang đăng nhập không tải được. Kiểm tra mạng rồi bấm THỬ LẠI.";
        }
    }

    private void Browser_NewWindowRequested(
        object? sender,
        CoreWebView2NewWindowRequestedEventArgs e)
    {
        e.Handled = true;
        if (TryCompleteFromCallback(e.Uri))
        {
            return;
        }

        if (GachaOverlayLoginNavigationPolicy.IsAllowed(e.Uri))
        {
            LoginBrowser.CoreWebView2.Navigate(e.Uri);
            return;
        }

        LoginStatusLabel.Text = "Đã chặn cửa sổ nằm ngoài Gacha và Steam.";
    }

    private bool TryCompleteFromCallback(string? callback)
    {
        if (!Uri.TryCreate(callback, UriKind.Absolute, out var uri)
            || !string.Equals(
                uri.Scheme,
                GachaOverlayAuthService.CallbackScheme,
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!GachaOverlayAuthService.TryParseCallback(
                callback,
                out var result,
                out var errorMessage)
            || result is null)
        {
            LoginStatusLabel.Text = $"Gacha trả về callback không hợp lệ: {errorMessage ?? "hãy thử lại."}";
            return true;
        }

        if (!_completing)
        {
            _completing = true;
            Credentials = result.ToCredentials();
            _completed = true;
            DialogResult = true;
            Close();
        }

        return true;
    }

    private void NavigateToLogin()
    {
        if (LoginBrowser.CoreWebView2 is null || _completing)
        {
            return;
        }

        BrowserLoadingPanel.Visibility = Visibility.Visible;
        LoginStatusLabel.Text = "Đang chuyển tới Gacha và Steam…";
        LoginBrowser.CoreWebView2.Navigate(GachaOverlayAuthService.LoginUri.AbsoluteUri);
    }

    private void RetryButton_Click(object sender, RoutedEventArgs e) => NavigateToLogin();

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_completing)
        {
            Close();
        }
    }

    private void Window_Closed(object? sender, EventArgs e)
    {
        if (LoginBrowser.CoreWebView2 is not null)
        {
            LoginBrowser.CoreWebView2.NavigationStarting -= Browser_NavigationStarting;
            LoginBrowser.CoreWebView2.NavigationCompleted -= Browser_NavigationCompleted;
            LoginBrowser.CoreWebView2.NewWindowRequested -= Browser_NewWindowRequested;
        }

        if (!_completed)
        {
            Credentials = null;
        }

        LoginBrowser.Dispose();
    }

    private static string FriendlyMessage(Exception exception) => exception switch
    {
        WebView2RuntimeNotFoundException => "Máy chưa có Microsoft Edge WebView2 Runtime.",
        _ => exception.Message
    };
}
