using System.Globalization;

namespace HB.Sftp;

/// <summary>A file or folder shown in one of the panes of the SFTP window.</summary>
public sealed record FileEntry(string Name, string FullPath, bool IsDirectory, long Size, DateTime Modified)
{
    public string Glyph => IsDirectory ? "📁" : "📄";
    public string SizeText => IsDirectory ? "" : FormatSize(Size);
    public string ModifiedText => Modified == default ? "" : Modified.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

    public static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024):0.#} MB",
        _ => $"{bytes / (1024.0 * 1024 * 1024):0.##} GB",
    };
}

/// <summary>
/// One side of the SFTP window: this computer or the remote device. Paths are in the form of
/// that side (C:\… or /…); the pane never needs to know which.
/// </summary>
public interface IFileSource
{
    string Title { get; }
    bool IsRemote { get; }

    /// <summary>Where browsing starts when no path is given.</summary>
    string HomePath { get; }

    string Combine(string directory, string name);

    /// <summary>The folder above, or null at the top.</summary>
    string? Parent(string path);

    Task<List<FileEntry>> ListAsync(string directory);
    Task CreateDirectoryAsync(string path);

    /// <summary>Deletes a file, or a folder with everything in it.</summary>
    Task DeleteAsync(FileEntry entry);

    Task RenameAsync(string path, string newPath);
}
