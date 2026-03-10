using System.Buffers.Binary;
using System.Text;

namespace VirtualEncryptedDisk;

public sealed class ContainerFileService
{
    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("VED1");

    public void Write(string path, EncryptedPayload payload)
    {
        using var fs = File.Create(path);
        fs.Write(Magic);
        WriteChunk(fs, payload.Salt);
        WriteChunk(fs, payload.Nonce);
        WriteChunk(fs, payload.Tag);
        WriteChunk(fs, payload.CipherText);
    }

    public EncryptedPayload Read(string path)
    {
        using var fs = File.OpenRead(path);

        Span<byte> magic = stackalloc byte[4];
        fs.ReadExactly(magic);
        if (!magic.SequenceEqual(Magic))
        {
            throw new InvalidDataException("不是受支持的加密容器格式。");
        }

        var salt = ReadChunk(fs);
        var nonce = ReadChunk(fs);
        var tag = ReadChunk(fs);
        var cipher = ReadChunk(fs);

        return new EncryptedPayload(salt, nonce, tag, cipher);
    }

    private static void WriteChunk(Stream stream, byte[] bytes)
    {
        Span<byte> len = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(len, bytes.Length);
        stream.Write(len);
        stream.Write(bytes);
    }

    private static byte[] ReadChunk(Stream stream)
    {
        Span<byte> len = stackalloc byte[4];
        stream.ReadExactly(len);
        var size = BinaryPrimitives.ReadInt32LittleEndian(len);
        if (size < 0 || size > 1024 * 1024 * 1024)
        {
            throw new InvalidDataException("容器字段长度无效。");
        }

        var buffer = new byte[size];
        stream.ReadExactly(buffer);
        return buffer;
    }
}
