namespace VirtualEncryptedDisk;

public sealed class PlainFileContentStore : IFileContentStore
{
    public bool Exists(string path) => File.Exists(path);

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

    public void EnsureParentDirectory(string path)
    {
        var parent = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(parent))
        {
            Directory.CreateDirectory(parent);
        }
    }
}
