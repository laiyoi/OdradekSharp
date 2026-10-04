using System.Diagnostics;
using OdradekSharp.Ds2;
using OdradekSharp.Rtti;

namespace OdradekSharp.Export;

public sealed record ExportOptions
{
    /// <summary>Directory the JSON files are written to.</summary>
    public required string OutputDirectory { get; init; }

    /// <summary>Also match types derived from the requested one (default true, like odradek's isAssignableFrom).</summary>
    public bool IncludeDerived { get; init; } = true;

    /// <summary>
    /// Read child groups while reading a group. Only needed to resolve <c>Ref</c>/<c>cptr</c> pointers that
    /// point into a child group; StreamingDataSource locators are resolved either way. Default false because
    /// a group's child tree can be enormous (reading group 499 with subgroups takes ~130 s).
    /// </summary>
    public bool ReadSubgroups { get; init; }

    /// <summary>Worker threads. Default: min(4, CPU count).</summary>
    public int Threads { get; init; } = Math.Min(4, Environment.ProcessorCount);

    /// <summary>Cap on decompressed data held by concurrent group reads.</summary>
    public long MaxMemoryBytes { get; init; } = 1536L * 1024 * 1024;

    public int Limit { get; init; } = int.MaxValue;
}

public sealed record ExportReport(
    int Exported,
    int Requested,
    int GroupsRead,
    int GroupsSkipped,
    int SkippedByTruncation,
    int WriteFailures,
    IReadOnlyList<string> Warnings,
    double Seconds,
    long PeakWorkingSetBytes)
{
    public override string ToString() =>
        $"exported {Exported}/{Requested} object(s) from {GroupsRead} group(s) in {Seconds:F1}s " +
        $"(skipped {GroupsSkipped} group(s), {SkippedByTruncation} object(s) past a truncation, " +
        $"{WriteFailures} write failure(s), peak {PeakWorkingSetBytes / 1048576} MiB)";
}

/// <summary>
/// Exports every object of a type the efficient way: group metadata is scanned first (no object bytes are
/// read), then each matching group is read ONCE, keeping only the objects that will be written.
///
/// Do NOT loop <c>ReadObject(id)</c> for this: that reads the object's whole group (and, by default, its
/// entire child-group tree) per object — reading a single object out of group 499 costs ~40 s (and ~130 s
/// with child groups), so a few hundred objects turn into hours.
/// </summary>
public static class TypeExporter
{
    public static ExportReport Export(
        DecimaGame game,
        string typeName,
        ExportOptions options,
        Action<string>? progress = null)
    {
        var sw = Stopwatch.StartNew();
        var target = game.Types.Resolve(typeName);
        if (target is not ClassTypeInfo targetClass)
            throw new ArgumentException($"{typeName} is not a compound type", nameof(typeName));

        Directory.CreateDirectory(options.OutputDirectory);

        // 1) metadata-only pass: which groups hold the type, and at which indices
        var jobs = new List<(int GroupId, HashSet<int> Wanted)>();
        var requested = 0;
        foreach (var group in game.Graph.Groups)
        {
            var matches = new HashSet<int>();
            for (var i = 0; i < group.Types.Count; i++)
            {
                var t = group.Types[i];
                var hit = options.IncludeDerived
                    ? StreamingObjectReader.IsAssignableFrom(targetClass, t)
                    : t.Name == targetClass.Name;
                if (hit) matches.Add(i);
            }
            if (matches.Count == 0) continue;
            jobs.Add((group.Id, matches));
            requested += matches.Count;
            if (requested >= options.Limit) break;
        }
        progress?.Invoke($"{requested} object(s) in {jobs.Count} group(s); threads={options.Threads}");

        // 2) read each group once (in parallel) and write the wanted objects
        // Limit <= 0 means "no limit": a caller passing 0 for "unlimited" must not silently export nothing.
        var limit = options.Limit <= 0 ? int.MaxValue : options.Limit;
        var written = 0;
        var read = 0;
        var skipped = 0;
        var truncated = 0;
        var writeFailures = 0;
        var diagnostics = new List<string>();
        var warnings = new List<string>();
        using var gate = new MemoryGate(Math.Max(options.MaxMemoryBytes, 64L * 1024 * 1024));
        var parallel = new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, options.Threads) };

        Parallel.ForEach(jobs, parallel, job =>
        {
            if (Volatile.Read(ref written) >= limit) return;
            var group = game.Graph.GetGroup(job.GroupId);
            var spanBytes = Math.Min(group.Spans.Sum(s => (long)s.Length), gate.Budget);
            gate.Enter(spanBytes);
            try
            {
                IReadOnlyList<TypedObject> objects;
                try
                {
                    objects = game.ReadGroupFiltered(job.GroupId, job.Wanted, options.ReadSubgroups);
                }
                catch (Exception e)
                {
                    Interlocked.Increment(ref skipped);
                    lock (warnings) warnings.Add($"group {job.GroupId} skipped: {e.Message}");
                    progress?.Invoke($"  group {job.GroupId} skipped: {e.Message}");
                    return;
                }
                Interlocked.Increment(ref read);
                var valid = game.ValidObjectCount(job.GroupId);
                // Diagnostics for the "nothing was written" case: distinguish group truncation from a
                // return-value problem (all wanted entries null) from per-object write failures.
                if (written == 0)
                {
                    var nonNull = 0;
                    foreach (var i in job.Wanted)
                        if (i < objects.Count && objects[i] is not null) nonNull++;
                    lock (diagnostics)
                        diagnostics.Add($"group {job.GroupId}: wanted={job.Wanted.Count}, " +
                                        $"objects={objects.Count}, nonNull={nonNull}, valid={valid}, " +
                                        $"readSubgroups={options.ReadSubgroups}");
                }
                foreach (var i in job.Wanted)
                {
                    if (i >= valid)
                    {
                        // The group desynchronized before this object (an object could not be decoded),
                        // so the object's bytes cannot be trusted. Reported so it is never silent.
                        Interlocked.Increment(ref truncated);
                        continue;
                    }
                    if (Volatile.Read(ref written) >= limit) break;
                    var obj = objects[i];
                    if (obj is null) continue;
                    try
                    {
                        var path = Path.Combine(options.OutputDirectory,
                            $"{obj.Type.Name}_{job.GroupId}_{i}.json");
                        File.WriteAllText(path, JsonExporter.Export(obj));
                        Interlocked.Increment(ref written);
                    }
                    catch (Exception e)
                    {
                        // Never swallow a write failure: it is the difference between "0 exported" and a
                        // usable error message.
                        Interlocked.Increment(ref writeFailures);
                        lock (warnings)
                        {
                            if (warnings.Count < 20)
                                warnings.Add($"{job.GroupId}:{i} write failed: {e.GetType().Name}: {e.Message}");
                        }
                    }
                }
            }
            finally
            {
                gate.Exit(spanBytes);
            }
            game.ReleaseCaches();
        });

        sw.Stop();
        if (written == 0 && requested > 0)
        {
            warnings.Insert(0, $"nothing was written: requested={requested}, groupsRead={read}, " +
                               $"groupsSkipped={skipped}, writeFailures={writeFailures}, " +
                               $"outputDirectory={options.OutputDirectory} (exists={Directory.Exists(options.OutputDirectory)})");
            foreach (var d in diagnostics.Take(10)) warnings.Insert(1, "  " + d);
        }
        var proc = Process.GetCurrentProcess();
        return new ExportReport(written, requested, read, skipped, truncated, writeFailures, warnings,
            sw.Elapsed.TotalSeconds, proc.PeakWorkingSet64);
    }
}
