using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using HB.Terminal;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace HB.Documents;

/// <summary>
/// Shows the output of a document: rendered HTML (markdown) or a PDF (LaTeX).
/// The same content can be shown docked in the workspace or in a separate window.
/// </summary>
public sealed class PreviewView : ContentControl, IDisposable
{
    private const string HtmlPage = "https://hb.assets/preview.html";
    private const string DocumentHost = "hb.docs";
    private const string PdfHost = "hb.preview";

    /// <summary>PDFs are shown from copies kept here, so the compiler can always overwrite its output.</summary>
    public static string PdfFolder { get; } =
        Directory.CreateDirectory(Path.Combine(AppPaths.DataFolder, "preview")).FullName;

    private readonly WebView2 _web = new();
    private CoreWebView2Environment? _environment;
    private bool _ready;
    private bool _onHtmlPage;
    private bool _loadingHtmlPage;
    private string _html = "";
    private string? _documentFolder;
    private string? _pdf;

    public PreviewView()
    {
        _web.DefaultBackgroundColor = System.Drawing.Color.FromArgb(22, 22, 22);
        Content = _web;
        Loaded += OnLoaded;
    }

    /// <summary>Shows HTML; images and links resolve against <paramref name="documentFolder"/>.</summary>
    public void ShowHtml(string html, string? documentFolder)
    {
        _pdf = null;
        _html = html;
        _documentFolder = documentFolder;
        Apply();
    }

    public void ShowMessage(string text) => ShowHtml($"<p id=\"empty\">{WebUtility.HtmlEncode(text)}</p>", null);

    /// <summary>Shows a PDF that is inside <see cref="PdfFolder"/>.</summary>
    public void ShowPdf(string pdfPath)
    {
        _pdf = pdfPath;
        Apply();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        try
        {
            _environment = await WebViewEnvironment.GetAsync();
            await _web.EnsureCoreWebView2Async(_environment);
            var core = _web.CoreWebView2;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.SetVirtualHostNameToFolderMapping("hb.assets",
                Path.Combine(AppContext.BaseDirectory, "Assets"), CoreWebView2HostResourceAccessKind.Allow);
            core.SetVirtualHostNameToFolderMapping(PdfHost, PdfFolder, CoreWebView2HostResourceAccessKind.Allow);
            // Images and links of the document are served from its folder by hand: a folder
            // mapping changed after the page has loaded is not seen by that page.
            core.AddWebResourceRequestedFilter($"https://{DocumentHost}/*", CoreWebView2WebResourceContext.All);
            core.WebResourceRequested += OnDocumentResourceRequested;
            core.WebMessageReceived += OnWebMessage;
            core.NavigationStarting += OnNavigationStarting;
            core.NavigationCompleted += OnNavigationCompleted;
            core.NewWindowRequested += (_, args) =>
            {
                args.Handled = true;
                OpenInBrowser(args.Uri);
            };
            _ready = true;
            Apply();
        }
        catch (Exception ex)
        {
            Content = new TextBlock
            {
                Text = "The preview could not start: " + ex.Message,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(16),
            };
        }
    }

    private void Apply()
    {
        if (!_ready) return;
        var core = _web.CoreWebView2;

        if (_pdf != null)
        {
            _onHtmlPage = _loadingHtmlPage = false;
            core.Navigate($"https://{PdfHost}/{Uri.EscapeDataString(Path.GetFileName(_pdf))}");
            return;
        }

        if (!_onHtmlPage)
        {
            // The content is posted when the page has loaded.
            if (!_loadingHtmlPage) core.Navigate(HtmlPage);
            _loadingHtmlPage = true;
            return;
        }

        core.PostWebMessageAsJson(JsonSerializer.Serialize(new { html = _html }));
    }

    /// <summary>The page sends the text the user selected with the mouse, to be copied.</summary>
    private static void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            using var message = JsonDocument.Parse(e.WebMessageAsJson);
            if (message.RootElement.ValueKind == JsonValueKind.Object &&
                message.RootElement.TryGetProperty("type", out var type) && type.GetString() == "copy" &&
                message.RootElement.TryGetProperty("text", out var text))
                SelectToCopy.TryCopy(text.GetString() ?? "");
        }
        catch (JsonException) { }
    }

    /// <summary>Answers a request for <c>https://hb.docs/…</c> with the file of that name in the document folder.</summary>
    private void OnDocumentResourceRequested(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
    {
        if (_environment == null) return;
        var path = ResolveDocumentFile(e.Request.Uri);
        if (path == null)
        {
            e.Response = _environment.CreateWebResourceResponse(null, 404, "Not Found", "Cache-Control: no-store");
            return;
        }
        try
        {
            // The whole file is read now: the stream is consumed after this handler returns, and a
            // file the user is replacing must not be held open meanwhile.
            var stream = new MemoryStream(File.ReadAllBytes(path));
            e.Response = _environment.CreateWebResourceResponse(stream, 200, "OK",
                $"Content-Type: {ContentTypeOf(path)}\nCache-Control: no-store");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            e.Response = _environment.CreateWebResourceResponse(null, 404, "Not Found", "Cache-Control: no-store");
        }
    }

    private string? ResolveDocumentFile(string uri)
    {
        if (_documentFolder == null || !Uri.TryCreate(uri, UriKind.Absolute, out var parsed)) return null;
        var relative = Uri.UnescapeDataString(parsed.AbsolutePath).TrimStart('/').Replace('/', Path.DirectorySeparatorChar);
        if (relative.Length == 0) return null;
        try
        {
            var path = Path.GetFullPath(Path.Combine(_documentFolder, relative));
            return File.Exists(path) ? path : null;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private static string ContentTypeOf(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".gif" => "image/gif",
        ".webp" => "image/webp",
        ".bmp" => "image/bmp",
        ".svg" => "image/svg+xml",
        ".ico" => "image/x-icon",
        ".avif" => "image/avif",
        ".mp4" or ".m4v" => "video/mp4",
        ".webm" => "video/webm",
        ".mp3" => "audio/mpeg",
        ".wav" => "audio/wav",
        ".ogg" or ".opus" => "audio/ogg",
        ".pdf" => "application/pdf",
        ".css" => "text/css",
        ".html" or ".htm" => "text/html; charset=utf-8",
        ".txt" or ".md" => "text/plain; charset=utf-8",
        _ => "application/octet-stream",
    };

    private void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (!_loadingHtmlPage) return;
        _loadingHtmlPage = false;
        _onHtmlPage = true;
        Apply();
    }

    /// <summary>The preview only shows its own pages: web links open in the default browser instead.</summary>
    private void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        if (e.Uri.StartsWith(HtmlPage, StringComparison.OrdinalIgnoreCase) ||
            e.Uri.StartsWith($"https://{PdfHost}/", StringComparison.OrdinalIgnoreCase))
            return;
        e.Cancel = true;
        OpenInBrowser(e.Uri);
    }

    private static void OpenInBrowser(string uri)
    {
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed) || parsed.Host == DocumentHost) return;
        if (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps) return;
        try { Process.Start(new ProcessStartInfo(parsed.AbsoluteUri) { UseShellExecute = true }); }
        catch (System.ComponentModel.Win32Exception) { }
    }

    public void Dispose() => _web.Dispose();
}

/// <summary>A separate window for the preview, e.g. to keep it on another screen.</summary>
public sealed class PreviewWindow : Window
{
    public PreviewWindow()
    {
        Title = "Preview";
        Width = 900;
        Height = 1000;
        Content = View;
        Closed += (_, _) => View.Dispose();
    }

    public PreviewView View { get; } = new();
}
