# OdradekSharp

A C# (.NET 10) reimplementation of the parts of [ShadelessFox/odradek](https://github.com/ShadelessFox/odradek)
that read **Death Stranding 2: On the Beach** (Decima engine, DS2 flavour) game data directly:
the streaming graph, the RTTI type system and the object/pointer/link model.

It is a *reader*, not a re-implementation of the GUI. Its output is meant to be
**byte-identical** to odradek's own JSON export, and that is what it is tested against.

## Status

| Type | Verified against odradek's export |
|---|---|
| `WwiseWemResource` | 400/400, 7,816/7,816 byte-identical |
| `WwiseID` | 401/401 byte-identical |
| `WwiseBankResource` | 74/74 byte-identical |
| `GraphSoundResource` | 394/400 |
| `GraphProgramResource` | 395/401 |
| `NodeConstantsResource` | 390/401 |

The remaining diffs are all objects that contain pointers, and all of them are caused by one known
defect, described under *Known limitations* below.

## Requirements

* .NET 10 SDK
* A Death Stranding 2 installation (the tool reads `LocalCacheWinGame\package\streaming_graph.core`
  plus the `package.*.core.stream` files and `streaming_links.stream`)
* `Data/types.json` and `Data/extensions.json` — the RTTI type definitions. They are shipped here
  because odradek ships them too: odradek extracts them from the game and keeps them in its own
  repository (`odradek-game-ds2/src/main/resources`).

## Build

```
dotnet build -c Release
# -> bin/Release/net10.0/odradeksharp.exe
```

## Usage

```
odradeksharp info    <gameDir>                              graph statistics
odradeksharp groups  <gameDir> [--limit N]                  list groups
odradeksharp types   <gameDir> [--limit N]                  list types with object counts
odradeksharp find    <gameDir> <Type> [--exact] [--limit N] locate objects of a type
odradeksharp read    <gameDir> <group>:<index> [--json] [--out F] [--no-subgroups]
odradeksharp group   <gameDir> <groupId>                    group metadata (spans, sub groups, types)
odradeksharp trace   <gameDir> <group>:<index>              per-object stream offsets
odradeksharp hex     <gameDir> <file> <offset> <length>     raw bytes
odradeksharp rtti    <gameDir>                              type-factory statistics
odradeksharp dump    <gameDir> <Type> --out <dir> [--exact] [--no-subgroups] [--threads N] [--limit N]
```

Example — export every `WwiseWemResource` as JSON:

```
odradeksharp dump "K:\...\DEATH STRANDING 2 - ON THE BEACH" WwiseWemResource \
    --out out --exact --no-subgroups
```

## Performance

Measured on one machine, game data on a local SSD, 4 threads:

| Task | Time | Peak memory |
|---|---|---|
| All 7,838 `WwiseWemResource` | ~30 s | ~4.9 GB |
| 400 `GraphSoundResource` (38 groups) | 0.6 s | ~1.0 GB |
| 400 `WwiseID` (48 groups) | 0.3 s | ~0.9 GB |
| `read group 499 object 1173` (group 499 = 124,219 objects, one 286 MB span) | 1.2 s | ~1.4 GB |

Notes on where the time actually goes:

* Objects have no offsets in the stream, so reaching object *k* requires decoding objects `0..k`.
  Like odradek, the reader currently decodes the whole group.
* Cost is therefore driven by **which groups** you touch, not by how many objects you want: the same
  work is ~0.3 s for 40 small groups and ~8 s for one 124k-object group.
* The library keeps a bounded group cache. Reusing it is what makes walking references cheap;
  throwing group results away after every group (as a bulk export may) makes large groups expensive.

## Notes on the format

See the source for the details; the important ones:

* `ObjectId` = `(groupId, indexWithinGroup)`; the group is resolved through the group table.
* Pointers are one presence byte plus one entry from the group's link table
  (big-endian 7-bit varint groups, `0x40` marks the presence of a group field).
* Strings are length + CRC32-prefixed; enums are read at their declared size.
* Callbacks (`ExtraBinaryDataHolder` / `MsgReadBinary`) are **inherited** from base types, and must
  write into the type's extension fields (`extensions.json`), because that is what odradek's JSON
  exporter walks.
* Java's `Float.toString`/`Double.toString` layout is reproduced exactly (e.g. `7.796708E-4`).

## Known limitations

1. **`PhysicsRagdollResource` and `FacialRigSettingWithLODResource` callbacks are not ported**
   (they need Jolt / RigLogic). A group that contains one of them is read up to that object and then
   truncated; every later object in that group is unavailable. This is reported explicitly as
   `N object(s) past a truncation`. `PhysicsShapeResource` *is* ported.
2. **Link cursor defect.** In group `31127`, starting at object `3386`, the reader consumes one extra
   link entry, which shifts every later pointer in that group by one. Symptom: the `[1]Skeleton`
   attribute of `SkeletonAnimationResource` is read twice. Reproduce with:

   ```
   ODRADEKSHARP_TRACE_LINKS=1 odradeksharp read <gameDir> 31127:3386 --json --no-subgroups
   ```

   The ground truth (odradek's own link database, `%LOCALAPPDATA%\Odradek\links-*.db`) says object
   `31127:3386` has exactly two pointers: `[attr1] -> 77626:2164` and `[attr7] -> 31127:1159`.
3. `cptr` pointers are rendered as `<cptr to G:I>`; odradek has no `toString()` override for `CPtr`,
   so it prints a Java identity string that cannot be reproduced.

## License

**GPL-3.0**, because this is a derivative work of odradek, which is licensed under GPL-3.0.
See `LICENSE`.

The type definitions in `Data/` originate from the game and are redistributed the same way odradek
redistributes them.
