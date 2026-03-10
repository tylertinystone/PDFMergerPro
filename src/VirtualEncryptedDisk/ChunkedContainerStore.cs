using System.Text;

namespace VirtualEncryptedDisk;

/// <summary>
/// 阶段2/3：分块容器存储（独立于现有 zip 运行时路径）。
/// 提供按 chunk 的加密读写与元数据索引能力。
/// </summary>
public sealed class ChunkedContainerStore
{
    private const string Magic = "VEC2";

    private readonly EncryptionService _encryption;
    private readonly string _containerPath;
    private readonly string _password;

    private readonly Dictionary<long, EncryptedChunk> _chunks = new();

    private byte[] _salt = Array.Empty<byte>();
    private byte[] _baseNonce = Array.Empty<byte>();

    public int ChunkSize { get; }

    public ChunkedContainerStore(EncryptionService encryption, string containerPath, string password, int chunkSize = 1 * 1024 * 1024)
    {
        if (chunkSize <= 0) throw new ArgumentOutOfRangeException(nameof(chunkSize));
        _encryption = encryption;
        _containerPath = containerPath;
        _password = password;
        ChunkSize = chunkSize;
    }

    public static ChunkedContainerStore CreateNew(EncryptionService encryption, string containerPath, string password, int chunkSize = 1 * 1024 * 1024)
    {
        var store = new ChunkedContainerStore(encryption, containerPath, password, chunkSize)
        {
            _salt = EncryptionService.GenerateSalt(),
            _baseNonce = EncryptionService.GenerateBaseNonce()
        };
        store.Flush();
        return store;
    }

    public static ChunkedContainerStore Open(EncryptionService encryption, string containerPath, string password)
    {
        var bytes = File.ReadAllBytes(containerPath);
        using var ms = new MemoryStream(bytes);
        using var br = new BinaryReader(ms, Encoding.UTF8, leaveOpen: true);

        var magic = Encoding.ASCII.GetString(br.ReadBytes(4));
        if (!string.Equals(magic, Magic, StringComparison.Ordinal))
        {
            throw new InvalidDataException("不是 VEC2 分块容器。");
        }

        var chunkSize = br.ReadInt32();
        var saltLen = br.ReadInt32();
        var nonceLen = br.ReadInt32();
        var chunkCount = br.ReadInt32();

        var salt = br.ReadBytes(saltLen);
        var baseNonce = br.ReadBytes(nonceLen);

        var store = new ChunkedContainerStore(encryption, containerPath, password, chunkSize)
        {
            _salt = salt,
            _baseNonce = baseNonce
        };

        for (var i = 0; i < chunkCount; i++)
        {
            var chunkIndex = br.ReadInt64();
            var cipherLen = br.ReadInt32();
            var tagLen = br.ReadInt32();
            var cipher = br.ReadBytes(cipherLen);
            var tag = br.ReadBytes(tagLen);
            store._chunks[chunkIndex] = new EncryptedChunk(chunkIndex, cipher, tag);
        }

        return store;
    }

    public static ChunkedContainerStore OpenOrCreate(EncryptionService encryption, string containerPath, string password, int chunkSize = 1 * 1024 * 1024)
    {
        return File.Exists(containerPath)
            ? Open(encryption, containerPath, password)
            : CreateNew(encryption, containerPath, password, chunkSize);
    }

    public byte[] ReadChunk(long chunkIndex)
    {
        if (chunkIndex < 0) throw new ArgumentOutOfRangeException(nameof(chunkIndex));
        if (!_chunks.TryGetValue(chunkIndex, out var encrypted))
        {
            return Array.Empty<byte>();
        }

        return _encryption.DecryptChunk(encrypted, _password, _salt, _baseNonce);
    }

    public void WriteChunk(long chunkIndex, ReadOnlySpan<byte> plainChunk)
    {
        if (chunkIndex < 0) throw new ArgumentOutOfRangeException(nameof(chunkIndex));
        if (plainChunk.Length > ChunkSize)
        {
            throw new ArgumentException($"chunk 数据长度不能大于 ChunkSize({ChunkSize})");
        }

        var encrypted = _encryption.EncryptChunk(plainChunk, _password, _salt, _baseNonce, chunkIndex);
        _chunks[chunkIndex] = encrypted;
    }

    public void DeleteChunk(long chunkIndex)
    {
        _chunks.Remove(chunkIndex);
    }

    public IReadOnlyCollection<long> GetChunkIndexes() => _chunks.Keys.OrderBy(x => x).ToArray();

    public byte[] ReadAllBytes()
    {
        if (_chunks.Count == 0)
        {
            return Array.Empty<byte>();
        }

        using var ms = new MemoryStream();
        foreach (var index in _chunks.Keys.OrderBy(x => x))
        {
            var plain = ReadChunk(index);
            ms.Write(plain, 0, plain.Length);
        }

        return ms.ToArray();
    }

    public void WriteAllBytes(ReadOnlySpan<byte> data)
    {
        _chunks.Clear();
        if (data.Length == 0)
        {
            return;
        }

        var chunkCount = (data.Length + ChunkSize - 1) / ChunkSize;
        for (var i = 0; i < chunkCount; i++)
        {
            var start = i * ChunkSize;
            var len = Math.Min(ChunkSize, data.Length - start);
            WriteChunk(i, data.Slice(start, len));
        }
    }

    public void Flush()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_containerPath)) ?? Directory.GetCurrentDirectory());

        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);

        bw.Write(Encoding.ASCII.GetBytes(Magic));
        bw.Write(ChunkSize);
        bw.Write(_salt.Length);
        bw.Write(_baseNonce.Length);
        bw.Write(_chunks.Count);
        bw.Write(_salt);
        bw.Write(_baseNonce);

        foreach (var pair in _chunks.OrderBy(p => p.Key))
        {
            var chunk = pair.Value;
            bw.Write(chunk.ChunkIndex);
            bw.Write(chunk.CipherText.Length);
            bw.Write(chunk.Tag.Length);
            bw.Write(chunk.CipherText);
            bw.Write(chunk.Tag);
        }

        bw.Flush();
        File.WriteAllBytes(_containerPath, ms.ToArray());
    }
}
