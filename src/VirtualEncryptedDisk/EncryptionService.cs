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
}

public sealed record EncryptedPayload(
    byte[] Salt,
    byte[] Nonce,
    byte[] Tag,
    byte[] CipherText
);
