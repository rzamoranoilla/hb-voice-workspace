using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace HB.Sftp;

/// <summary>Files dragged from one pane: which side they are on and their paths.</summary>
public sealed record DragPayload(bool FromRemote, IReadOnlyList<string> Paths);

/// <summary>Files dropped on a pane, to be copied into <see cref="TargetDirectory"/>.</summary>
public sealed record DropRequest(IReadOnlyList<string> Paths, string TargetDirectory, bool FromRemote);

/// <summary>A row of the list: the entry plus the state the list needs.</summary>
public sealed class FileRow(FileEntry entry) : INotifyPropertyChanged
{
    private bool _isDropTarget;

    public FileEntry Entry { get; } = entry;

    public bool IsDropTarget
    {
        get => _isDropTarget;
        set
        {
            if (_isDropTarget == value) return;
            _isDropTarget = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsDropTarget)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>
/// One side of the SFTP window: a folder of this computer or of the device, with the files in it.
/// Folders open with a double click; files are dragged to the other pane (or dropped here).
/// </summary>
public partial class FilePane : UserControl
{
    private readonly ObservableCollection<FileRow> _rows = [];
    private IFileSource? _source;
    private string _path = "";
    private int _listing; // increases with each listing, so a slow one never replaces a newer one
    private Point? _dragStart;
    private FileRow? _deferredClick;
    private FileRow? _dropTarget;

    public FilePane()
    {
        InitializeComponent();
        List.ItemsSource = _rows;
    }

    /// <summary>The user wants these entries copied to the other pane (double-click, Enter, menu).</summary>
    public event Action<IReadOnlyList<FileEntry>>? TransferRequested;

    /// <summary>Files were dropped on this pane.</summary>
    public event Action<DropRequest>? DropReceived;

    /// <summary>The pane shows another folder now.</summary>
    public event Action<string>? Navigated;

    /// <summary>Something went wrong; the window shows it.</summary>
    public event Action<string>? Failed;

    public IFileSource? Source => _source;
    public string CurrentPath => _path;

    /// <summary>What the "transfer" command is called here: files go to the other side.</summary>
    public string TransferVerb { get; set; } = "Transfer";

    public IReadOnlyList<FileEntry> SelectedEntries =>
        List.SelectedItems.Cast<FileRow>().Select(r => r.Entry).ToList();

    /// <summary>Shows a side; <paramref name="startPath"/> is tried first, then the source's home.</summary>
    public async Task ShowAsync(IFileSource source, string? startPath)
    {
        _source = source;
        TitleText.Text = source.Title;
        TransferMenuItem.Header = TransferVerb;
        // The remembered folder may be gone: then the home folder, without a complaint.
        if (startPath != null && await TryNavigateAsync(startPath, quiet: true)) return;
        await TryNavigateAsync(source.HomePath);
    }

    /// <summary>Clears the pane, e.g. when the connection is gone.</summary>
    public void Clear()
    {
        _source = null;
        _rows.Clear();
        PathBox.Text = "";
        StatusText.Text = "";
    }

    public Task RefreshAsync() => _source == null ? Task.CompletedTask : TryNavigateAsync(_path);

    /// <summary>Lists a folder; false (with the reason shown) when it cannot be read.</summary>
    public async Task<bool> TryNavigateAsync(string path, bool quiet = false)
    {
        if (_source == null) return false;
        int listing = ++_listing;
        StatusText.Text = "Reading…";
        List<FileEntry> entries;
        try
        {
            entries = await _source.ListAsync(path);
        }
        catch (Exception ex) when (ex is SftpException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            if (listing != _listing) return false;
            StatusText.Text = ex.Message;
            if (!quiet) Failed?.Invoke(ex.Message);
            return false;
        }
        if (listing != _listing) return false;

        _path = path;
        PathBox.Text = path.Length == 0 ? "Drives" : path;
        _rows.Clear();
        foreach (var entry in entries.OrderBy(e => !e.IsDirectory).ThenBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase))
            _rows.Add(new FileRow(entry));
        UpdateStatus();
        Navigated?.Invoke(path);
        return true;
    }

    private void UpdateStatus()
    {
        int folders = _rows.Count(r => r.Entry.IsDirectory);
        int files = _rows.Count - folders;
        var text = $"{Plural(folders, "folder")}, {Plural(files, "file")}";
        if (List.SelectedItems.Count > 0)
        {
            long bytes = List.SelectedItems.Cast<FileRow>().Where(r => !r.Entry.IsDirectory).Sum(r => r.Entry.Size);
            text += $" · {List.SelectedItems.Count} selected" + (bytes > 0 ? $" ({FileEntry.FormatSize(bytes)})" : "");
        }
        StatusText.Text = text;
    }

    private static string Plural(int count, string noun) => count == 1 ? $"1 {noun}" : $"{count} {noun}s";

    // ---- Navigation ----

    private async void Up_Click(object sender, RoutedEventArgs e) => await GoUpAsync();

    private async Task GoUpAsync()
    {
        if (_source?.Parent(_path) is { } parent) await TryNavigateAsync(parent);
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAsync();

    private async void PathBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        var path = PathBox.Text.Trim();
        if (string.Equals(path, "Drives", StringComparison.OrdinalIgnoreCase) && _source is { IsRemote: false }) path = "";
        if (await TryNavigateAsync(path)) List.Focus();
    }

    private async void List_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (RowAt(e.GetPosition(List)) is { } row) await OpenAsync(row.Entry);
    }

