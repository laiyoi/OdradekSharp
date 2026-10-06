using BinaryReader = OdradekSharp.Io.BinaryReader;
using OdradekSharp.Io;
using OdradekSharp.Rtti;

namespace OdradekSharp.Ds2;

/// <summary>
/// Streaming-side reader: reads whole groups of objects, resolving pointers through the streaming
/// link table and StreamingDataSource locators.
/// Port of odradek's StreamingObjectReader (odradek-game-ds2/.../storage/StreamingObjectReader.java).
/// </summary>
public sealed class StreamingObjectReader(RttiReader reader, StreamingGraph graph, string gameRoot,
    Io.FileStore fileStore)
{
    /// <summary>
    /// Result of reading a group. Objects before <see cref="ValidCount"/> were decoded successfully; the
    /// rest (if any) could not be decoded because a preceding object needed an unimplemented callback,
    /// which desynchronizes the span.
    /// </summary>
    public readonly record struct GroupResult(StreamingGraph.Group Group, List<TypedObject> Objects, int ValidCount);

    private readonly RttiReader _reader = reader;
    private readonly StreamingGraph _graph = graph;
    private readonly string _gameRoot = gameRoot;
    private readonly Io.FileStore _files = fileStore;
    // Bounded cache: a cached group keeps its whole object graph alive, so an unbounded cache
    // (my first version) exhausts memory on large exports. odradek caps it too (LruWeakCache(5000)).
    private const int CacheCapacity = 2;
    private readonly Dictionary<int, GroupResult> _cache = [];
    private readonly Queue<int> _cacheOrder = new();

    private List<GroupResult> _currentSubGroups = [];
    private GroupResult _currentGroup;
    private LinkCursor? _links;
    private int _locatorIndex;
    private bool _resolveLinks;
    private bool _resolveLocators;

    public IReadOnlyList<TypedObject> Objects => _currentGroup.Objects;

    /// <summary>Optional per-object trace: object index, type and byte range inside its span.</summary>
    public bool TraceEnabled { get; set; }
    public List<(int Index, string Type, int Start, int End, string File, int SpanLength)> Trace { get; } = [];

    public GroupResult ReadGroup(int id, bool readSubgroups = true, HashSet<int>? wanted = null)
        => ReadGroup(id, [], readSubgroups, wanted);

    /// <summary>
    /// Reads a group and drops every object that is not in <paramref name="wanted"/> as soon as the group
    /// is fully parsed (the objects are still parsed — that is the only way to find object boundaries —
    /// but their object graphs become garbage instead of being retained). Filtered results are never
    /// cached, because they are only valid for the requested index set.
    ///
    /// <paramref name="collectPayload"/> = false additionally drops the *bulk payloads* (big primitive
    /// arrays, byte buffers) of the wanted objects themselves. The byte layout is still parsed exactly —
    /// only the materialized arrays are dropped — which is what a caller needs when it only looks at a few
    /// scalar/pointer fields. Leave it on when a container field must be inspected.
    /// </summary>
    public GroupResult ReadGroupFiltered(int id, HashSet<int> wanted, bool readSubgroups, bool collectPayload = true)
        => ReadGroup(id, [], readSubgroups, wanted, collectPayload);

    private GroupResult ReadGroup(int id, Dictionary<int, GroupResult> cache, bool readSubgroups,
        HashSet<int>? wanted = null, bool collectPayload = true)
    {
        var group = _graph.GetGroup(id); // throws KeyNotFoundException like odradek's "Group not found"
        if (wanted is null && _cache.TryGetValue(id, out var cached)) return cached;
        var result = ReadGroupInternal(group, readSubgroups, wanted, collectPayload);
        if (wanted is null) Store(group.Id, result);
        return result;
    }

    private GroupResult ReadGroupInternal(StreamingGraph.Group group, bool readSubgroups,
        HashSet<int>? wanted = null, bool collectPayload = true)
    {
        var subGroups = new List<GroupResult>(group.SubGroups.Count);
        if (readSubgroups)
        {
            foreach (var sub in group.SubGroups)
            {
                try
                {
                    subGroups.Add(ReadGroup(sub.Id, [], true));
                }
                catch (Exception e)
                {
                    // Deviates from odradek, which would propagate the exception: a child group that cannot
                    // be read at all is skipped. Links pointing into it resolve to null.
                    Warnings.Add($"subgroup {sub.Id} skipped: {e.Message}");
                }
            }
        }

        _currentSubGroups = subGroups;
        _resolveLinks = readSubgroups;
        _resolveLocators = true; // locators are group-local: resolve even when subgroups are skipped

        if (wanted is null && _cache.TryGetValue(group.Id, out var cached)) return cached;
        var result = ReadSingleGroup(group, wanted, collectPayload);
        if (wanted is null) Store(group.Id, result);
        return result;
    }

    private void Store(int groupId, GroupResult result)
    {
        if (_cache.ContainsKey(groupId)) return;
        while (_cache.Count >= CacheCapacity && _cacheOrder.Count > 0)
            _cache.Remove(_cacheOrder.Dequeue());
        _cache[groupId] = result;
        _cacheOrder.Enqueue(groupId);
    }

    /// <summary>Drops cached group graphs (the caller no longer needs them).</summary>
    public void ClearCache()
    {
        _cache.Clear();
        _cacheOrder.Clear();
    }

    public List<string> Warnings { get; } = [];

    /// <summary>
    /// Size of the span window kept in memory. A span can be hundreds of megabytes; materializing it in
    /// one piece is what made large exports blow up. Objects are parsed from a sliding window that grows
    /// on demand (an object that crosses the window boundary is re-parsed after the window grows).
    /// </summary>
    public int SpanWindowBytes { get; set; } = 8 * 1024 * 1024;

    /// <summary>Port of StreamingObjectReader.readSingleGroup (:100-135).</summary>
    private GroupResult ReadSingleGroup(StreamingGraph.Group group, HashSet<int>? wanted = null,
        bool collectPayload = true)
    {
        var objects = new List<TypedObject>(group.Types.Count);
        foreach (var type in group.Types) objects.Add(new TypedObject { Type = type });

        var result = new GroupResult(group, objects, objects.Count);
        _currentGroup = result;
        _links = _graph.Links(group.LinkStart);
        _locatorIndex = 0;

        var index = 0;
        foreach (var span in group.Spans)
        {
            var file = _graph.Files[span.FileIndex];
            var data = _files.Get(file);
            _reader.Context = new Context(this, span.Offset, file);

            // Sliding window over the span.
            var window = Math.Min(SpanWindowBytes, Math.Max(span.Length, 1));
            var buffer = data.Read(span.Offset, Math.Min(window, span.Length));
            var loaded = buffer.Length;
            var spanReader = new BinaryReader(buffer);
            while (spanReader.Remaining > 0)
            {
                if (index >= objects.Count)
                    throw new InvalidDataException(
                        $"Span has {spanReader.Remaining} leftover bytes but group {group.Id} declares {objects.Count} objects");
                var obj = objects[index];
                obj.Id = new ObjectId(group.Id, index);
                if (LinkCursor.Trace) LinkCursor.TraceObject = index;
                var start = spanReader.Position;
                // Cursor marks must be taken once per object (not per attempt): an attempt that ran past
                // the window has already consumed links/locators, and the retry has to start from here.
                var linkMark = _links!.Position;
                var locatorMark = _locatorIndex;
                _reader.CollectPayload = wanted is null || (collectPayload && wanted.Contains(index));
                while (true)
                {
                    try
                    {
                        _reader.FillCompound(obj.Type, spanReader, obj);
                        break;
                    }
                    catch (EndOfStreamException) when (loaded < span.Length)
                    {
                        // The object runs past the window: grow it and re-parse from the object start.
                        // Both cursors MUST be rewound first. They are group-wide and sequential, so a
                        // retry that keeps the aborted attempt's progress silently consumes one link per
                        // retry for this object and shifts every later pointer in the group by that much
                        // (this is what corrupted group 31127 from object 3386 on: a 323 KB object
                        // straddling the 8 MiB window boundary consumed its Skeleton link twice).
                        var grown = Math.Min(span.Length, loaded + Math.Max(SpanWindowBytes, loaded));
                        buffer = data.Read(span.Offset, grown);
                        loaded = buffer.Length;
                        spanReader = new BinaryReader(buffer) { Position = start };
                        _links.Seek(linkMark);
                        _locatorIndex = locatorMark;
                    }
                    catch (Exception e)
                    {
                        // The object's size is unknown, so every later object in the span is desynchronized.
                        // Keep what was read successfully and report the group as truncated.
                        if (TraceEnabled) Trace.Add((index, obj.Type.Name, start, spanReader.Position, file, span.Length));
                        var message = $"group {group.Id} truncated at object [{index}] {obj.Type.Name}: {e.Message}";
                        Warnings.Add(message);
                        Console.Error.WriteLine("  " + message);
                        _reader.CollectPayload = true;
                        _currentGroup = result with { ValidCount = index };
                        _reader.Context = null;
                        return _currentGroup;
                    }
                }
                if (TraceEnabled)
                    Trace.Add((index, obj.Type.Name, start, spanReader.Position, file, span.Length));

                // Objects we were not asked for are reduced to a type-only stub as soon as they are parsed,
                // so a monster group (group 499 has 124k objects) does not have to stay in memory.
                // A stub — not null — because other objects in the same group may point at it, and the
                // reference (odradek's `<ref to G:I>`) must still resolve; only its payload is dropped.
                if (wanted is not null && !_resolveLinks && !wanted.Contains(index))
                    objects[index] = Stub(obj);
                index++;
            }
        }
        _reader.CollectPayload = true;
        _reader.Context = null;
        if (wanted is not null)
        {
            for (var i = 0; i < objects.Count; i++)
                if (!wanted.Contains(i)) objects[i] = Stub(objects[i]);
        }
        return result;
    }

    /// <summary>Replaces a parsed object with a type-only placeholder (keeps pointer type checks working).</summary>
    private static TypedObject Stub(TypedObject source) => new() { Type = source.Type, Id = source.Id };

    /// <summary>Port of StreamingObjectReader.resolveLink (:178-239) and resolveStreamingDataSource (:157-176).</summary>
    private sealed class Context(StreamingObjectReader owner, int spanOffset, string file) : IStreamingContext
    {
        public bool ResolveLinks => owner._resolveLinks;
        public bool ResolveLocators => owner._resolveLocators;

        public GraphLink NextLink() => (owner._links ?? throw new InvalidOperationException("no link cursor")).Next();

        public void OnStreamingDataSource(TypedObject dataSource)
        {
            if (!owner._resolveLocators) return;
            var channel = dataSource.Fields.TryGetValue("Channel", out var c) ? Convert.ToInt64(c!) : -1;
            var length = dataSource.Fields.TryGetValue("Length", out var l) ? Convert.ToInt64(l!) : 0;
            if (channel == -1 || length <= 0) return; // isValid()
            var locator = owner._currentGroup.Group.Locators[owner._locatorIndex++];
            dataSource.Fields["Locator"] = (long)((locator.Offset << 24) | (locator.FileIndex & 0xffffff));
        }

        public object? ResolveLink(PointerTypeInfo info, GraphLink link)
        {
            if (info.PointerKind == "StreamingRef")
            {
                // StreamingRef stores the *global group id* (StreamingObjectReader.java:188-196)
                return link.Group is { } gid ? new ObjectRef("StreamingRef", new ObjectId(gid, link.Index), null) : null;
            }

            // For Ref/cptr/WeakPtr the link's group field is an index into the parent's subGroups list
            // (StreamingObjectReader.java:199-205); absent means "current group".
            StreamingObjectReader.GroupResult? loaded;
            int targetGroupId;
            if (link.Group is { } subIndex)
            {
                var subGroups = owner._currentGroup.Group.SubGroups;
                if (subIndex < 0 || subIndex >= subGroups.Count) return null;
                if (owner._resolveLinks && subIndex < owner._currentSubGroups.Count)
                {
                    loaded = owner._currentSubGroups[subIndex];
                    targetGroupId = loaded.Value.Group.Id;
                }
                else
                {
                    // Child groups were not read: the group *id* is still available from the metadata,
                    // which is all the reference string needs (odradek's `<ref to G:I>`). The target
                    // object is simply not materialized.
                    loaded = null;
                    targetGroupId = subGroups[subIndex].Id;
                }
            }
            else
            {
                loaded = owner._currentGroup;
                targetGroupId = owner._currentGroup.Group.Id;
            }

            TypedObject? obj = null;
            if (loaded is { } g)
            {
                if (link.Index < 0 || link.Index >= g.Objects.Count) return null;
                obj = g.Objects[link.Index];
                if (obj is null) return null;
                if (!IsAssignableFrom(info.TargetType, obj.Type)) return null; // odradek logs and returns null
            }
            return new ObjectRef(info.PointerKind, new ObjectId(targetGroupId, link.Index), obj);
        }
    }

    /// <summary>Port of ClassTypeInfo.isAssignableFrom (:77-87): name-based, recursive over bases.</summary>
    public static bool IsAssignableFrom(TypeInfo expected, ClassTypeInfo actual)
    {
        if (expected is not ClassTypeInfo cls) return false;
        if (cls.Name == actual.Name) return true;
        foreach (var b in actual.Bases)
            if (IsAssignableFrom(cls, b.Type)) return true;
        return false;
    }
}
