using System.IO;
using System.Net;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Markdig;
using Microsoft.Win32;

namespace HB.Documents;

/// <summary>
/// The Markdown and LaTeX templates: a file browser that can be minimized, a text editor and the
/// output (rendered markdown or the compiled PDF), which can also live in its own window.
/// Edits are saved automatically and the output follows them.
/// </summary>
public partial class DocumentPane : UserControl
{
    private static readonly string[] MarkdownExtensions = [".md", ".markdown", ".mdown", ".txt"];
    private static readonly string[] LatexExtensions = [".tex", ".bib", ".sty", ".cls", ".bst", ".txt"];
    private static readonly string[] ImageExtensions = [".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp", ".svg", ".ico", ".avif"];
    private static readonly MarkdownPipeline Pipeline =
        new MarkdownPipelineBuilder().UseAdvancedExtensions().Use<DisplayMathExtension>().Build();

    private const string NewLatexDocument =
        "\\documentclass{article}\r\n\\usepackage[utf8]{inputenc}\r\n\r\n\\title{Title}\r\n\\author{}\r\n\r\n" +
        "\\begin{document}\r\n\\maketitle\r\n\r\nStart writing here.\r\n\r\n\\end{document}\r\n";

    private enum FileKind { Folder, Document, Image, Pdf, Other }

    private sealed record FileNode(string Path, FileKind Kind);

    private readonly DispatcherTimer _saveTimer = new() { Interval = TimeSpan.FromMilliseconds(800) };
    private readonly DispatcherTimer _renderTimer = new() { Interval = TimeSpan.FromMilliseconds(150) };
    private Workspace? _workspace;
    private string? _file;
    private bool _dirty;
    private bool _loadingFile;

    private PreviewWindow? _window;
    private Action<PreviewView>? _output; // replays the current output on whichever view shows it
    private Action<PreviewView>? _documentOutput; // the document's own output, to come back to after showing another file
    private bool _compiling;
    private bool _compileAgain;
    private string? _shownPdf;
    private string? _shownOtherPdf;

    /// <summary>Raised when something the workspace remembers changed (open file, browser minimized).</summary>
    public event Action? StateChanged;

    public DocumentPane()
    {
        InitializeComponent();
        _saveTimer.Tick += (_, _) =>
        {
            FlushSave();
            if (IsLatex) Compile();
        };
        _renderTimer.Tick += (_, _) => RenderMarkdown();
        UpdatePreviewPlacement();
    }

    private bool IsLatex => _workspace?.IsLatex == true;

    /// <summary>Shows the editor for a workspace, reopening the file it had open.</summary>
    public void Show(Workspace workspace)
    {
        if (_workspace != workspace)
        {
            FlushSave();
            _workspace = workspace;
            _output = _documentOutput = null;
            CompileButton.Visibility = IsLatex ? Visibility.Visible : Visibility.Collapsed;
            BuildTree();
            var file = workspace.Options.DocumentFile;
            OpenFile(file != null && File.Exists(file) ? file : null);
        }
        UpdateBrowserVisibility();
    }

    /// <summary>Called when another kind of workspace takes the column.</summary>
    public void Leave()
    {
        FlushSave();
        _workspace = null;
    }

    /// <summary>Writes pending edits to disk now instead of waiting for the auto-save.</summary>
    public void FlushSave()
    {
        _saveTimer.Stop();
        if (!_dirty || _file == null) return;
        try
        {
            File.WriteAllText(_file, Editor.Text, new UTF8Encoding(false));
            _dirty = false;
            StatusText.Text = $"Saved {DateTime.Now:T}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StatusText.Text = "Not saved: " + ex.Message;
        }
    }

    /// <summary>Reads the file again when something else (the transcriber) wrote to the file being edited.</summary>
    public void ReloadIfOpen(string path)
    {
        if (_file == null || !string.Equals(_file, path, StringComparison.OrdinalIgnoreCase)) return;
        LoadText(_file);
        Editor.CaretIndex = Editor.Text.Length;
        Editor.ScrollToEnd();
    }

    /// <summary>Closes the separate preview window, if any, when the app window goes away.</summary>
    public void ClosePreviewWindow() => _window?.Close();

    // ---- Files ----

