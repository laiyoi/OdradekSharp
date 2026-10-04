namespace OdradekSharp.Io;

/// <summary>
/// Random-access reader for game data files. Mirrors odradek's
/// StreamingGraphStorage.mount (DirectStorageReader if the file starts with "DSAR", else a plain file).
/// Instances are meant to be mounted once and reused (see FileStore).
/// </summary>
public abstract class DataFile : IDisposable
{
    private long _bytesRead;
    public long BytesRead => Interlocked.Read(ref _bytesRead);
    protected void AddBytes(long n) => Interlocked.Add(ref _bytesRead, n);

    public abstract byte[] Read(long offset, int length);
    public abstract long Size { get; }
    public abstract void Dispose();

    /// <summary>Opens a file, transparently handling the DSAR (DirectStorage + LZ4) container.</summary>
    public static DataFile Open(string path)
    {
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
        Span<byte> magic = stackalloc byte[4];
        stream.ReadExactly(magic);
        stream.Position = 0;
        if (magic[0] == (byte)'D' && magic[1] == (byte)'S' && magic[2] == (byte)'A' && magic[3] == (byte)'R')
            return new DirectStorageFile(path, stream);
        return new PlainFile(stream);
    }
}

internal sealed class PlainFile(FileStream stream) : DataFile
{
    private readonly object _lock = new();
    public override long Size => stream.Length;

    public override byte[] Read(long offset, int length)
    {
        var buffer = new byte[length];
        lock (_lock)
        {
            stream.Position = offset;
            stream.ReadExactly(buffer, 0, length);
        }
        AddBytes(length);
        return buffer;
    }

    public override void Dispose() => stream.Dispose();
}

/// <summary>
/// DirectStorage archive ("DSAR") reader — port of odradek's DirectStorageReader +
/// ChunkedBinaryReader (odradek-core/.../io/DirectStorageReader.java, ChunkedBinaryReader.java).
/// Keeps a small LRU of decompressed chunks (odradek keeps exactly one; a few more makes the repeated
/// sequential span reads of a group noticeably cheaper).
/// </summary>
internal sealed class DirectStorageFile : DataFile
{
    public readonly record struct Chunk(long Offset, long CompressedOffset, int Size, int CompressedSize);

    private const int CacheSlots = 1;

    private readonly FileStream _stream;
    private readonly Chunk[] _chunks;
    private readonly long _totalSize;
    private readonly object _lock = new();

    private readonly byte[] _compressed;
    private readonly Dictionary<long, byte[]> _cache = [];
    private readonly Queue<long> _lru = new();
    private long _decompressCount;

    public DirectStorageFile(string path, FileStream stream)
    {
        _stream = stream;
        var header = new byte[32];
        stream.ReadExactly(header);
        var r = new BinaryReader(header);
        var magic = r.ReadUInt();
        if (magic != 0x52415344) throw new InvalidDataException("Invalid DSAR magic");
        var versionMajor = r.ReadUShort();
        var versionMinor = r.ReadUShort();
        if (versionMajor != 3 && versionMinor != 1)
            throw new InvalidDataException($"Unsupported archive version {versionMajor}.{versionMinor}");
        var chunkCount = r.ReadInt();
        var firstChunkOffset = r.ReadInt();
        _totalSize = r.ReadLong();

        _chunks = new Chunk[chunkCount];
        var table = new byte[chunkCount * 32];
        stream.Position = 32;
        stream.ReadExactly(table);
        AddBytes(table.Length);
        var tr = new BinaryReader(table);
        var maxCompressed = 0;
        for (var i = 0; i < chunkCount; i++)
        {
            var offset = tr.ReadLong();
            var compressedOffset = tr.ReadLong();
            var size = tr.ReadInt();
            var compressedSize = tr.ReadInt();
            var type = tr.ReadByte();
            tr.Skip(7);
            if (type != 3) throw new InvalidDataException($"Unsupported chunk compression type: {type}");
            _chunks[i] = new Chunk(offset, compressedOffset, size, compressedSize);
            maxCompressed = Math.Max(maxCompressed, compressedSize);
        }
        if (_chunks.Length > 0 && firstChunkOffset != 32 + chunkCount * 32)
            Console.Error.WriteLine($"warning: DSAR firstChunkOffset mismatch in {path}");
        _compressed = new byte[maxCompressed];
    }

    public override long Size => _totalSize;

    public long DecompressCount => Interlocked.Read(ref _decompressCount);
    public int CachedChunks => _cache.Count;

    public override byte[] Read(long offset, int length)
    {
        var result = new byte[length];
        var pos = offset;
        var written = 0;
        lock (_lock)
        {
            while (written < length)
            {
                var chunk = FindChunk(pos);
                var chunkOffset = (int)(pos - chunk.Offset);
                var n = Math.Min(chunk.Size - chunkOffset, length - written);
                if (n <= 0) throw new EndOfStreamException();

                var data = GetChunk(chunk);
                Array.Copy(data, chunkOffset, result, written, n);
                pos += n;
                written += n;
            }
        }
        return result;
    }

    private byte[] GetChunk(Chunk chunk)
    {
        if (_cache.TryGetValue(chunk.Offset, out var cached)) return cached;

        if (_cache.Count >= CacheSlots && _lru.Count > 0)
            _cache.Remove(_lru.Dequeue());

        _stream.Position = chunk.CompressedOffset;
        _stream.ReadExactly(_compressed, 0, chunk.CompressedSize);
        AddBytes(chunk.CompressedSize);
        var buffer = new byte[chunk.Size];
        Lz4.DecompressBlock(_compressed.AsSpan(0, chunk.CompressedSize), buffer);
        Interlocked.Increment(ref _decompressCount);
        _cache[chunk.Offset] = buffer;
        _lru.Enqueue(chunk.Offset);
        return buffer;
    }

    private Chunk FindChunk(long position)
    {
        // chunk table is sorted by offset and contiguous; binary search the last chunk with Offset <= position
        var lo = 0;
        var hi = _chunks.Length - 1;
        while (lo < hi)
        {
            var mid = (lo + hi + 1) / 2;
            if (_chunks[mid].Offset <= position) lo = mid; else hi = mid - 1;
        }
        return _chunks[lo];
    }

    public override void Dispose() => _stream.Dispose();
}
