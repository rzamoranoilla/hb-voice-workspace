using System.IO;
using System.Windows;

namespace HB.Sftp;

/// <summary>
/// Two panes, this computer and the device, between which files are copied over SFTP.
/// Opened from the SSH bar with the connection typed there.
/// </summary>
public partial class SftpWindow : Window
{
    private sealed record TransferJob(bool Upload, IReadOnlyList<string> Paths, string TargetDirectory);

    private readonly string _host;
    private readonly int _port;
    private readonly string _user;
    private readonly string _password;
    private readonly string _localStart;
    private readonly string? _remoteStart;
    private readonly Queue<TransferJob> _jobs = new();
    private SftpSession? _session;
    private CancellationTokenSource? _connecting;
    private CancellationTokenSource? _transferring;
    private bool _running;

    public SftpWindow(string host, int port, string user, string password, string localStart, string? remoteStart)
    {
        InitializeComponent();
        _host = host;
        _port = port;
        _user = user;
        _password = password;
        _localStart = localStart;
        _remoteStart = remoteStart;
        Title = port == 22 ? $"SFTP — {user}@{host}" : $"SFTP — {user}@{host}:{port}";

        LocalPane.TransferVerb = "Copy to the device";
        RemotePane.TransferVerb = "Copy to this computer";
        LocalPane.TransferRequested += entries => Enqueue(true, entries.Select(e => e.FullPath).ToList(), RemotePane.CurrentPath);
        RemotePane.TransferRequested += entries => Enqueue(false, entries.Select(e => e.FullPath).ToList(), LocalPane.CurrentPath);
        LocalPane.DropReceived += drop => Enqueue(false, drop.Paths, drop.TargetDirectory);
        RemotePane.DropReceived += drop => Enqueue(true, drop.Paths, drop.TargetDirectory);
        LocalPane.Failed += ShowProblem;
        RemotePane.Failed += ShowProblem;
        RemotePane.Navigated += path => RemotePathChanged?.Invoke(path);
        // A problem shown in the bar goes away once a folder is read again.
        LocalPane.Navigated += _ => HideProblem();
        RemotePane.Navigated += _ => HideProblem();

        Loaded += async (_, _) =>
        {
            await LocalPane.ShowAsync(new LocalSource(_localStart), _localStart);
            await ConnectAsync();
        };
        Closed += (_, _) =>
        {
            _connecting?.Cancel();
            _transferring?.Cancel();
            _session?.Dispose();
        };
    }

    /// <summary>The folder of the device being shown, to be remembered for next time.</summary>
    public event Action<string>? RemotePathChanged;

    // ---- Connection ----

    private async Task ConnectAsync()
    {
        _connecting?.Cancel();
        var connecting = _connecting = new CancellationTokenSource();
        ConnectBar.Visibility = Visibility.Visible;
        RetryButton.Visibility = Visibility.Collapsed;
        ConnectText.Text = $"Connecting to {_user}@{_host}…";
        try
        {
            var session = await SftpSession.ConnectAsync(_host, _port, _user, _password, AskToTrust, connecting.Token);
            if (connecting.IsCancellationRequested)
            {
                session.Dispose();
                return;
            }
            _session = session;
            ConnectBar.Visibility = Visibility.Collapsed;
            await RemotePane.ShowAsync(new RemoteSource(session), _remoteStart);
        }
        catch (OperationCanceledException) { }
        catch (SftpException ex)
        {
            ConnectText.Text = ex.Message;
            RetryButton.Visibility = Visibility.Visible;
        }
    }

