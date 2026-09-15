using System.IO;
using Microsoft.Web.WebView2.Core;

namespace TheIsleOverlay.App;

/// <summary>
/// Reads only playorigin.gg cookies from Isle Live Map's own WebView2
/// profile. This lets the main map button reuse a previously verified Origin
/// login without persisting the website session in a second credential file.
/// </summary>
internal static class OriginSessionCookieReader
{
    private static readonly Uri OriginUri = new("https://playorigin.gg/");

    public static async Task<string?> ReadFromProfileAsync(
        nint parentWindow,
        CancellationToken cancellationToken = default)
    {
        if (parentWindow == 0)
        {
            return null;
        }

        Directory.CreateDirectory(AppPaths.WebView2Profile);
        var environment = await CoreWebView2Environment.CreateAsync(
                browserExecutableFolder: null,
                userDataFolder: AppPaths.WebView2Profile)
            .WaitAsync(cancellationToken);
        var controller = await environment.CreateCoreWebView2ControllerAsync(parentWindow)
            .WaitAsync(cancellationToken);
        try
        {
            controller.IsVisible = false;
            return await ReadAsync(controller.CoreWebView2.CookieManager, cancellationToken);
        }
        finally
        {
            controller.Close();
        }
    }

    public static async Task<string?> ReadAsync(
        CoreWebView2CookieManager cookieManager,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(cookieManager);
        var cookies = await cookieManager.GetCookiesAsync(OriginUri.AbsoluteUri)
            .WaitAsync(cancellationToken);
        var values = cookies
            .Where(cookie =>
                !string.IsNullOrWhiteSpace(cookie.Name)
                && !string.IsNullOrWhiteSpace(cookie.Value))
            .OrderBy(cookie => cookie.Name, StringComparer.Ordinal)
            .Select(cookie => $"{cookie.Name}={cookie.Value}")
            .ToArray();
        return values.Length == 0 ? null : string.Join("; ", values);
    }
}
