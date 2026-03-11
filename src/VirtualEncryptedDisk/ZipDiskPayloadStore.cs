namespace VirtualEncryptedDisk;

public sealed class ZipDiskPayloadStore : IDiskPayloadStore
{
    public void Extract(byte[] payloadBytes, string rootDirectory)
        => VirtualDiskArchiveService.ExtractToDirectory(payloadBytes, rootDirectory);

    public byte[] Create(string rootDirectory)
        => VirtualDiskArchiveService.CreateFromDirectory(rootDirectory);
}
