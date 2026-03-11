namespace VirtualEncryptedDisk;

public interface IFileContentStore
{
    bool Exists(string path);
    int Read(string path, Span<byte> buffer, long offset);
    void Write(string path, ReadOnlySpan<byte> buffer, long offset, bool writeToEndOfFile);
    void SetLength(string path, long length);
    long GetLength(string path);
    void EnsureParentDirectory(string path);
}
