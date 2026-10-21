using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using HB.Terminal;

namespace HB;

public static class AppPaths
{
    /// <summary>Where accounts, settings and models are kept; HB_DATA_DIR overrides the default location.</summary>
    public static string DataFolder { get; } = Directory.CreateDirectory(
        Environment.GetEnvironmentVariable("HB_DATA_DIR") ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HB")).FullName;
}

/// <summary>What a workspace offers besides the common layout. New templates are added to <see cref="All"/>.</summary>
public sealed record WorkspaceTemplate(string Id, string Name, string Description)
{
    public const string DefaultId = "default";
    public const string SshId = "ssh";
    public const string MarkdownId = "markdown";
    public const string LatexId = "latex";

    public static IReadOnlyList<WorkspaceTemplate> All { get; } =
    [
        new(DefaultId, "Default", "PowerShell tabs and the transcriber."),
        new(SshId, "SSH", "Adds a connection bar to the terminal to log in to another device over SSH."),
        new(MarkdownId, "Markdown", "A markdown editor with a file browser and a live preview, instead of terminals."),
        new(LatexId, "LaTeX", "A LaTeX editor with a file browser and the compiled PDF, instead of terminals."),
    ];
}

/// <summary>Per-workspace options kept in the settings file.</summary>
public sealed class WorkspaceOptions
{
    public string Template { get; set; } = WorkspaceTemplate.DefaultId;
    public string SshHost { get; set; } = "";
    public int SshPort { get; set; } = 22;
    public string SshUser { get; set; } = "";

    /// <summary>True when the SSH connection bar is minimized to leave the whole area to the terminal.</summary>
    public bool SshBarCollapsed { get; set; }

    /// <summary>
    /// Markdown and LaTeX templates: the file open in the editor and whether the file browser is
    /// minimized. The JSON names date from when only the Markdown template existed.
    /// </summary>
    [JsonPropertyName("MarkdownFile")]
    public string? DocumentFile { get; set; }

    [JsonPropertyName("MarkdownBrowserCollapsed")]
    public bool DocumentBrowserCollapsed { get; set; }

    /// <summary>LaTeX template: the document that is compiled while a file it includes is being edited.</summary>
    public string? LatexMainFile { get; set; }

    /// <summary>Names of the terminal tabs, restored when the workspace is opened again.</summary>
    public List<string> Tabs { get; set; } = new();
}

/// <summary>A saved SSH login. The password is encrypted with the app user's key.</summary>
public sealed class SshConnection
{
    /// <summary>What the user calls this device; may be empty.</summary>
    public string Name { get; set; } = "";
    public string Host { get; set; } = "";
    public int Port { get; set; } = 22;
    public string User { get; set; } = "";
    public string? PasswordProtected { get; set; }

    [JsonIgnore]
    public string Address => Port == 22 ? $"{User}@{Host}" : $"{User}@{Host}:{Port}";

    [JsonIgnore]
    public string Label => Name.Length == 0 ? Address : $"{Name} — {Address}";

    public bool Matches(string host, int port, string user) =>
        Port == port && string.Equals(Host, host, StringComparison.OrdinalIgnoreCase) && User == user;
}

/// <summary>A folder the user works in: it owns its terminals and its transcription file.</summary>
public sealed class Workspace
{
    public Workspace(string path, WorkspaceOptions? options = null)
    {
        Path = path;
        Name = new DirectoryInfo(path).Name;
        Options = options ?? new WorkspaceOptions();
    }

    public string Name { get; }
    public string Path { get; }
    public WorkspaceOptions Options { get; }
    public bool IsSsh => Options.Template == WorkspaceTemplate.SshId;
    public bool IsLatex => Options.Template == WorkspaceTemplate.LatexId;

    /// <summary>True for the templates that show the document editor instead of terminals.</summary>
    public bool IsDocument => IsLatex || Options.Template == WorkspaceTemplate.MarkdownId;

    /// <summary>The markdown file that receives this workspace's transcriptions.</summary>
    public string MarkdownPath => System.IO.Path.Combine(Path, Name + ".md");