    private async Task OpenAsync(FileEntry entry)
    {
        if (entry.IsDirectory) await TryNavigateAsync(entry.FullPath);
        else TransferRequested?.Invoke([entry]);
    }

    private async void Open_Click(object sender, RoutedEventArgs e)
    {
        if (List.SelectedItem is FileRow row) await OpenAsync(row.Entry);
    }

    private void Transfer_Click(object sender, RoutedEventArgs e)
    {
        var selected = SelectedEntries;
        if (selected.Count > 0) TransferRequested?.Invoke(selected);
    }

    private async void List_KeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter when List.SelectedItems.Count == 1 && List.SelectedItem is FileRow row:
                e.Handled = true;
                await OpenAsync(row.Entry);
                break;
            case Key.Enter when List.SelectedItems.Count > 1:
                e.Handled = true;
                TransferRequested?.Invoke(SelectedEntries);
                break;
            case Key.Back:
                e.Handled = true;
                await GoUpAsync();
                break;
            case Key.F5:
                e.Handled = true;
                await RefreshAsync();
                break;
            case Key.Delete:
                e.Handled = true;
                await DeleteSelectedAsync();
                break;
            case Key.F2:
                e.Handled = true;
                await RenameSelectedAsync();
                break;
        }
    }

    private void List_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateStatus();

    /// <summary>The name column takes the width the other two leave.</summary>
    private void List_SizeChanged(object sender, SizeChangedEventArgs e) =>
        NameColumn.Width = Math.Max(120, List.ActualWidth - 90 - 130 - 28);

    // ---- Folder commands ----

    private async void NewFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_source == null) return;
        var name = TextPromptDialog.Ask(Window.GetWindow(this), "New folder", "Name of the new folder:", "New folder");
        if (string.IsNullOrWhiteSpace(name)) return;
        await RunAsync(() => _source.CreateDirectoryAsync(_source.Combine(_path, name.Trim())));
    }

    private async void Rename_Click(object sender, RoutedEventArgs e) => await RenameSelectedAsync();

    private async Task RenameSelectedAsync()
    {
        if (_source == null || List.SelectedItem is not FileRow row || _path.Length == 0) return;
        var name = TextPromptDialog.Ask(Window.GetWindow(this), "Rename", $"New name for {row.Entry.Name}:", row.Entry.Name);
        if (string.IsNullOrWhiteSpace(name) || name == row.Entry.Name) return;
        await RunAsync(() => _source.RenameAsync(row.Entry.FullPath, _source.Combine(_path, name.Trim())));
    }

    private async void Delete_Click(object sender, RoutedEventArgs e) => await DeleteSelectedAsync();

    private async Task DeleteSelectedAsync()
    {
        if (_source == null) return;
        var selected = SelectedEntries;
        if (selected.Count == 0 || _path.Length == 0) return;
        var what = selected.Count == 1 ? selected[0].Name : $"{selected.Count} items";
        var note = selected.Any(s => s.IsDirectory) ? " Folders are deleted with everything in them." : "";
        if (MessageBox.Show(Window.GetWindow(this), $"Delete {what} from {_source.Title}?{note}", "Delete",
                MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK)
            return;
        await RunAsync(async () =>
        {
            foreach (var entry in selected) await _source.DeleteAsync(entry);
        });
    }

    /// <summary>Runs a command on the folder, shows a failure, and reads the folder again.</summary>
    private async Task RunAsync(Func<Task> work)
    {
        try { await work(); }
        catch (Exception ex) when (ex is SftpException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            Failed?.Invoke(ex.Message);
        }
        await RefreshAsync();
    }

    // ---- Dragging out ----

    private FileRow? RowAt(Point position) =>
        List.InputHitTest(position) is DependencyObject hit &&
        ItemsControl.ContainerFromElement(List, hit) is ListViewItem item
            ? item.DataContext as FileRow
            : null;

    private void List_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _deferredClick = null;
        var row = RowAt(e.GetPosition(List));
        _dragStart = row != null ? e.GetPosition(List) : null;
        // A press on one of several selected rows would shrink the selection to that row at
        // once, so several files could never be dragged together: the click is applied on
        // release instead, unless a drag started meanwhile.
        if (row != null && List.SelectedItems.Count > 1 && List.SelectedItems.Contains(row) &&
            Keyboard.Modifiers == ModifierKeys.None && e.ClickCount == 1)
        {
            _deferredClick = row;
            e.Handled = true;
            List.Focus(); // the list would have taken the focus with the click
        }
    }

    private void List_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_deferredClick is { } row)
        {
            List.SelectedItems.Clear();
            List.SelectedItem = row;
            _deferredClick = null;
        }
        _dragStart = null;
    }

    private void List_MouseMove(object sender, MouseEventArgs e)
    {
        if (_dragStart is not { } start || e.LeftButton != MouseButtonState.Pressed || _source == null) return;
        var delta = e.GetPosition(List) - start;
        if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;

        _dragStart = null;
        _deferredClick = null;
        var paths = SelectedEntries.Select(entry => entry.FullPath).ToList();
        if (paths.Count == 0) return;

        var data = new DataObject();
        data.SetData(typeof(DragPayload), new DragPayload(_source.IsRemote, paths));
        // Local files can also be dropped on Explorer and other programs.
        if (!_source.IsRemote) data.SetData(DataFormats.FileDrop, paths.ToArray());
        DragDrop.DoDragDrop(List, data, DragDropEffects.Copy);
    }

    // ---- Dropping in ----

    /// <summary>The paths being dragged, when this pane can take them.</summary>
    private (IReadOnlyList<string> Paths, bool FromRemote)? Dropped(IDataObject data)
    {
        if (_source == null || _path.Length == 0) return null;
        if (data.GetData(typeof(DragPayload)) is DragPayload payload)
            return payload.FromRemote == _source.IsRemote ? null : (payload.Paths, payload.FromRemote);
        // Files from Explorer go to the device; on the local side Explorer itself is the place to copy.
        if (_source.IsRemote && data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
            return (files, false);
        return null;
    }

    private void List_DragOver(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (Dropped(e.Data) == null)
        {
            e.Effects = DragDropEffects.None;
            SetDropTarget(null);
            return;
        }
        e.Effects = DragDropEffects.Copy;
        var row = RowAt(e.GetPosition(List));
        SetDropTarget(row is { Entry.IsDirectory: true } ? row : null);
    }

    private void List_DragLeave(object sender, DragEventArgs e) => SetDropTarget(null);

    private void List_Drop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        var target = _dropTarget?.Entry.FullPath ?? _path;
        SetDropTarget(null);
        if (Dropped(e.Data) is not { } dropped) return;
        // Not into itself or a folder of its own
        var paths = dropped.Paths.Where(p => !IsSameOrInside(target, p)).ToList();
        if (paths.Count > 0) DropReceived?.Invoke(new DropRequest(paths, target, dropped.FromRemote));
    }

    private static bool IsSameOrInside(string target, string path)
    {
        var a = target.TrimEnd('/', '\\');
        var b = path.TrimEnd('/', '\\');
        return string.Equals(a, b, StringComparison.OrdinalIgnoreCase) ||
               (a.Length > b.Length && a.StartsWith(b, StringComparison.OrdinalIgnoreCase) && a[b.Length] is '/' or '\\');
    }

    private void SetDropTarget(FileRow? row)
    {
        if (_dropTarget == row) return;
        if (_dropTarget != null) _dropTarget.IsDropTarget = false;
        _dropTarget = row;
        if (row != null) row.IsDropTarget = true;
    }
}

/// <summary>A small dialog that asks for one line of text.</summary>
public static class TextPromptDialog
{
    public static string? Ask(Window? owner, string title, string question, string initial)
    {
        var box = new TextBox { Text = initial, Padding = new Thickness(6, 4, 6, 4), Margin = new Thickness(0, 6, 0, 16) };
        var ok = new Button { Content = "OK", IsDefault = true, MinWidth = 80, Padding = new Thickness(0, 6, 0, 6) };
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 80, Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(0, 6, 0, 6) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        var panel = new StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(new TextBlock { Text = question, TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(box);
        panel.Children.Add(buttons);
        var window = new Window
        {
            Title = title,
            Content = panel,
            Width = 420,
            SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = owner,
            ShowInTaskbar = false,
        };
        ok.Click += (_, _) => window.DialogResult = true;
        window.Loaded += (_, _) =>
        {
            box.Focus();
            // The name without its extension is selected, as Explorer does.
            int dot = initial.LastIndexOf('.');
            box.Select(0, dot > 0 ? dot : initial.Length);
        };
        return window.ShowDialog() == true ? box.Text : null;
    }
}
