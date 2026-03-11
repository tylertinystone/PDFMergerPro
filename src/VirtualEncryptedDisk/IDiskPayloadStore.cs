namespace VirtualEncryptedDisk;

public interface IDiskPayloadStore
{
    void Extract(byte[] payloadBytes, string rootDirectory);
    byte[] Create(string rootDirectory);
    byte[] CreateEmpty();
}