    private void OpenFile(string? path)
    {
        FlushSave();
        _file = path;
        if (path != null && !LoadText(path)) _file = null;
        if (_file == null) LoadText(null);

        Editor.IsEnabled = _file != null;
        NoFileHint.Visibility = _file == null ? Visibility.Visible : Visibility.Collapsed;
        FileNameText.Text = _file == null ? "No file selected" : DisplayName(_file);
        FileNameText.ToolTip = _file;
        ErrorBox.Visibility = Visibility.Collapsed;
        if (_window != null) _window.Title = "Preview — " + FileNameText.Text;

        if (_workspace != null && _workspace.Options.DocumentFile != _file)
        {
            _workspace.Options.DocumentFile = _file;
            StateChanged?.Invoke();
        }

        if (IsLatex) Compile();
        else RenderMarkdown();
    }

    private bool LoadText(string? path)
    {
        _loadingFile = true;
        try
        {
            Editor.Text = path == null ? "" : File.ReadAllText(path);
            _dirty = false;
            StatusText.Text = "";
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StatusText.Text = "Could not open the file: " + ex.Message;
            return false;
        }
        finally
        {
            _loadingFile = false;
        }
    }

    private string DisplayName(string file)
    {
        if (_workspace == null) return file;
        var relative = Path.GetRelativePath(_workspace.Path, file);
        return relative.StartsWith("..") ? file : relative;
    }

