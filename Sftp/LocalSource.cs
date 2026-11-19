using System.IO;

namespace HB.Sftp;

/// <summary>The files of this computer. The empty path lists the drives.</summary>
public sealed class LocalSource : IFileSource
{
    public LocalSource(string homePath) => HomePath = homePath;

    public string Title => "This computer";
    public bool IsRemote => false;
    public string HomePath { get; }

    public string Combine(string directory, string name) =>
        directory.Length == 0 ? name : Path.Combine(directory, name);

    public string? Parent(string path)
    {
        if (path.Length == 0) return null;
        return Path.GetDirectoryName(path) ?? ""; // null for "C:\": the drives come next
    }

    public Task<List<FileEntry>> ListAsync(string directory) => Task.Run(() =>
    {
        var entries = new List<FileEntry>();
        if (directory.Length == 0)
        {
            foreach (var drive in DriveInfo.GetDrives())
            {
                if (!drive.IsReady) continue;
                var label = drive.VolumeLabel.Length > 0 ? $"{drive.Name} ({drive.VolumeLabel})" : drive.Name;
                entries.Add(new FileEntry(label, drive.RootDirectory.FullName, true, 0, default));
            }
            return entries;
        }

        var info = new DirectoryInfo(directory);
        foreach (var child in info.EnumerateFileSystemInfos())
        {
            if ((child.Attributes & FileAttributes.Hidden) != 0 && child.Name.StartsWith('$')) continue; // $Recycle.Bin and such
            bool isDirectory = (child.Attributes & FileAttributes.Directory) != 0;
            long size = isDirectory ? 0 : ((FileInfo)child).Length;
            entries.Add(new FileEntry(child.Name, child.FullName, isDirectory, size, child.LastWriteTime));
        }
        return entries;
    });

    public Task CreateDirectoryAsync(string path) => Task.Run(() => Directory.CreateDirectory(path));

    public Task DeleteAsync(FileEntry entry) => Task.Run(() =>
    {
        if (entry.IsDirectory) Directory.Delete(entry.FullPath, recursive: true);
        else File.Delete(entry.FullPath);
    });

    public Task RenameAsync(string path, string newPath) => Task.Run(() =>
    {
        if (Directory.Exists(path)) Directory.Move(path, newPath);
        else File.Move(path, newPath);
    });
}
