using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace VirtualEncryptedDisk;

public sealed class EncryptionService
{
    private const int SaltSize = 16;
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int KeySize = 32;
    private const int Iterations = 200_000;

    public EncryptedPayload Encrypt(byte[] plainBytes, string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var key = DeriveKey(password, salt);

        var cipher = new byte[plainBytes.Length];
        var tag = new byte[TagSize];

        using var aes = new AesGcm(key, TagSize);
        aes.Encrypt(nonce, plainBytes, cipher, tag);

        return new EncryptedPayload(salt, nonce, tag, cipher);
    }

    public byte[] Decrypt(EncryptedPayload payload, string password)
    {
        var key = DeriveKey(password, payload.Salt);
        var plain = new byte[payload.CipherText.Length];

        using var aes = new AesGcm(key, TagSize);
        aes.Decrypt(payload.Nonce, payload.CipherText, payload.Tag, plain);
        return plain;
    }

    /// <summary>
    /// 分块加密（为后续“按偏移读写”做准备）：每个 chunk 独立 GCM Tag。
    /// </summary>
    public EncryptedChunk EncryptChunk(ReadOnlySpan<byte> plainChunk, string password, byte[] salt, byte[] baseNonce, long chunkIndex)
    {
        ValidateChunkInputs(salt, baseNonce, chunkIndex);
        var key = DeriveKey(password, salt);
        var nonce = CreateChunkNonce(baseNonce, chunkIndex);

        var cipher = new byte[plainChunk.Length];
        var tag = new byte[TagSize];

        using var aes = new AesGcm(key, TagSize);
        aes.Encrypt(nonce, plainChunk, cipher, tag);
        return new EncryptedChunk(chunkIndex, cipher, tag);
    }

    /// <summary>
    /// 分块解密：配合 EncryptChunk 使用。
    /// </summary>
    public byte[] DecryptChunk(EncryptedChunk chunk, string password, byte[] salt, byte[] baseNonce)
    {
        ValidateChunkInputs(salt, baseNonce, chunk.ChunkIndex);
        var key = DeriveKey(password, salt);
        var nonce = CreateChunkNonce(baseNonce, chunk.ChunkIndex);

        var plain = new byte[chunk.CipherText.Length];
        using var aes = new AesGcm(key, TagSize);
        aes.Decrypt(nonce, chunk.CipherText, chunk.Tag, plain);
        return plain;
    }

    public static byte[] GenerateSalt() => RandomNumberGenerator.GetBytes(SaltSize);

    public static byte[] GenerateBaseNonce() => RandomNumberGenerator.GetBytes(NonceSize);

    private static byte[] DeriveKey(string password, byte[] salt)
    {
        return Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password),
            salt,
            Iterations,
            HashAlgorithmName.SHA256,
            KeySize
        );
    }

    private static byte[] CreateChunkNonce(byte[] baseNonce, long chunkIndex)
    {
        var nonce = new byte[NonceSize];
        Buffer.BlockCopy(baseNonce, 0, nonce, 0, NonceSize);

        Span<byte> indexBytes = stackalloc byte[8];
        BinaryPrimitives.WriteInt64LittleEndian(indexBytes, chunkIndex);
        for (var i = 0; i < 8; i++)
        {
            nonce[NonceSize - 8 + i] ^= indexBytes[i];
        }

        return nonce;
    }

    private static void ValidateChunkInputs(byte[] salt, byte[] baseNonce, long chunkIndex)
    {
        if (salt.Length != SaltSize) throw new ArgumentException($"salt 长度必须为 {SaltSize}");
        if (baseNonce.Length != NonceSize) throw new ArgumentException($"baseNonce 长度必须为 {NonceSize}");
        if (chunkIndex < 0) throw new ArgumentOutOfRangeException(nameof(chunkIndex));
    }
}

public sealed record EncryptedPayload(
    byte[] Salt,
    byte[] Nonce,
    byte[] Tag,
    byte[] CipherText
);

public sealed record EncryptedChunk(
    long ChunkIndex,
    byte[] CipherText,
    byte[] Tag
);
