using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using Renci.SshNet;
using Renci.SshNet.Common;
using Renci.SshNet.Sftp;

namespace HB.Sftp;

/// <summary>What a transfer is doing right now, for the progress bar.</summary>
public sealed record TransferStatus(string Action, string File, long BytesDone, long BytesTotal, int FilesDone, int FilesTotal);

/// <summary>The server's key, for the user to accept the first time (or when it changed).</summary>
public sealed record HostKeyInfo(string Host, int Port, string KeyType, string Fingerprint, bool Changed);

/// <summary>
/// An SFTP connection to a device: browsing on one channel, transfers on a second one so the
/// user can keep looking around while a large copy runs. All calls may come from the UI thread;
/// the work happens on the thread pool, one operation per channel at a time.
/// </summary>
public sealed class SftpSession : IDisposable
{
    private readonly string _host;
    private readonly int _port;
    private readonly string _user;
    private readonly string _password;
    private readonly Func<HostKeyInfo, bool> _trustHostKey;
    private readonly SemaphoreSlim _browseGate = new(1, 1);
    private readonly SemaphoreSlim _transferGate = new(1, 1);
    private SftpClient? _browse;
    private SftpClient? _transfer;
    private bool _disposed;

    private SftpSession(string host, int port, string user, string password, Func<HostKeyInfo, bool> trustHostKey)
    {
        _host = host;
        _port = port;
        _user = user;
        _password = password;
        _trustHostKey = trustHostKey;
    }

    public string Host => _host;
    public string User => _user;
    public string Address => _port == 22 ? $"{_user}@{_host}" : $"{_user}@{_host}:{_port}";

    /// <summary>The folder the server put us in: the user's home, normally.</summary>
    public string HomePath { get; private set; } = "/";

    /// <summary>
    /// Connects and authenticates. <paramref name="trustHostKey"/> is asked (on a background
    /// thread) when the device's key is not known yet or has changed.
    /// </summary>
    public static async Task<SftpSession> ConnectAsync(string host, int port, string user, string password,
        Func<HostKeyInfo, bool> trustHostKey, CancellationToken cancellation)
    {
        var session = new SftpSession(host, port, user, password, trustHostKey);
        try
        {
            await Task.Run(() =>
            {
                session._browse = session.Open(cancellation);
                session.HomePath = session._browse.WorkingDirectory;
            }, cancellation);
        }
        catch
        {
            session.Dispose();
            throw;
        }
        return session;
    }

    /// <summary>Opens a connection, trying the user's SSH keys, then the password.</summary>
    private SftpClient Open(CancellationToken cancellation)
    {
        var methods = new List<AuthenticationMethod>();
        foreach (var key in LoadPrivateKeys()) methods.Add(new PrivateKeyAuthenticationMethod(_user, key));
        if (_password.Length > 0)
        {
            methods.Add(new PasswordAuthenticationMethod(_user, _password));
            // Servers that ask for the password interactively (OpenSSH does so by default).
            var interactive = new KeyboardInteractiveAuthenticationMethod(_user);
            interactive.AuthenticationPrompt += (_, e) =>
            {
                foreach (var prompt in e.Prompts) prompt.Response = _password;
            };
            methods.Add(interactive);
        }
        if (methods.Count == 0) methods.Add(new NoneAuthenticationMethod(_user));

        var info = new ConnectionInfo(_host, _port, _user, methods.ToArray()) { Timeout = TimeSpan.FromSeconds(25) };
        var client = new SftpClient(info) { OperationTimeout = TimeSpan.FromSeconds(60) };
        client.HostKeyReceived += (_, e) => e.CanTrust = CheckHostKey(e.HostKeyName, e.HostKey);
        try
        {
            cancellation.ThrowIfCancellationRequested();
            client.Connect();
        }
        catch (SshAuthenticationException ex)
        {
            client.Dispose();
            throw new SftpException(_password.Length == 0
                ? "The device did not accept the SSH keys of this computer: enter the password."
                : "The user name or the password was not accepted.", ex);
        }
        catch (SshConnectionException ex)
        {
            client.Dispose();
            throw new SftpException(ex.Message.Contains("key", StringComparison.OrdinalIgnoreCase)
                ? "The connection was not made because the device's key was not accepted."
                : $"The connection was lost: {ex.Message}", ex);
        }
        catch (Exception ex) when (ex is System.Net.Sockets.SocketException or SshOperationTimeoutException or ProxyException)
        {
            client.Dispose();
            throw new SftpException($"Could not reach {_host}:{_port}: {ex.Message}", ex);
        }
        catch
        {
            client.Dispose();
            throw;
        }
        return client;
    }

