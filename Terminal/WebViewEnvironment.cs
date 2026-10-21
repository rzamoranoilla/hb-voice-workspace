using System.IO;
using Microsoft.Web.WebView2.Core;

namespace HB.Terminal;

/// <summary>The single WebView2 environment shared by every embedded browser view of the app.</summary>
public static class WebViewEnvironment
{
    private static Task<CoreWebView2Environment>? s_environment;

    public static Task<CoreWebView2Environment> GetAsync() =>
        s_environment ??= CoreWebView2Environment.CreateAsync(
            userDataFolder: Path.Combine(AppPaths.DataFolder, "WebView2"));
}
