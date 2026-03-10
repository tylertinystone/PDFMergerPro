namespace VirtualEncryptedDisk;

public interface IPersistableDiskDriver
{
    byte[]? TakeUpdatedDiskBytes();
}