    private static IEnumerable<PrivateKeyFile> LoadPrivateKeys()
    {
        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh");
        foreach (var name in new[] { "id_ed25519", "id_ecdsa", "id_rsa" })
        {
            var path = Path.Combine(folder, name);
            if (!File.Exists(path)) continue;
            PrivateKeyFile? key = null;
            try { key = new PrivateKeyFile(path); }
            catch (Exception ex) when (ex is SshException or IOException or UnauthorizedAccessException or NotSupportedException)
            {
                // Encrypted or unsupported key: skipped, the password will do.
            }
            if (key != null) yield return key;
        }
    }

    // ---- Host keys: remembered per device, asked the first time and whenever they change ----

    private static readonly string KnownHostsPath = Path.Combine(AppPaths.DataFolder, "sftp-known-hosts.json");
    private static readonly object KnownHostsLock = new();

    private bool CheckHostKey(string keyType, byte[] key)
    {
        var fingerprint = "SHA256:" + Convert.ToBase64String(SHA256.HashData(key)).TrimEnd('=');
        var id = $"{_host.ToLowerInvariant()}:{_port}";
        var record = $"{keyType} {fingerprint}";
        lock (KnownHostsLock)
        {
            var known = LoadKnownHosts();
            if (known.TryGetValue(id, out var stored) && stored == record) return true;
            if (!_trustHostKey(new HostKeyInfo(_host, _port, keyType, fingerprint, Changed: stored != null))) return false;
            known[id] = record;
            SaveKnownHosts(known);
            return true;
        }
    }

    private static Dictionary<string, string> LoadKnownHosts()
    {
        try
        {
            if (File.Exists(KnownHostsPath))
                return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(KnownHostsPath)) ?? new();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { }
        return new();
    }