    private void Editor_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_loadingFile) return;
        _dirty = true;
        StatusText.Text = "Editing…";
        _saveTimer.Stop();
        _saveTimer.Start();
        if (!IsLatex)
        {
            _renderTimer.Stop();
            _renderTimer.Start();
        }
    }

    private void Editor_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.S && Keyboard.Modifiers == ModifierKeys.Control)
        {
            FlushSave();
            if (IsLatex) Compile();
            e.Handled = true;
        }
    }

    private void NewFile_Click(object sender, RoutedEventArgs e)
    {
        if (_workspace == null) return;
        var dialog = new SaveFileDialog
        {
            Title = "New file",
            InitialDirectory = _workspace.Path,
            Filter = IsLatex ? "LaTeX (*.tex)|*.tex|All files|*.*" : "Markdown (*.md)|*.md|All files|*.*",
            DefaultExt = IsLatex ? ".tex" : ".md",
            FileName = IsLatex ? "main.tex" : "notes.md",
            OverwritePrompt = false, // an existing file is opened, never emptied
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        try
        {
            if (!File.Exists(dialog.FileName))
            {
                bool isTex = Path.GetExtension(dialog.FileName).Equals(".tex", StringComparison.OrdinalIgnoreCase);
                File.WriteAllText(dialog.FileName, isTex ? NewLatexDocument : "");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StatusText.Text = "Could not create the file: " + ex.Message;
            return;
        }
        BuildTree();
        OpenFile(dialog.FileName);
    }

    private void OpenOther_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Open a file",
            Filter = IsLatex
                ? "LaTeX files|*.tex;*.bib;*.sty;*.cls;*.bst|All files|*.*"
                : "Markdown and text|*.md;*.markdown;*.mdown;*.txt|All files|*.*",
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true) OpenFile(dialog.FileName);
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => BuildTree();

    // ---- File browser ----

    private void BrowserToggle_Click(object sender, RoutedEventArgs e)
    {
        if (_workspace == null) return;
        _workspace.Options.DocumentBrowserCollapsed = !_workspace.Options.DocumentBrowserCollapsed;
        UpdateBrowserVisibility();
        StateChanged?.Invoke();
        if (_file != null) Editor.Focus();
    }

    private void UpdateBrowserVisibility()
    {
        bool collapsed = _workspace?.Options.DocumentBrowserCollapsed == true;
        BrowserPanel.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
        BrowserToggleButton.Content = collapsed ? "Files ▾" : "Hide files ▴";
    }

    private void BuildTree()
    {
        FileTree.Items.Clear();
        if (_workspace == null) return;
        var root = CreateNode(new FileNode(_workspace.Path, FileKind.Folder), _workspace.Name);
        FileTree.Items.Add(root);
        root.IsExpanded = true;
    }

    private TreeViewItem CreateNode(FileNode node, string name)
    {
        var icon = node.Kind switch
        {
            FileKind.Folder => "📁 ",
            FileKind.Document => "📄 ",
            FileKind.Image => "🖼 ",
            FileKind.Pdf => "📕 ",
            _ => "📎 ",
        };
        var item = new TreeViewItem { Header = icon + name, Tag = node };
        if (node.Kind == FileKind.Folder)
        {
            item.Items.Add(""); // placeholder so the folder can be expanded; filled on first use
            item.Expanded += Folder_Expanded;
        }
        else if (node.Kind == FileKind.Other)
        {
            item.Opacity = 0.6;
            item.ToolTip = "Double-click to open with Windows";
        }
        return item;
    }

    private FileKind KindOf(FileInfo file)
    {
        var extensions = IsLatex ? LatexExtensions : MarkdownExtensions;
        if (extensions.Contains(file.Extension, StringComparer.OrdinalIgnoreCase)) return FileKind.Document;
        if (ImageExtensions.Contains(file.Extension, StringComparer.OrdinalIgnoreCase)) return FileKind.Image;
        if (file.Extension.Equals(".pdf", StringComparison.OrdinalIgnoreCase)) return FileKind.Pdf;
        return FileKind.Other;
    }

    private void Folder_Expanded(object sender, RoutedEventArgs e)
    {
        var item = (TreeViewItem)sender;
        if (e.OriginalSource != item || item.Items.Count != 1 || item.Items[0] is not string) return;
        item.Items.Clear();
        try
        {
            var folder = new DirectoryInfo(((FileNode)item.Tag).Path);
            foreach (var child in folder.EnumerateDirectories().OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase))
            {
                if (IsHidden(child)) continue;
                item.Items.Add(CreateNode(new FileNode(child.FullName, FileKind.Folder), child.Name));
            }
            foreach (var file in folder.EnumerateFiles().OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase))
            {
                if (IsHidden(file)) continue;
                item.Items.Add(CreateNode(new FileNode(file.FullName, KindOf(file)), file.Name));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static bool IsHidden(FileSystemInfo entry) =>
        entry.Name.StartsWith('.') || (entry.Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0;

    private void FileTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is not TreeViewItem { Tag: FileNode node } || node.Kind == FileKind.Folder) return;
        switch (node.Kind)
        {
            case FileKind.Document:
                if (string.Equals(node.Path, _file, StringComparison.OrdinalIgnoreCase))
                {
                    // Back to the document after looking at another file.
                    if (_documentOutput != null && _output != _documentOutput) Present(_documentOutput);
                }
                else OpenFile(node.Path);
                break;
            case FileKind.Image:
                ShowImage(node.Path);
                break;
            case FileKind.Pdf:
                StatusText.Text = $"Showing {Path.GetFileName(node.Path)}";
                ShowPdf(node.Path, isDocumentOutput: false);
                break;
            default:
                StatusText.Text = $"Double-click to open {Path.GetFileName(node.Path)} with Windows";
                break;
        }
    }

    private void FileTree_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (FileTree.SelectedItem is not TreeViewItem { Tag: FileNode { Kind: FileKind.Other } node }) return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(node.Path) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            StatusText.Text = "Could not open the file: " + ex.Message;
        }
    }

    /// <summary>Shows an image of the workspace in the preview; the next edit brings the document back.</summary>
    private void ShowImage(string path)
    {
        var name = Path.GetFileName(path);
        StatusText.Text = $"Showing {name}";
        var html = $"<p><img src=\"{WebUtility.HtmlEncode(Uri.EscapeDataString(name))}\" alt=\"{WebUtility.HtmlEncode(name)}\"></p>" +
                   $"<p id=\"empty\">{WebUtility.HtmlEncode(name)}</p>";
        var folder = Path.GetDirectoryName(path);
        Present(view => view.ShowHtml(html, folder), isDocumentOutput: false);
    }

    // ---- Output ----

    /// <summary>Sends the output to the view that currently shows it and remembers it for the other one.</summary>
    private void Present(Action<PreviewView> output, bool isDocumentOutput = true)
    {
        _output = output;
        if (isDocumentOutput) _documentOutput = output;
        output(_window?.View ?? DockedPreview);
    }

    private void RenderMarkdown()
    {
        _renderTimer.Stop();
        if (_file == null)
        {
            Present(view => view.ShowHtml("", null));
            return;
        }
        var html = Markdown.ToHtml(Editor.Text, Pipeline);
        var folder = Path.GetDirectoryName(_file);
        Present(view => view.ShowHtml(html, folder));
    }

    private void Compile_Click(object sender, RoutedEventArgs e)
    {
        FlushSave();
        Compile();
    }

    /// <summary>Builds the PDF of the main LaTeX file; changes made meanwhile trigger another build.</summary>
    private async void Compile()
    {
        if (_workspace is not { } workspace) return;
        if (_compiling)
        {
            _compileAgain = true;
            return;
        }

        var main = FindMainFile(workspace);
        if (main == null)
        {
            Present(view => view.ShowMessage(
                "Open the main .tex file (the one with \\documentclass) to see its PDF here."));
            return;
        }

        _compiling = true;
        StatusText.Text = $"Compiling {Path.GetFileName(main)}…";
        if (_output == null) Present(view => view.ShowMessage("Compiling…"));
        var result = await LatexCompiler.CompileAsync(main);
        _compiling = false;

        if (_workspace == workspace)
        {
            if (result.Success)
            {
                ErrorBox.Visibility = Visibility.Collapsed;
                StatusText.Text = $"PDF updated {DateTime.Now:T}";
                ShowPdf(result.PdfPath!);
            }
            else
            {
                ErrorBox.Text = result.Errors;
                ErrorBox.Visibility = Visibility.Visible;
                StatusText.Text = "Compilation failed";
            }
        }

        if (_compileAgain)
        {
            _compileAgain = false;
            Compile();
        }
    }

    /// <summary>
    /// The file being edited when it is a whole document; otherwise the last document compiled in
    /// this workspace, so that editing a chapter it includes rebuilds the right PDF.
    /// </summary>
    private string? FindMainFile(Workspace workspace)
    {
        if (_file != null && Path.GetExtension(_file).Equals(".tex", StringComparison.OrdinalIgnoreCase) &&
            Editor.Text.Contains("\\documentclass"))
        {
            if (workspace.Options.LatexMainFile != _file)
            {
                workspace.Options.LatexMainFile = _file;
                StateChanged?.Invoke();
            }
            return _file;
        }
        var saved = workspace.Options.LatexMainFile;
        return saved != null && File.Exists(saved) ? saved : null;
    }

    private void ShowPdf(string pdf, bool isDocumentOutput = true)
    {
        // Each PDF is shown from a fresh copy: the viewer keeps its file open, and the compiler
        // (or the user) must be able to overwrite the original.
        var copy = Path.Combine(PreviewView.PdfFolder, Guid.NewGuid().ToString("N") + ".pdf");
        try
        {
            File.Copy(pdf, copy);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StatusText.Text = "The PDF could not be shown: " + ex.Message;
            return;
        }
        // The document's copy outlives a PDF looked at from the file browser, so the preview can return to it.
        var previous = isDocumentOutput ? _shownPdf : _shownOtherPdf;
        if (isDocumentOutput) _shownPdf = copy;
        else _shownOtherPdf = copy;
        Present(view => view.ShowPdf(copy), isDocumentOutput);
        if (previous != null)
        {
            try { File.Delete(previous); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } // still open in the viewer
        }
    }

    // ---- Separate preview window ----

    private void PopOut_Click(object sender, RoutedEventArgs e)
    {
        if (_window != null)
        {
            _window.Close();
            return;
        }

        var window = new PreviewWindow { Title = "Preview — " + FileNameText.Text };
        window.Closed += (_, _) =>
        {
            _window = null;
            UpdatePreviewPlacement();
            _output?.Invoke(DockedPreview);
        };
        _window = window;
        window.Show();
        UpdatePreviewPlacement();
        _output?.Invoke(window.View);
    }

    /// <summary>While the output is in its own window, the editor takes the room it used here.</summary>
    private void UpdatePreviewPlacement()
    {
        bool separate = _window != null;
        DockedPreview.Visibility = separate ? Visibility.Collapsed : Visibility.Visible;
        PreviewRow.Height = separate ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        SplitterRow.Height = new GridLength(separate ? 0 : 5);
        PopOutButton.Content = separate ? "Dock preview ⤓" : "Pop out preview ⧉";
    }
}
