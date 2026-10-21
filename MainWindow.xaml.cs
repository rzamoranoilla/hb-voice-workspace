using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using HB.Speech;
using HB.Terminal;
using Microsoft.Win32;

namespace HB;

public partial class MainWindow : Window
{
    private readonly ObservableCollection<Workspace> _workspaces = new();
    private readonly SpeechTranscriber _transcriber = new();
    private readonly TextSpeaker _speaker = new();
    private readonly UserSession _session;
    private readonly AppSettings _settings;
    private readonly ObservableCollection<SshConnection> _connections;
    private Workspace? _recordingWorkspace;
    private DictationLanguage? _recordingLanguage;
    private string? _playingFileName;
    private readonly DispatcherTimer _positionTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private bool _loading = true;
    private bool _closing;

    public MainWindow(UserSession session)
    {
        InitializeComponent();

        _session = session;
        _settings = AppSettings.Load(session.SettingsPath);
        _connections = new ObservableCollection<SshConnection>(_settings.SshConnections);
        SshSavedBox.ItemsSource = _connections;
        DocPane.StateChanged += SaveSettings;
        UserText.Text = session.Name;
        Title = "HB Voice Workspace — " + session.Name;

        WorkspaceList.ItemsSource = _workspaces;
        LanguageBox.ItemsSource = DictationLanguage.All;
        ModelBox.ItemsSource = SpeechModel.All;

        var languageCode = _settings.Language ?? CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
        LanguageBox.SelectedItem = DictationLanguage.All.FirstOrDefault(l => l.Code == languageCode)
                                   ?? DictationLanguage.All.First(l => l.Code == "en");
        ModelBox.SelectedItem = SpeechModel.All.FirstOrDefault(m => m.Id == _settings.Model) ?? SpeechModel.All[0];

        VoiceBox.ItemsSource = SpeakerVoice.All;
        VoiceBox.SelectedItem = SpeakerVoice.All.FirstOrDefault(v => v.Info.Id == _settings.Voice)
                                ?? SpeakerVoice.All.FirstOrDefault(v => v.Info.Language.StartsWith(languageCode))
                                ?? SpeakerVoice.All.FirstOrDefault();
        _speaker.Finished += () => ReadAloudButton.Content = "🔊  Read aloud";

        _transcriber.TextChanged += text => Dispatcher.BeginInvoke(() =>
        {
            LiveText.Text = text;
            LiveScroll.ScrollToEnd();
        });
        _transcriber.StatusChanged += status => Dispatcher.BeginInvoke(() => StatusText.Text = status);
        _transcriber.FileEnded += error => Dispatcher.BeginInvoke(async () =>
        {
            if (error != null) StatusText.Text = "Playback stopped: " + error.Message;
            await StopFileAsync(finished: error == null);
        });
        _positionTimer.Tick += (_, _) => UpdateFilePosition();

        foreach (var path in _settings.Workspaces.Where(Directory.Exists))
            _workspaces.Add(new Workspace(path, _settings.WorkspaceOptions.GetValueOrDefault(path)));
        _loading = false;

        WorkspaceList.SelectedItem = _workspaces.FirstOrDefault(w => w.Path == _settings.SelectedWorkspace)
                                     ?? _workspaces.FirstOrDefault();
        ShowWorkspace();
    }

    private Workspace? Current => WorkspaceList.SelectedItem as Workspace;

    /// <summary>True when the window was closed to switch user rather than to quit.</summary>
    public bool LogoutRequested { get; private set; }

    private void Logout_Click(object sender, RoutedEventArgs e)
    {
        if (_transcriber.IsRecording)
        {
            StatusText.Text = "Stop the dictation before logging out.";
            return;
        }
        LogoutRequested = true;
        Close();
    }

    // ---- Workspaces ----

    private void AddWorkspace_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new NewWorkspaceDialog { Owner = this };
        if (dialog.ShowDialog() != true) return;