    private static void SaveKnownHosts(Dictionary<string, string> known)
    {
        try { File.WriteAllText(KnownHostsPath, JsonSerializer.Serialize(known, new JsonSerializerOptions { WriteIndented = true })); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    // ---- Browsing ----

    public Task<List<FileEntry>> ListAsync(string directory) => Browse(client =>
    {
        var entries = new List<FileEntry>();
        foreach (var file in client.ListDirectory(directory))
        {
            if (file.Name is "." or "..") continue;
            bool isDirectory = file.IsDirectory;
            long size = file.Length;
            if (file.IsSymbolicLink)
            {
                // What the link points to decides whether it opens as a folder.
                try
                {
                    var target = client.GetAttributes(file.FullName);
                    isDirectory = target.IsDirectory;
                    size = target.Size;
                }
                catch (SshException) { } // dangling link: shown as a file
            }
            entries.Add(new FileEntry(file.Name, file.FullName, isDirectory, isDirectory ? 0 : size, file.LastWriteTime));
        }
        return entries;
    });

    public Task CreateDirectoryAsync(string path) => Browse(client => { client.CreateDirectory(path); return 0; });

    public Task DeleteAsync(FileEntry entry) => Browse(client =>
    {
        if (entry.IsDirectory) DeleteTree(client, entry.FullPath);
        else client.DeleteFile(entry.FullPath);
        return 0;
    });

    public Task RenameAsync(string path, string newPath) => Browse(client => { client.RenameFile(path, newPath); return 0; });

    private static void DeleteTree(SftpClient client, string directory)
    {
        foreach (var file in client.ListDirectory(directory))
        {
            if (file.Name is "." or "..") continue;
            if (file.IsDirectory && !file.IsSymbolicLink) DeleteTree(client, file.FullName);
            else client.DeleteFile(file.FullName);
        }
        client.DeleteDirectory(directory);
    }

    private Task<T> Browse<T>(Func<SftpClient, T> work) => Task.Run(async () =>
    {
        await _browseGate.WaitAsync();
        try
        {
            if (_disposed || _browse == null) throw new SftpException("The connection is closed.");
            return Wrap(() => work(_browse));
        }
        finally { _browseGate.Release(); }
    });

    // ---- Transfers (files and whole folders), on their own channel ----

    public Task UploadAsync(IReadOnlyList<string> localPaths, string remoteDirectory,
        IProgress<TransferStatus> progress, CancellationToken cancellation) => Transfer(client =>
    {
        // Everything is counted first, so the progress means something.
        var files = new List<(string Local, string Remote)>();
        var folders = new List<string>();
        long total = 0;
        foreach (var path in localPaths)
        {
            var remote = PosixCombine(remoteDirectory, Path.GetFileName(path));
            if (Directory.Exists(path))
            {
                folders.Add(remote);
                foreach (var folder in Directory.EnumerateDirectories(path, "*", SearchOption.AllDirectories))
                    folders.Add(PosixCombine(remote, Path.GetRelativePath(path, folder)));
                foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                {
                    files.Add((file, PosixCombine(remote, Path.GetRelativePath(path, file))));
                    total += new FileInfo(file).Length;
                }
            }
            else
            {
                files.Add((path, remote));
                total += new FileInfo(path).Length;
            }
        }

        foreach (var folder in folders)
        {
            cancellation.ThrowIfCancellationRequested();
            if (!client.Exists(folder)) client.CreateDirectory(folder);
        }

        long done = 0;
        for (int i = 0; i < files.Count; i++)
        {
            cancellation.ThrowIfCancellationRequested();
            var (local, remote) = files[i];
            var name = Path.GetFileName(local);
            progress.Report(new TransferStatus("Uploading", name, done, total, i, files.Count));
            long before = done;
            try
            {
                using var input = new CountingStream(File.OpenRead(local), cancellation,
                    n => progress.Report(new TransferStatus("Uploading", name, done += n, total, i, files.Count)));
                client.UploadFile(input, remote, canOverride: true);
            }
            catch (OperationCanceledException)
            {
                try { client.DeleteFile(remote); } catch (SshException) { } // the half-copied file
                throw;
            }
            done = before + new FileInfo(local).Length;
        }
        progress.Report(new TransferStatus("Uploaded", "", total, total, files.Count, files.Count));
        return 0;
    });

    public Task DownloadAsync(IReadOnlyList<string> remotePaths, string localDirectory,
        IProgress<TransferStatus> progress, CancellationToken cancellation) => Transfer(client =>
    {
        var files = new List<(string Remote, string Local, long Size)>();
        var folders = new List<string>();
        long total = 0;
        foreach (var path in remotePaths)
        {
            var name = PosixName(path);
            var local = Path.Combine(localDirectory, name);
            var attributes = client.GetAttributes(path);
            if (attributes.IsDirectory)
            {
                folders.Add(local);
                CollectTree(client, path, local, files, folders, ref total);
            }
            else
            {
                files.Add((path, local, attributes.Size));
                total += attributes.Size;
            }
        }

        foreach (var folder in folders)
        {
            cancellation.ThrowIfCancellationRequested();
            Directory.CreateDirectory(folder);
        }

        long done = 0;
        for (int i = 0; i < files.Count; i++)
        {
            cancellation.ThrowIfCancellationRequested();
            var (remote, local, size) = files[i];
            var name = Path.GetFileName(local);
            progress.Report(new TransferStatus("Downloading", name, done, total, i, files.Count));
            long before = done;
            try
            {
                using var output = new CountingStream(File.Create(local), cancellation,
                    n => progress.Report(new TransferStatus("Downloading", name, done += n, total, i, files.Count)));
                client.DownloadFile(remote, output);
            }
            catch (OperationCanceledException)
            {
                try { File.Delete(local); } catch (IOException) { }
                throw;
            }
            done = before + size;
        }
        progress.Report(new TransferStatus("Downloaded", "", total, total, files.Count, files.Count));
        return 0;
    });

    private static void CollectTree(SftpClient client, string remoteDirectory, string localDirectory,
        List<(string, string, long)> files, List<string> folders, ref long total)
    {
        foreach (var file in client.ListDirectory(remoteDirectory))
        {
            if (file.Name is "." or "..") continue;
            var local = Path.Combine(localDirectory, file.Name);
            if (file.IsDirectory)
            {
                folders.Add(local);
                CollectTree(client, file.FullName, local, files, folders, ref total);
            }
            else if (!file.IsSymbolicLink || !client.GetAttributes(file.FullName).IsDirectory)
            {
                files.Add((file.FullName, local, file.Length));
                total += file.Length;
            }
        }
    }

    private Task<T> Transfer<T>(Func<SftpClient, T> work) => Task.Run(async () =>
    {
        await _transferGate.WaitAsync();
        try
        {
            if (_disposed) throw new SftpException("The connection is closed.");
            // The second channel is opened on the first transfer (the device's key is known by then).
            if (_transfer is not { IsConnected: true })
            {
                _transfer?.Dispose();
                _transfer = Open(CancellationToken.None);
            }
            return Wrap(() => work(_transfer));
        }
        finally { _transferGate.Release(); }
    });

    /// <summary>Turns the library's exceptions into one with a message fit for the window.</summary>
    private static T Wrap<T>(Func<T> work)
    {
        try { return work(); }
        catch (SftpPermissionDeniedException ex) { throw new SftpException("Permission denied: " + ex.Message, ex); }
        catch (SftpPathNotFoundException ex) { throw new SftpException("Not found: " + ex.Message, ex); }
        catch (SshConnectionException ex) { throw new SftpException("The connection was lost: " + ex.Message, ex); }
        catch (SshException ex) { throw new SftpException(ex.Message, ex); }
    }

    public static string PosixCombine(string directory, string name)
    {
        name = name.Replace('\\', '/');
        return directory.EndsWith('/') ? directory + name : directory + "/" + name;
    }

    public static string PosixName(string path)
    {
        var trimmed = path.TrimEnd('/');
        int slash = trimmed.LastIndexOf('/');
        return slash < 0 ? trimmed : trimmed[(slash + 1)..];
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        // Closing is slow when the other side is gone: it happens off the UI thread.
        var browse = _browse;
        var transfer = _transfer;
        _browse = null;
        _transfer = null;
        Task.Run(() =>
        {
            try { browse?.Dispose(); } catch (Exception) { }
            try { transfer?.Dispose(); } catch (Exception) { }
        });
    }

    /// <summary>Counts the bytes that pass and stops the transfer when it is cancelled.</summary>
    private sealed class CountingStream(Stream inner, CancellationToken cancellation, Action<int> advanced) : Stream
    {
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => inner.CanWrite;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }

        public override int Read(byte[] buffer, int offset, int count)
        {
            cancellation.ThrowIfCancellationRequested();
            int read = inner.Read(buffer, offset, count);
            if (read > 0) advanced(read);
            return read;
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            cancellation.ThrowIfCancellationRequested();
            inner.Write(buffer, offset, count);
            advanced(count);
        }

        public override void Flush() => inner.Flush();
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void SetLength(long value) => inner.SetLength(value);

        protected override void Dispose(bool disposing)
        {
            if (disposing) inner.Dispose();
            base.Dispose(disposing);
        }
    }
}

/// <summary>An SFTP failure explained in the user's terms.</summary>
public sealed class SftpException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>The remote device as a file source for the pane.</summary>
public sealed class RemoteSource(SftpSession session) : IFileSource
{
    public string Title => session.Address;
    public bool IsRemote => true;
    public string HomePath => session.HomePath;

    public string Combine(string directory, string name) => SftpSession.PosixCombine(directory, name);

    public string? Parent(string path)
    {
        var trimmed = path.TrimEnd('/');
        if (trimmed.Length == 0) return null;
        int slash = trimmed.LastIndexOf('/');
        return slash <= 0 ? "/" : trimmed[..slash];
    }

    public Task<List<FileEntry>> ListAsync(string directory) => session.ListAsync(directory);
    public Task CreateDirectoryAsync(string path) => session.CreateDirectoryAsync(path);
    public Task DeleteAsync(FileEntry entry) => session.DeleteAsync(entry);
    public Task RenameAsync(string path, string newPath) => session.RenameAsync(path, newPath);
}
