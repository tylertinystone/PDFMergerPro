namespace VirtualEncryptedDisk;

public sealed class PlainFileContentStore : IFileContentStore
{
    public bool Exists(string path) => File.Exists(path) || Directory.Exists(path);

    public bool IsDirectory(string path) => Directory.Exists(path);

    public int Read(string path, Span<byte> buffer, long offset)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (offset >= fs.Length)
        {
            return 0;
        }

        fs.Position = offset;
        return fs.Read(buffer);
    }

    public void Write(string path, ReadOnlySpan<byte> buffer, long offset, bool writeToEndOfFile)
    {
        using var fs = new FileStream(path, FileMode.OpenOrCreate, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        fs.Position = writeToEndOfFile ? fs.Length : offset;
        fs.Write(buffer);
    }

    public void SetLength(string path, long length)
    {
        using var fs = new FileStream(path, FileMode.OpenOrCreate, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        fs.SetLength(length);
    }

    public long GetLength(string path)
    {
        var info = new FileInfo(path);
        return info.Length;
    }

    public FileAttributes GetAttributes(string path)
    {
        return IsDirectory(path)
            ? new DirectoryInfo(path).Attributes
            : new FileInfo(path).Attributes;
    }

    public DateTime GetCreationTime(string path)
    {
        return IsDirectory(path)
            ? new DirectoryInfo(path).CreationTime
            : new FileInfo(path).CreationTime;
    }

    public DateTime GetLastAccessTime(string path)
    {
        return IsDirectory(path)
            ? new DirectoryInfo(path).LastAccessTime
            : new FileInfo(path).LastAccessTime;
    }

    public DateTime GetLastWriteTime(string path)
    {
        return IsDirectory(path)
            ? new DirectoryInfo(path).LastWriteTime
            : new FileInfo(path).LastWriteTime;
    }

    public void EnsureParentDirectory(string path)
    {
        var parent = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(parent))
        {
            Directory.CreateDirectory(parent);
        }
    }
}
