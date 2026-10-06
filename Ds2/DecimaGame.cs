using BinaryReader = OdradekSharp.Io.BinaryReader;
using OdradekSharp.Io;
using OdradekSharp.Rtti;

namespace OdradekSharp.Ds2;

/// <summary>
/// Entry point of the port: opens a DS2 installation, loads the type factory and the streaming graph,
/// and exposes object search / reading.
/// Mirrors odradek's DS2Game (odradek-game-ds2/.../game/DS2Game.java).
/// </summary>
public sealed class DecimaGame : IDisposable
{
    public string Root { get; }
    public TypeFactory Types { get; }
    public RttiReader Reader { get; }
    public StreamingGraph Graph { get; }
    public StreamingObjectReader Objects { get; }

    private readonly System.Collections.Concurrent.ConcurrentDictionary<int, List<TypedObject>> _groupCache = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<int, int> _validCounts = new();
    private readonly Io.FileStore _files;
    private readonly ThreadLocal<StreamingObjectReader> _threadReaders;

    public string StreamingGraphPath { get; private set; } = "";

    private DecimaGame(string root, TypeFactory types, RttiReader reader, StreamingGraph graph, Io.FileStore files)
    {
        Root = root;
        Types = types;
        Reader = reader;
        Graph = graph;
        _files = files;
        Objects = new StreamingObjectReader(reader, graph, root, files);
        // One reader per thread: group reads are independent and the reader holds per-group cursors.
        _threadReaders = new ThreadLocal<StreamingObjectReader>(
            () => new StreamingObjectReader(new RttiReader(types), graph, root, files));
    }

    /// <summary>Mounted game data files (opened once, like odradek's mountAll).</summary>
    public Io.FileStore Files => _files;

    /// <summary>Recognises a DS2 installation the same way odradek does: DS2.exe in the root.</summary>
    public static bool Supports(string gameDir) => File.Exists(Path.Combine(gameDir, "DS2.exe"));

    public static DecimaGame Open(string gameDir, string? typesJson = null, string? extensionsJson = null,
        bool verbose = false)
    {
        if (!Directory.Exists(gameDir)) throw new DirectoryNotFoundException(gameDir);
        if (!Supports(gameDir))
            Console.Error.WriteLine($"warning: no DS2.exe found in {gameDir} (continuing anyway)");

        typesJson ??= Path.Combine(AppContext.BaseDirectory, "Data", "types.json");
        extensionsJson ??= Path.Combine(Path.GetDirectoryName(typesJson)!, "extensions.json");

        var types = TypeFactory.Load(typesJson, extensionsJson);
        var reader = new RttiReader(types);
        var graphPath = Path.Combine(gameDir, "LocalCacheWinGame", "package", "streaming_graph.core");
        if (!File.Exists(graphPath)) throw new FileNotFoundException("streaming graph not found", graphPath);

        var files = new Io.FileStore(gameDir) { Verbose = verbose };
        var graph = StreamingGraph.Build(graphPath, types, reader, gameDir, files);
        graph.GameRoot = gameDir;
        // Mount every data file up front, like StreamingGraphStorage.mountAll: reopening a DSAR file
        // re-reads its chunk table, which used to dominate export time.
        files.MountAll(graph.Files);
        var game = new DecimaGame(gameDir, types, reader, graph, files) { StreamingGraphPath = graphPath };
        return game;
    }

    /// <summary>
    /// Enumerates all objects whose type matches <paramref name="typeName"/> (including derived types).
    /// This is metadata-only: no object payload is read.
    /// </summary>
    public IEnumerable<(ObjectId Id, ClassTypeInfo Type)> FindObjects(string typeName, bool includeDerived = true)
    {
        var target = Types.Resolve(typeName);
        if (target is not ClassTypeInfo targetClass)
            throw new ArgumentException($"{typeName} is not a compound type", nameof(typeName));

        foreach (var group in Graph.Groups)
        {
            for (var i = 0; i < group.Types.Count; i++)
            {
                var type = group.Types[i];
                var matches = includeDerived
                    ? StreamingObjectReader.IsAssignableFrom(targetClass, type)
                    : type.Name == targetClass.Name;
                if (matches) yield return (new ObjectId(group.Id, i), type);
            }
        }
    }