        var path = dialog.FolderPath;
        var workspace = _workspaces.FirstOrDefault(w => string.Equals(w.Path, path, StringComparison.OrdinalIgnoreCase));
        if (workspace == null)
        {
            workspace = new Workspace(path, new WorkspaceOptions { Template = dialog.SelectedTemplate.Id });
            _workspaces.Add(workspace);
        }
        WorkspaceList.SelectedItem = workspace;
        SaveSettings();
    }

    private void RemoveWorkspace_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not Workspace workspace) return;
        if (workspace == _recordingWorkspace)
        {
            StatusText.Text = "Stop the dictation before closing this workspace.";
            return;
        }

        foreach (var tab in workspace.Tabs.ToList()) CloseTab(workspace, tab);
        _workspaces.Remove(workspace);
        if (Current == null) WorkspaceList.SelectedItem = _workspaces.FirstOrDefault();
        ShowWorkspace();
        SaveSettings();
    }

    private void WorkspaceList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        ShowWorkspace();
        SaveSettings();
    }

    /// <summary>Points the terminal and transcriber columns at the selected workspace.</summary>
    private void ShowWorkspace()
    {
        var workspace = Current;
        NoWorkspacesHint.Visibility = _workspaces.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        NoTerminalHint.Visibility = workspace == null ? Visibility.Visible : Visibility.Collapsed;
        AddTabButton.IsEnabled = workspace != null;

        if (workspace != null && !workspace.EntriesLoaded)
        {
            workspace.EntriesLoaded = true;
            try
            {
                foreach (var entry in TranscriptStore.Load(workspace)) workspace.Entries.Add(entry);
            }
            catch (IOException ex)
            {
                StatusText.Text = "Could not read " + workspace.MarkdownPath + ": " + ex.Message;
            }
        }

        EntriesList.ItemsSource = workspace?.Entries;
        EntriesScroll.ScrollToEnd();
        MarkdownPathText.Text = workspace == null ? "" : "Saved to " + workspace.MarkdownPath;
        MarkdownPathText.ToolTip = workspace?.MarkdownPath;

        UpdateSshBarVisibility();
        if (workspace?.IsSsh == true)
        {
            var options = workspace.Options;
            SshHostBox.Text = options.SshHost;
            SshPortBox.Text = options.SshPort.ToString(CultureInfo.InvariantCulture);
            SshUserBox.Text = options.SshUser;
            // The workspace remembers its last device; the password comes from the saved connection, if any.
            var saved = _connections.FirstOrDefault(c => c.Matches(options.SshHost, options.SshPort, options.SshUser));
            SshPasswordBox.Password = _session.Unprotect(saved?.PasswordProtected);
            SshNameBox.Text = saved?.Name ?? "";
            _loading = true;
            SshSavedBox.SelectedItem = saved;
            _loading = false;
            SshStatus.Text = "";
        }

        // Markdown and LaTeX workspaces replace the terminals with the document editor.
        bool isDocument = workspace?.IsDocument == true;
        TerminalColumn.Visibility = isDocument ? Visibility.Collapsed : Visibility.Visible;
        DocPane.Visibility = isDocument ? Visibility.Visible : Visibility.Collapsed;
        if (isDocument)
        {
            TabStrip.ItemsSource = null;
            ShowSelectedTerminal();
            DocPane.Show(workspace!);
            return;
        }
        DocPane.Leave();

        if (workspace != null && !workspace.TabsRestored)
        {
            workspace.TabsRestored = true;
            foreach (var title in workspace.Options.Tabs) AddTab(workspace, title);
            workspace.TabCounter = workspace.Tabs.Count;
            workspace.SelectedTab = workspace.Tabs.FirstOrDefault();
        }
        if (workspace != null && workspace.Tabs.Count == 0) AddTab(workspace);
        TabStrip.ItemsSource = workspace?.Tabs;
        TabStrip.SelectedItem = workspace?.SelectedTab;
        ShowSelectedTerminal();
    }

    // ---- PowerShell tabs ----

    private void AddTab_Click(object sender, RoutedEventArgs e)
    {
        if (Current is not { } workspace) return;
        AddTab(workspace);
        TabStrip.SelectedItem = workspace.SelectedTab;
        ShowSelectedTerminal();
        SaveSettings();
    }

    private void AddTab(Workspace workspace, string? title = null)
    {
        var view = new TerminalView(workspace.Path) { Visibility = Visibility.Collapsed };
        var tab = new TerminalTab(title ?? $"PowerShell {++workspace.TabCounter}", view);
        view.Exited += () => CloseTab(workspace, tab);
        TerminalHost.Children.Add(view);
        workspace.Tabs.Add(tab);
        workspace.SelectedTab = tab;
    }

    private void CloseTab_Click(object sender, RoutedEventArgs e)
    {
        if (Current is { } workspace && ((FrameworkElement)sender).DataContext is TerminalTab tab)
            CloseTab(workspace, tab);
    }

    private void CloseTab(Workspace workspace, TerminalTab tab)
    {
        int index = workspace.Tabs.IndexOf(tab);
        if (index < 0 || _closing) return;

        if (workspace.SelectedTab == tab)
            workspace.SelectedTab = workspace.Tabs.ElementAtOrDefault(index + 1) ?? workspace.Tabs.ElementAtOrDefault(index - 1);
        workspace.Tabs.RemoveAt(index);
        TerminalHost.Children.Remove(tab.View);
        tab.View.Dispose();

        if (workspace == Current)
        {
            TabStrip.SelectedItem = workspace.SelectedTab;
            ShowSelectedTerminal();
        }
        SaveSettings();
    }

    // Double-click a tab title to rename it; the names are saved with the workspace.

    private void RenameTab_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is TerminalTab tab) tab.IsEditing = true;
    }

    private void TabTitle_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount != 2 || ((FrameworkElement)sender).DataContext is not TerminalTab tab) return;
        tab.IsEditing = true;
        e.Handled = true;
    }

    private void TabRename_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        var box = (TextBox)sender;
        if (!box.IsVisible || box.DataContext is not TerminalTab tab) return;
        box.Text = tab.Title;
        // Wait until the context menu has closed and given focus back, or it would end the rename at once.
        Dispatcher.BeginInvoke(() =>
        {
            box.Focus();
            box.SelectAll();
        }, DispatcherPriority.ApplicationIdle);
    }

    private void TabRename_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Enter or Key.Escape)) return;
        var box = (TextBox)sender;
        if (e.Key == Key.Escape && box.DataContext is TerminalTab tab) box.Text = tab.Title;
        e.Handled = true;
        EndRename(box);
        ShowSelectedTerminal();
    }

    private void TabRename_LostFocus(object sender, RoutedEventArgs e) => EndRename((TextBox)sender);

    private void EndRename(TextBox box)
    {
        if (box.DataContext is not TerminalTab { IsEditing: true } tab) return;
        var title = box.Text.Trim();
        if (title.Length > 0) tab.Title = title;
        tab.IsEditing = false;
        SaveSettings();
    }

    private void TabStrip_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Current is not { } workspace || TabStrip.SelectedItem is not TerminalTab tab) return;
        if (!workspace.Tabs.Contains(tab)) return; // the strip is being re-pointed at another workspace
        workspace.SelectedTab = tab;
        ShowSelectedTerminal();
    }

    private void ShowSelectedTerminal()
    {
        var selected = Current?.SelectedTab?.View;
        foreach (TerminalView view in TerminalHost.Children)
            view.Visibility = view == selected ? Visibility.Visible : Visibility.Collapsed;
        selected?.FocusTerminal();
    }

    // ---- SSH template ----

    private void SshToggle_Click(object sender, RoutedEventArgs e)
    {
        if (Current is not { IsSsh: true } workspace) return;
        workspace.Options.SshBarCollapsed = !workspace.Options.SshBarCollapsed;
        UpdateSshBarVisibility();
        SaveSettings();
        ShowSelectedTerminal();
    }

    /// <summary>The SSH bar exists only in SSH workspaces and can be minimized to give the terminal the room.</summary>
    private void UpdateSshBarVisibility()
    {
        bool isSsh = Current?.IsSsh == true;
        bool collapsed = Current?.Options.SshBarCollapsed == true;
        SshBar.Visibility = isSsh && !collapsed ? Visibility.Visible : Visibility.Collapsed;
        SshToggleButton.Visibility = isSsh ? Visibility.Visible : Visibility.Collapsed;
        SshToggleButton.Content = collapsed ? "SSH connection ▾" : "Hide SSH connection ▴";
    }

    /// <summary>Reads and validates the connection fields; reports the problem in the SSH bar.</summary>
    private bool TryReadSshFields(out string host, out int port, out string user)
    {
        // The values are typed into a shell, so only plain host and user names are accepted.
        host = SshHostBox.Text.Trim();
        user = SshUserBox.Text.Trim();
        port = 0;
        if (!Regex.IsMatch(host, @"^[A-Za-z0-9][A-Za-z0-9.\-:]*$"))
            SshStatus.Text = "Enter the host name or IP address of the device.";
        else if (!Regex.IsMatch(user, @"^[A-Za-z0-9_][A-Za-z0-9_.\-]*$"))
            SshStatus.Text = "Enter the user name (letters, digits, dot, dash or underscore).";
        else if (!int.TryParse(SshPortBox.Text.Trim(), out port) || port is < 1 or > 65535)
            SshStatus.Text = "The port must be a number between 1 and 65535 (SSH normally uses 22).";
        else
            return true;
        return false;
    }

    private void SshSave_Click(object sender, RoutedEventArgs e)
    {
        if (!TryReadSshFields(out var host, out int port, out var user)) return;

        // Saving under an existing name updates that device; otherwise the same address is updated.
        var name = SshNameBox.Text.Trim();
        var connection = (name.Length > 0
                             ? _connections.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase))
                             : null)
                         ?? _connections.FirstOrDefault(c => c.Matches(host, port, user));
        if (connection != null) _connections.Remove(connection);
        connection = new SshConnection
        {
            Name = name,
            Host = host,
            Port = port,
            User = user,
            PasswordProtected = SshPasswordBox.Password.Length > 0 ? _session.Protect(SshPasswordBox.Password) : null,
        };
        _connections.Add(connection);

        _loading = true;
        SshSavedBox.SelectedItem = connection;
        _loading = false;
        SaveSettings();
        SshStatus.Text = connection.PasswordProtected != null
            ? $"Saved {connection.Label} with its password."
            : $"Saved {connection.Label} without a password.";
    }

    private void SshDelete_Click(object sender, RoutedEventArgs e)
    {
        if (SshSavedBox.SelectedItem is not SshConnection connection)
        {
            SshStatus.Text = "Choose a saved connection to delete.";
            return;
        }
        _connections.Remove(connection);
        SaveSettings();
        SshStatus.Text = $"Deleted {connection.Label}.";
    }

    private void SshSavedBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || SshSavedBox.SelectedItem is not SshConnection connection) return;
        SshNameBox.Text = connection.Name;
        SshHostBox.Text = connection.Host;
        SshPortBox.Text = connection.Port.ToString(CultureInfo.InvariantCulture);
        SshUserBox.Text = connection.User;
        SshPasswordBox.Password = _session.Unprotect(connection.PasswordProtected);
        SshStatus.Text = "";
    }

    private void SshConnect_Click(object sender, RoutedEventArgs e)
    {
        if (Current is not { IsSsh: true } workspace) return;
        if (!TryReadSshFields(out var host, out int port, out var user)) return;

        var options = workspace.Options;
        options.SshHost = host;
        options.SshPort = port;
        options.SshUser = user;
        SaveSettings();

        if (workspace.SelectedTab == null)
        {
            AddTab(workspace);
            TabStrip.SelectedItem = workspace.SelectedTab;
            ShowSelectedTerminal();
        }
        SshStatus.Text = workspace.SelectedTab!.View.ConnectSsh(host, port, user, SshPasswordBox.Password)
            ? $"Connecting to {user}@{host} in {workspace.SelectedTab.Title}…"
            : "The terminal is still starting, try again in a moment.";
    }

    // ---- Transcriber ----

    private async void MicButton_Click(object sender, RoutedEventArgs e)
    {
        MicButton.IsEnabled = false;
        try
        {
            if (_transcriber.IsRecording) await StopDictationAsync();
            else await StartDictationAsync();
        }
        finally
        {
            MicButton.IsEnabled = true;
        }
    }

    private async Task StartDictationAsync()
    {
        if (Current is not { } workspace)
        {
            StatusText.Text = "Open a workspace first: the transcription is saved in its folder.";
            return;
        }

        var language = (DictationLanguage)LanguageBox.SelectedItem;
        var model = (SpeechModel)ModelBox.SelectedItem;
        try
        {
            StatusText.Text = "Preparing…";
            await _transcriber.StartAsync(language.Code, model.Id);
        }
        catch (Exception ex)
        {
            StatusText.Text = ex is NAudio.MmException
                ? "No microphone available. Check that one is connected and that Windows allows desktop apps to use it (Settings > Privacy & security > Microphone)."
                : "Could not start the dictation: " + ex.Message;
            return;
        }

        _recordingWorkspace = workspace;
        _recordingLanguage = language;
        LiveText.Text = "";
        LivePanel.Visibility = Visibility.Visible;
        LanguageBox.IsEnabled = ModelBox.IsEnabled = false;
        MicButton.Content = "⏹  Stop dictation";
        StatusText.Text = $"Listening ({language.Name}) — recording into {workspace.Name}.md";
    }

    private async Task StopDictationAsync()
    {
        StatusText.Text = "Finishing transcription…";
        string text;
        try
        {
            text = await _transcriber.StopAsync();
        }
        catch (Exception ex)
        {
            text = LiveText.Text.Trim();
            StatusText.Text = "The transcription ended with an error: " + ex.Message;
        }

        var workspace = _recordingWorkspace!;
        _recordingWorkspace = null;
        LivePanel.Visibility = Visibility.Collapsed;
        LanguageBox.IsEnabled = ModelBox.IsEnabled = true;
        MicButton.Content = "🎤  Start dictation";

        if (text.Length == 0)
        {
            StatusText.Text = "Microphone off — nothing was heard, no entry saved.";
            return;
        }

        SaveEntry(workspace, new TranscriptEntry(DateTime.Now, _recordingLanguage!.Name, text), "Microphone off");
    }

    /// <summary>Adds an entry to the workspace list and appends it to its markdown file.</summary>
    private void SaveEntry(Workspace workspace, TranscriptEntry entry, string statusPrefix)
    {
        workspace.Entries.Add(entry);
        if (workspace == Current) EntriesScroll.ScrollToEnd();
        try
        {
            // The transcription file may be the one open in the markdown editor.
            DocPane.FlushSave();
            TranscriptStore.Append(workspace, entry);
            DocPane.ReloadIfOpen(workspace.MarkdownPath);
            StatusText.Text = $"{statusPrefix} — entry saved to {workspace.Name}.md";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StatusText.Text = "The entry could not be saved to the markdown file: " + ex.Message;
        }
    }

    private async void FileButton_Click(object sender, RoutedEventArgs e)
    {
        if (_transcriber.IsPlayingFile)
        {
            await StopFileAsync(finished: false);
            return;
        }
        if (_transcriber.IsRecording)
        {
            StatusText.Text = "Stop the dictation first.";
            return;
        }
        if (Current is not { } workspace)
        {
            StatusText.Text = "Open a workspace first: the transcription is saved in its folder.";
            return;
        }

        var dialog = new OpenFileDialog
        {
            Title = "Choose the audio file to transcribe",
            Filter = "Audio and video|*.mp3;*.wav;*.m4a;*.aac;*.wma;*.flac;*.ogg;*.opus;*.mp4;*.wmv;*.mkv|All files|*.*",
        };
        if (dialog.ShowDialog(this) != true) return;

        var language = (DictationLanguage)LanguageBox.SelectedItem;
        var model = (SpeechModel)ModelBox.SelectedItem;
        var fileName = Path.GetFileName(dialog.FileName);
        FileButton.IsEnabled = false;
        MicButton.IsEnabled = false;
        LanguageBox.IsEnabled = ModelBox.IsEnabled = false;
        StatusText.Text = $"Preparing {fileName}…";
        try
        {
            await _transcriber.StartFileAsync(dialog.FileName, language.Code, model.Id);
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Could not play {fileName}: {ex.Message}";
            ResetFileUi();
            return;
        }
        finally
        {
            FileButton.IsEnabled = true;
        }

        _recordingWorkspace = workspace;
        _recordingLanguage = language;
        _playingFileName = fileName;
        LiveText.Text = "";
        LivePanel.Visibility = Visibility.Visible;
        FileButton.Content = "⏹  Stop and save";
        StatusText.Text = $"Playing {fileName} ({language.Name}) — transcribing into {workspace.Name}.md";
        TranscribeProgress.IsIndeterminate = false;
        ProgressPanel.Visibility = Visibility.Visible;
        UpdateFilePosition();
        _positionTimer.Start();
    }

    private void UpdateFilePosition()
    {
        var duration = _transcriber.FileDuration;
        var position = _transcriber.FilePosition;
        if (position > duration) position = duration;
        TranscribeProgress.Value = duration > TimeSpan.Zero ? position / duration : 0;
        ProgressStageText.Text = $"Playing {FormatDuration(position)} / {FormatDuration(duration)}";
        ProgressTimeText.Text = $"{FormatDuration(duration - position)} left";
    }

    /// <summary>Stops the playback (or handles its natural end) and saves what was transcribed.</summary>
    private async Task StopFileAsync(bool finished)
    {
        if (!_transcriber.IsPlayingFile) return;
        _positionTimer.Stop();
        FileButton.IsEnabled = false;
        StatusText.Text = "Finishing transcription…";
        string text;
        try
        {
            text = await _transcriber.StopAsync();
        }
        catch (Exception ex)
        {
            text = LiveText.Text.Trim();
            StatusText.Text = "The transcription ended with an error: " + ex.Message;
        }

        var workspace = _recordingWorkspace!;
        var label = $"{_recordingLanguage!.Name} · {_playingFileName}";
        _recordingWorkspace = null;
        ResetFileUi();
        FileButton.IsEnabled = true;

        if (text.Length == 0)
        {
            StatusText.Text = $"No speech was recognised in {_playingFileName}, no entry saved.";
            return;
        }
        SaveEntry(workspace, new TranscriptEntry(DateTime.Now, label, text), finished ? "Playback finished" : "Stopped");
    }

    private void ResetFileUi()
    {
        LivePanel.Visibility = Visibility.Collapsed;
        ProgressPanel.Visibility = Visibility.Collapsed;
        TranscribeProgress.IsIndeterminate = false;
        FileButton.Content = "📂  Transcribe an audio file…";
        MicButton.IsEnabled = true;
        LanguageBox.IsEnabled = ModelBox.IsEnabled = true;
    }

    private static string FormatDuration(TimeSpan time)
    {
        if (time < TimeSpan.Zero) time = TimeSpan.Zero;
        return time.TotalHours >= 1 ? time.ToString(@"h\:mm\:ss") : time.ToString(@"m\:ss");
    }

    private void Entry_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is TranscriptEntry entry) CopyEntry(entry, entry.Text);
    }

    /// <summary>
    /// The text of an entry swallows the click of its button: a plain click still copies the whole
    /// entry, while a drag copies just the selected words (as everywhere else in the app).
    /// </summary>
    private void EntryText_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not TextBox { DataContext: TranscriptEntry entry } box) return;
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
            CopyEntry(entry, box.SelectionLength > 0 ? box.SelectedText : entry.Text));
    }

    private async void CopyEntry(TranscriptEntry entry, string text)
    {
        if (!SelectToCopy.TryCopy(text))
        {
            StatusText.Text = "The clipboard is busy, try again.";
            return;
        }
        entry.IsCopied = true;
        await Task.Delay(1500);
        entry.IsCopied = false;
    }

    // ---- Read aloud ----

    private async void ReadAloudButton_Click(object sender, RoutedEventArgs e)
    {
        if (_speaker.IsSpeaking)
        {
            _speaker.Stop();
            return;
        }

        ReadAloudStatus.Visibility = Visibility.Collapsed;
        var text = ReadAloudBox.Text.Trim();
        if (text.Length == 0)
        {
            ShowReadAloudStatus("Paste or type some text first.");
            return;
        }

        ReadAloudButton.IsEnabled = false;
        try
        {
            await _speaker.SpeakAsync(text, VoiceBox.SelectedItem as SpeakerVoice);
            ReadAloudButton.Content = "⏹  Stop";
        }
        catch (Exception ex)
        {
            ShowReadAloudStatus("The text could not be read: " + ex.Message);
        }
        finally
        {
            ReadAloudButton.IsEnabled = true;
        }
    }

    private void ShowReadAloudStatus(string message)
    {
        ReadAloudStatus.Text = message;
        ReadAloudStatus.Visibility = Visibility.Visible;
    }

    private void Options_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loading) SaveSettings();
    }

    // ---- Settings ----

    private void SaveSettings()
    {
        // Workspaces not shown yet in this session keep the tab names they were saved with.
        foreach (var workspace in _workspaces.Where(w => w.TabsRestored))
            workspace.Options.Tabs = workspace.Tabs.Select(t => t.Title).ToList();
        _settings.SshConnections = _connections.ToList();
        _settings.Workspaces = _workspaces.Select(w => w.Path).ToList();
        _settings.WorkspaceOptions = _workspaces.ToDictionary(w => w.Path, w => w.Options);
        _settings.SelectedWorkspace = Current?.Path;
        _settings.Language = (LanguageBox.SelectedItem as DictationLanguage)?.Code;
        _settings.Model = (ModelBox.SelectedItem as SpeechModel)?.Id ?? "base";
        _settings.Voice = (VoiceBox.SelectedItem as SpeakerVoice)?.Info.Id;
        _settings.Save();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        DocPane.FlushSave();
        DocPane.ClosePreviewWindow();
        SaveSettings();
        _closing = true;
        _transcriber.Dispose();
        _speaker.Dispose();
        foreach (var tab in _workspaces.SelectMany(w => w.Tabs)) tab.View.Dispose();
        base.OnClosing(e);
    }
}
