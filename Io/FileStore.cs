namespace OdradekSharp.Io;

/// <summary>
/// Mounts game data files once and reuses them, like odradek's StreamingGraphStorage.mountAll
/// (odradek-game-ds2/.../storage/StreamingGraphStorage.java:30-80). Reopening a DSAR file means
/// re-reading and re-parsing its whole chunk table and dropping the decompressed-chunk cache, which
/// dominated the export time before this cache existed.
/// </summary>
public sealed class FileStore(string gameRoot) : IDisposable
{
    private readonly Dictionary<string, DataFile> _files = new(StringComparer.Ordinal);
    private readonly object _lock = new();

    public string GameRoot { get; } = gameRoot;
    public int MountedCount { get; private set; }
    public long BytesRead => _files.Values.Sum(f => f.BytesRead);

    /// <summary>
    /// Print one line per file that is not present on disk. Off by default: a DS2 install without every
    /// language/region package produces well over a hundred of these, and they drown out real errors
    /// while being harmless for reading (the exporter never needed those files).
    /// </summary>
    public bool Verbose { get; set; }

    public DataFile Get(string devicePath)
    {
        lock (_lock)
        {
            if (_files.TryGetValue(devicePath, out var file)) return file;
            var path = Ds2.StreamingGraph.ResolveGamePath(GameRoot, devicePath);
            if (!File.Exists(path))
                throw new FileNotFoundException($"game data file is missing: {devicePath}", path);
            file = DataFile.Open(path);
            _files[devicePath] = file;
            MountedCount++;
            return file;
        }
    }

    /// <summary>
    /// Pre-mounts every file of a streaming graph (mirrors StreamingGraphStorage.mountAll, which logs
    /// and continues when a listed file is not present on disk).
    /// </summary>
    public void MountAll(IEnumerable<string> devicePaths)
    {
        foreach (var path in devicePaths)
        {
            try
            {
                Get(path);
            }
            catch (Exception e)
            {
                MissingFiles.Add(path);
                if (Verbose) Console.Error.WriteLine($"warning: file not mounted: {path} ({e.Message})");
            }
        }
        // Summarized instead of hidden: the count is still reported, the names need --verbose.
        if (!Verbose && MissingFiles.Count > 0)
            Console.Error.WriteLine($"warning: {MissingFiles.Count} file(s) not mounted " +
                                    $"(uninstalled language/region packages; harmless for reading, " +
                                    $"pass --verbose to list them)");
    }

    public List<string> MissingFiles { get; } = [];

    public void Dispose()
    {
        lock (_lock)
        {
            foreach (var f in _files.Values) f.Dispose();
            _files.Clear();
        }
    }
}
