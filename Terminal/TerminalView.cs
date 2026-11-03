using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace HB.Terminal;

/// <summary>
/// A PowerShell terminal: xterm.js rendered in WebView2, connected to a ConPTY-hosted shell.
/// </summary>
public sealed class TerminalView : ContentControl, IDisposable
{
    private const string FocusMessage = "\0focus";
    private const string RefitMessage = "\0refit";

    private readonly WebView2 _web = new();
    private readonly string _workingDirectory;
    private PseudoConsole? _console;
    private bool _disposed;
    private string? _sshPassword;
    private DateTime _sshPasswordDeadline;
    private string _recentOutput = "";

    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Raised on the UI thread when the shell process ends on its own.</summary>
    public event Action? Exited;

    /// <summary>Completes once the shell is running (or could not start — the terminal then says why).</summary>
    public Task Ready => _ready.Task;

    public TerminalView(string workingDirectory)
    {
        _workingDirectory = workingDirectory;
        _web.DefaultBackgroundColor = System.Drawing.Color.FromArgb(12, 12, 12);
        Content = _web;
        Loaded += OnLoaded;
        // The page fits its grid to the control by itself, but a terminal that is created hidden,
        // shown later, or resized while hidden can miss that: tell it on each of those events.
        SizeChanged += (_, _) => RequestRefit();
        IsVisibleChanged += (_, args) => { if (args.NewValue is true) RequestRefit(); };
    }

    /// <summary>The window moved to a screen with another scale: the cells change size.</summary>
    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        RequestRefit();
    }

    /// <summary>Asks the page to fit the grid to the control again and tell the shell its size.</summary>
    private void RequestRefit()
    {
        if (!_disposed) _web.CoreWebView2?.PostWebMessageAsString(RefitMessage);
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        try
        {
            await _web.EnsureCoreWebView2Async(await WebViewEnvironment.GetAsync());
            if (_disposed) return;

            var core = _web.CoreWebView2;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.SetVirtualHostNameToFolderMapping("hb.assets",
                Path.Combine(AppContext.BaseDirectory, "Assets"), CoreWebView2HostResourceAccessKind.Allow);
            core.WebMessageReceived += OnWebMessage;
            core.Navigate("https://hb.assets/terminal.html");
        }
        catch (Exception ex)
        {
            Content = new TextBlock
            {
                Text = "The terminal could not start: " + ex.Message,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(16),
            };
            _ready.TrySetResult();
        }
    }

    /// <summary>
    /// Runs ssh in this terminal and answers its first password prompt with <paramref name="password"/>.
    /// Returns false when the shell is not ready yet.
    /// </summary>
    public bool ConnectSsh(string host, int port, string user, string password)
    {
        if (_console == null) return false;
        _sshPassword = password.Length > 0 ? password : null;
        _sshPasswordDeadline = DateTime.UtcNow.AddSeconds(60);
        _recentOutput = "";
        _console.Write($"ssh -p {port} {user}@{host}\r");
        FocusTerminal();
        return true;
    }

    private void AnswerPasswordPrompt(string output)
    {
        if (_sshPassword == null) return;
        if (DateTime.UtcNow > _sshPasswordDeadline)
        {
            _sshPassword = null;
            return;
        }

        // The prompt can arrive split across reads, so match on the tail of what was received.
        _recentOutput += output;
        if (_recentOutput.Length > 200) _recentOutput = _recentOutput[^200..];
        if (!_recentOutput.Contains("password:", StringComparison.OrdinalIgnoreCase)) return;

        // Answer only once: if the password is wrong, ssh asks again and the user types it.
        var password = _sshPassword;
        _sshPassword = null;
        _console?.Write(password + "\r");
    }

    public void FocusTerminal()
    {
        _web.Focus();
        _web.CoreWebView2?.PostWebMessageAsString(FocusMessage);
    }

    private void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        using var message = JsonDocument.Parse(e.WebMessageAsJson);
        var root = message.RootElement;
        switch (root.GetProperty("type").GetString())
        {
            case "ready":
                StartShell(root.GetProperty("cols").GetInt32(), root.GetProperty("rows").GetInt32());
                break;
            case "input":
                _console?.Write(root.GetProperty("data").GetString() ?? "");
                break;
            case "resize":
                _console?.Resize(root.GetProperty("cols").GetInt32(), root.GetProperty("rows").GetInt32());
                break;
            case "copy":
                TrySetClipboard(root.GetProperty("text").GetString() ?? "");
                break;
            case "paste":
                if (Clipboard.ContainsText()) _console?.Write(Clipboard.GetText().Replace("\r\n", "\r"));
                break;
        }
    }

    private void StartShell(int cols, int rows)
    {
        if (_console != null || _disposed) return;
        try
        {
            var directory = Directory.Exists(_workingDirectory)
                ? _workingDirectory
                : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            _console = new PseudoConsole($"\"{FindShell()}\" -NoLogo", directory, cols, rows);
            _console.OutputReceived += text => Dispatcher.BeginInvoke(() =>
            {
                if (_disposed) return;
                _web.CoreWebView2?.PostWebMessageAsString(text);
                AnswerPasswordPrompt(text);
            });
            _console.Exited += () => Dispatcher.BeginInvoke(() => Exited?.Invoke());
            _ready.TrySetResult();
        }
        catch (Exception ex)
        {
            _web.CoreWebView2.PostWebMessageAsString("Could not start PowerShell: " + ex.Message);
            _ready.TrySetResult();
        }
    }

    /// <summary>Prefers PowerShell 7 (pwsh) when installed, otherwise Windows PowerShell.</summary>
    private static string FindShell()
    {
        foreach (var folder in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
        {
            if (string.IsNullOrWhiteSpace(folder)) continue;
            try
            {
                var candidate = Path.Combine(folder.Trim(), "pwsh.exe");
                if (File.Exists(candidate)) return candidate;
            }
            catch (ArgumentException) { }
        }
        return Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe");
    }

    private static void TrySetClipboard(string text)
    {
        try { Clipboard.SetText(text); }
        catch (System.Runtime.InteropServices.COMException) { } // clipboard busy
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _console?.Dispose();
        _ready.TrySetResult();
        _web.Dispose();
    }
}