    /// <summary>Asked on a background thread while connecting; the answer comes from the user.</summary>
    private bool AskToTrust(HostKeyInfo key) => Dispatcher.Invoke(() =>
    {
        var text = key.Changed
            ? $"WARNING: the key of {key.Host} has CHANGED.\n\nThis happens when the device was reinstalled, but also when another machine is answering in its place. " +
              $"Continue only if you know why it changed.\n\n{key.KeyType}\n{key.Fingerprint}\n\nConnect anyway?"
            : $"This is the first connection to {key.Host} from this computer.\n\nIts key is\n{key.KeyType}\n{key.Fingerprint}\n\n" +
              "If you can, compare it with the fingerprint shown on the device. Connect and remember this key?";
        return MessageBox.Show(this, text, "SFTP", MessageBoxButton.YesNo,
            key.Changed ? MessageBoxImage.Warning : MessageBoxImage.Question,
            key.Changed ? MessageBoxResult.No : MessageBoxResult.Yes) == MessageBoxResult.Yes;
    });

    private async void Retry_Click(object sender, RoutedEventArgs e) => await ConnectAsync();

    private void HideProblem()
    {
        if (_session != null && RetryButton.Visibility == Visibility.Collapsed) ConnectBar.Visibility = Visibility.Collapsed;
    }

    private void ShowProblem(string message)
    {
        ConnectBar.Visibility = Visibility.Visible;
        RetryButton.Visibility = _session == null ? Visibility.Visible : Visibility.Collapsed;
        ConnectText.Text = message;
    }

    // ---- Transfers, one after the other ----

    private void Upload_Click(object sender, RoutedEventArgs e) =>
        Enqueue(true, LocalPane.SelectedEntries.Select(entry => entry.FullPath).ToList(), RemotePane.CurrentPath);

    private void Download_Click(object sender, RoutedEventArgs e) =>
        Enqueue(false, RemotePane.SelectedEntries.Select(entry => entry.FullPath).ToList(), LocalPane.CurrentPath);

    private async void Enqueue(bool upload, IReadOnlyList<string> paths, string targetDirectory)
    {
        if (paths.Count == 0 || _session == null) return;
        if (targetDirectory.Length == 0)
        {
            ShowProblem("Open a folder on this computer first: files cannot be copied to the list of drives.");
            return;
        }
        _jobs.Enqueue(new TransferJob(upload, paths, targetDirectory));
        if (_running) return;

        _running = true;
        TransferBar.Visibility = Visibility.Visible;
        try
        {
            while (_jobs.TryDequeue(out var job))
            {
                var cancellation = _transferring = new CancellationTokenSource();
                var progress = new Progress<TransferStatus>(ShowProgress);
                try
                {
                    if (job.Upload) await _session.UploadAsync(job.Paths, job.TargetDirectory, progress, cancellation.Token);
                    else await _session.DownloadAsync(job.Paths, job.TargetDirectory, progress, cancellation.Token);
                }
                catch (OperationCanceledException)
                {
                    _jobs.Clear();
                    TransferText.Text = "Cancelled.";
                    break;
                }
                catch (Exception ex) when (ex is SftpException or IOException or UnauthorizedAccessException)
                {
                    _jobs.Clear();
                    ShowProblem("The copy stopped: " + ex.Message);
                    break;
                }
                finally
                {
                    _transferring = null;
                    await (job.Upload ? RemotePane : LocalPane).RefreshAsync();
                }
            }
        }
        finally
        {
            _running = false;
            TransferBar.Visibility = Visibility.Collapsed;
        }
    }

    private void ShowProgress(TransferStatus status)
    {
        if (status.File.Length == 0)
        {
            TransferText.Text = $"{status.Action} {status.FilesTotal} file(s), {FileEntry.FormatSize(status.BytesTotal)}.";
            TransferProgress.Value = 1;
            return;
        }
        TransferText.Text = $"{status.Action} {status.File} — file {status.FilesDone + 1} of {status.FilesTotal} · " +
                            $"{FileEntry.FormatSize(status.BytesDone)} of {FileEntry.FormatSize(status.BytesTotal)}";
        TransferProgress.Value = status.BytesTotal > 0 ? (double)status.BytesDone / status.BytesTotal : 0;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        _jobs.Clear();
        _transferring?.Cancel();
    }
}
