namespace VirtualEncryptedDisk;

public interface IFileContentStore
{
    bool Exists(string path);
    bool IsDirectory(string path);
    int Read(string path, Span<byte> buffer, long offset);
    void Write(string path, ReadOnlySpan<byte> buffer, long offset, bool writeToEndOfFile);
    void SetLength(string path, long length);
    long GetLength(string path);
    FileAttributes GetAttributes(string path);
    DateTime GetCreationTime(string path);
    DateTime GetLastAccessTime(string path);
    DateTime GetLastWriteTime(string path);
    IEnumerable<string> EnumerateFileSystemEntries(string path);
    void SetAttributes(string path, FileAttributes attributes);
    void SetCreationTime(string path, DateTime creationTime);
    void SetLastAccessTime(string path, DateTime lastAccessTime);
    void SetLastWriteTime(string path, DateTime lastWriteTime);
    void EnsureParentDirectory(string path);
    void Move(string oldPath, string newPath, bool replace, bool isDirectory);
}