    public ObservableCollection<TerminalTab> Tabs { get; } = new();
    public TerminalTab? SelectedTab { get; set; }
    public int TabCounter { get; set; }

    public ObservableCollection<TranscriptEntry> Entries { get; } = new();
    public bool EntriesLoaded { get; set; }
    public bool TabsRestored { get; set; }
}

public sealed class TerminalTab : INotifyPropertyChanged
{
    private string _title;
    private bool _isEditing;

    public TerminalTab(string title, TerminalView view)
    {
        _title = title;
        View = view;
    }

    public TerminalView View { get; }

    public string Title
    {
        get => _title;
        set
        {
            _title = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Title)));
        }
    }

    /// <summary>True while the user is renaming the tab.</summary>
    public bool IsEditing
    {
        get => _isEditing;
        set
        {
            _isEditing = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsEditing)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed class TranscriptEntry : INotifyPropertyChanged
{
    private bool _isCopied;

    public TranscriptEntry(DateTime timestamp, string language, string text)
    {
        Timestamp = timestamp;
        Language = language;
        Text = text;
    }

    public DateTime Timestamp { get; }
    public string Language { get; }
    public string Text { get; }
    public string Header => string.IsNullOrEmpty(Language) ? $"{Timestamp:g}" : $"{Timestamp:g} · {Language}";

    /// <summary>True for a moment after the entry was copied, to show feedback.</summary>
    public bool IsCopied
    {
        get => _isCopied;
        set
        {
            _isCopied = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsCopied)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed record DictationLanguage(string Code, string Name)
{
    public static IReadOnlyList<DictationLanguage> All { get; } =
    [
        new("auto", "Auto-detect"),
        new("ar", "Arabic"),
        new("ca", "Catalan"),
        new("zh", "Chinese"),
        new("cs", "Czech"),
        new("da", "Danish"),
        new("nl", "Dutch"),
        new("en", "English"),
        new("fi", "Finnish"),
        new("fr", "French"),
        new("gl", "Galician"),
        new("de", "German"),
        new("el", "Greek"),
        new("he", "Hebrew"),
        new("hi", "Hindi"),
        new("hu", "Hungarian"),
        new("id", "Indonesian"),
        new("it", "Italian"),
        new("ja", "Japanese"),
        new("ko", "Korean"),
        new("no", "Norwegian"),
        new("pl", "Polish"),
        new("pt", "Portuguese"),
        new("ro", "Romanian"),
        new("ru", "Russian"),
        new("es", "Spanish"),
        new("sv", "Swedish"),
        new("th", "Thai"),
        new("tr", "Turkish"),
        new("uk", "Ukrainian"),
        new("vi", "Vietnamese"),
    ];
}

public sealed record SpeechModel(string Id, string Name)
{
    public static IReadOnlyList<SpeechModel> All { get; } =
    [
        new("base", "Fast (142 MB)"),
        new("small", "More accurate (466 MB)"),
    ];
}

public sealed class AppSettings
{
    private string _filePath = "";

    public List<string> Workspaces { get; set; } = new();

    /// <summary>Options of each workspace, keyed by its path.</summary>
    public Dictionary<string, WorkspaceOptions> WorkspaceOptions { get; set; } = new();
    public List<SshConnection> SshConnections { get; set; } = new();
    public string? SelectedWorkspace { get; set; }
    public string? Language { get; set; }
    public string Model { get; set; } = "base";
    public string? Voice { get; set; }

    /// <summary>Loads the settings of one user; they are saved back to the same file.</summary>
    public static AppSettings Load(string filePath)
    {
        AppSettings? settings = null;
        try
        {
            if (File.Exists(filePath))
                settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(filePath));
        }
        catch (Exception ex) when (ex is IOException or JsonException) { }
        settings ??= new();
        settings._filePath = filePath;
        return settings;
    }

    public void Save()
    {
        try
        {
            File.WriteAllText(_filePath,
                JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (IOException) { }
    }
}