    /// <summary>Type name of an object without reading its payload (ObjectIdHolder.objectType).</summary>
    public ClassTypeInfo ObjectType(ObjectId id) => Graph.GetGroup(id.GroupId).Types[id.ObjectIndex];

    /// <summary>Reads the whole group and returns the object at the given index (odradek's readObject).</summary>
    public TypedObject ReadObject(ObjectId id, bool readSubgroups = true)
    {
        var objects = ReadGroup(id.GroupId, readSubgroups);
        if (id.ObjectIndex < 0 || id.ObjectIndex >= objects.Count)
            throw new ArgumentOutOfRangeException(nameof(id),
                $"object index {id.ObjectIndex} out of range for group {id.GroupId} ({objects.Count} objects)");
        return objects[id.ObjectIndex];
    }

    // A cached group holds its whole object graph; the cache must stay tiny (see cache overflow).
    private const int GroupCacheCapacity = 2;

    public IReadOnlyList<TypedObject> ReadGroup(int groupId, bool readSubgroups = true)
    {
        if (_groupCache.TryGetValue(groupId, out var cached)) return cached;
        var reader = _threadReaders.Value!;
        var result = reader.ReadGroup(groupId, readSubgroups);
        _validCounts[groupId] = result.ValidCount;
        while (_groupCache.Count >= GroupCacheCapacity)
        {
            // evict an arbitrary entry (parallel workers each add one)
            foreach (var key in _groupCache.Keys)
            {
                if (key != groupId && _groupCache.TryRemove(key, out _)) break;
            }
        }
        _groupCache[groupId] = result.Objects;
        return result.Objects;
    }

    /// <summary>
    /// Reads a group keeping only the objects at the given indices (the rest are parsed and dropped).
    /// Used by the exporter so a huge group does not have to stay in memory.
    /// <paramref name="collectPayload"/> = false also drops the bulk payloads (big primitive arrays) of
    /// the wanted objects — the layout is still parsed exactly, only the materialized arrays are dropped.
    /// </summary>
    public IReadOnlyList<TypedObject> ReadGroupFiltered(int groupId, HashSet<int> wanted, bool readSubgroups,
        bool collectPayload = true)
    {
        var reader = _threadReaders.Value!;
        var result = reader.ReadGroupFiltered(groupId, wanted, readSubgroups, collectPayload);
        _validCounts[groupId] = result.ValidCount;
        return result.Objects;
    }

    /// <summary>Releases all cached object graphs for the current thread.</summary>
    public void ReleaseCaches()
    {
        _groupCache.Clear();
        _validCounts.Clear();
        _threadReaders.Value!.ClearCache();
    }

    /// <summary>
    /// Number of leading objects of a group that decoded successfully. Everything from this index on is
    /// unreliable (an object needed an unimplemented callback). Must be called after ReadGroup.
    /// </summary>
    public int ValidObjectCount(int groupId) => _validCounts.TryGetValue(groupId, out var n) ? n : int.MaxValue;

    /// <summary>Per-thread reader (for parallel exports).</summary>
    public StreamingObjectReader ReaderForCurrentThread => _threadReaders.Value!;

    /// <summary>Reads raw bytes of a StreamingDataSource (locator -> file/offset/length).</summary>
    public byte[] ReadDataSource(TypedObject dataSource)
    {
        var locator = (long)dataSource.Fields["Locator"]!;
        var fileId = (int)(locator & 0xffffff);
        var fileOffset = (long)((ulong)locator >> 24);
        var offset = Convert.ToInt64(dataSource.Fields["Offset"]!);
        var length = Convert.ToInt32(dataSource.Fields["Length"]!);
        return _files.Get(Graph.Files[fileId]).Read(fileOffset + offset, length);
    }

    public void Dispose()
    {
        _threadReaders.Dispose();
        Types.Dispose();
        _files.Dispose();
    }
}
