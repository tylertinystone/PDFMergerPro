using System.Text;

namespace VirtualEncryptedDisk;

/// <summary>
/// 阶段4：分块容器存储，提供按偏移随机读写能力。
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
    private long _logicalLength;

    public int ChunkSize { get; }
    public long Length => _logicalLength;

    public ChunkedContainerStore(EncryptionService encryption, string containerPath, string password, int chunkSize = 1 * 1024 * 1024)
    {
        if (chunkSize <= 0) throw new ArgumentOutOfRangeException(nameof(chunkSize));
        _encryption = encryption;
        _containerPath = containerPath;
        _password = password;
        ChunkSize = chunkSize;
        _logicalLength = 0;
    }

    public static ChunkedContainerStore CreateNew(EncryptionService encryption, string containerPath, string password, int chunkSize = 1 * 1024 * 1024)
    {
        var store = new ChunkedContainerStore(encryption, containerPath, password, chunkSize)
        {
            _salt = EncryptionService.GenerateSalt(),
            _baseNonce = EncryptionService.GenerateBaseNonce(),
            _logicalLength = 0
        };
        store.Flush();
        return store;
    }

    public static ChunkedContainerStore Open(EncryptionService encryption, string containerPath, string password)
    {
        var bytes = File.ReadAllBytes(containerPath);

        try
        {
            return OpenWithLogicalLengthHeader(encryption, containerPath, password, bytes);
        }
        catch (Exception ex) when (ex is EndOfStreamException or IOException or InvalidDataException)
        {
            return OpenLegacyHeader(encryption, containerPath, password, bytes);
        }
    }

    private static ChunkedContainerStore OpenWithLogicalLengthHeader(EncryptionService encryption, string containerPath, string password, byte[] bytes)
    {
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
        var logicalLength = br.ReadInt64();

        var salt = br.ReadBytes(saltLen);
        var baseNonce = br.ReadBytes(nonceLen);

        var store = new ChunkedContainerStore(encryption, containerPath, password, chunkSize)
        {
            _salt = salt,
            _baseNonce = baseNonce,
            _logicalLength = logicalLength
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

    private static ChunkedContainerStore OpenLegacyHeader(EncryptionService encryption, string containerPath, string password, byte[] bytes)
    {
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
            _baseNonce = baseNonce,
            _logicalLength = 0
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

        if (store._chunks.Count > 0)
        {
            var maxChunk = store._chunks.Keys.Max();
            var last = store.ReadChunk(maxChunk);
            store._logicalLength = maxChunk * store.ChunkSize + last.Length;
        }

        return store;
    }

    public static ChunkedContainerStore OpenOrCreate(EncryptionService encryption, string containerPath, string password, int chunkSize = 1 * 1024 * 1024)
    {
        return File.Exists(containerPath)
            ? Open(encryption, containerPath, password)
            : CreateNew(encryption, containerPath, password, chunkSize);
    }

    public int ReadAt(long offset, Span<byte> destination)
    {
        if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset));
        if (destination.Length == 0) return 0;
        if (offset >= _logicalLength) return 0;

        var toRead = (int)Math.Min(destination.Length, _logicalLength - offset);
        var totalRead = 0;

        while (totalRead < toRead)
        {
            var absolute = offset + totalRead;
            var chunkIndex = absolute / ChunkSize;
            var inChunkOffset = (int)(absolute % ChunkSize);
            var chunkPlain = ReadChunk(chunkIndex);

            var availableInChunk = Math.Max(0, chunkPlain.Length - inChunkOffset);
            if (availableInChunk == 0)
            {
                var fill = Math.Min(ChunkSize - inChunkOffset, toRead - totalRead);
                destination.Slice(totalRead, fill).Clear();
                totalRead += fill;
                continue;
            }

            var copy = Math.Min(availableInChunk, toRead - totalRead);
            chunkPlain.AsSpan(inChunkOffset, copy).CopyTo(destination.Slice(totalRead, copy));
            totalRead += copy;
        }

        return totalRead;
    }

    public void WriteAt(long offset, ReadOnlySpan<byte> source)
    {
        if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset));
        if (source.Length == 0) return;

        var written = 0;
        while (written < source.Length)
        {
            var absolute = offset + written;
            var chunkIndex = absolute / ChunkSize;
            var inChunkOffset = (int)(absolute % ChunkSize);
            var writeLen = Math.Min(ChunkSize - inChunkOffset, source.Length - written);

            var chunkPlain = EnsureChunkBuffer(chunkIndex);
            source.Slice(written, writeLen).CopyTo(chunkPlain.AsSpan(inChunkOffset, writeLen));

            var effectiveLen = TrimTrailingZerosLength(chunkPlain);
            if (effectiveLen == 0)
            {
                DeleteChunk(chunkIndex);
            }
            else
            {
                WriteChunk(chunkIndex, chunkPlain.AsSpan(0, effectiveLen));
            }

            written += writeLen;
        }

        _logicalLength = Math.Max(_logicalLength, offset + source.Length);
    }

    public void SetLength(long length)
    {
        if (length < 0) throw new ArgumentOutOfRangeException(nameof(length));

        if (length == _logicalLength)
        {
            return;
        }

        if (length < _logicalLength)
        {
            var keepLastChunk = length == 0 ? -1 : (length - 1) / ChunkSize;
            foreach (var idx in _chunks.Keys.Where(x => x > keepLastChunk).ToArray())
            {
                _chunks.Remove(idx);
            }

            if (keepLastChunk >= 0)
            {
                var chunk = ReadChunk(keepLastChunk);
                var keepLen = (int)(length - keepLastChunk * ChunkSize);
                if (keepLen <= 0)
                {
                    _chunks.Remove(keepLastChunk);
                }
                else
                {
                    WriteChunk(keepLastChunk, chunk.AsSpan(0, Math.Min(keepLen, chunk.Length)));
                }
            }
        }

        _logicalLength = length;
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
        if (_logicalLength == 0)
        {
            return Array.Empty<byte>();
        }

        var buffer = new byte[_logicalLength];
        _ = ReadAt(0, buffer);
        return buffer;
    }

    public void WriteAllBytes(ReadOnlySpan<byte> data)
    {
        _chunks.Clear();
        _logicalLength = 0;
        if (data.Length == 0)
        {
            return;
        }

        WriteAt(0, data);
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
        bw.Write(_logicalLength);
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

    private byte[] EnsureChunkBuffer(long chunkIndex)
    {
        var existing = ReadChunk(chunkIndex);
        var buffer = new byte[ChunkSize];
        if (existing.Length > 0)
        {
            existing.AsSpan().CopyTo(buffer);
        }

        return buffer;
    }

    private static int TrimTrailingZerosLength(byte[] buffer)
    {
        for (var i = buffer.Length - 1; i >= 0; i--)
        {
            if (buffer[i] != 0)
            {
                return i + 1;
            }
        }

        return 0;
    }
}
