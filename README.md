# OdradekSharp

A C# (.NET 10) reimplementation of the parts of [ShadelessFox/odradek](https://github.com/ShadelessFox/odradek)
that read **Death Stranding 2: On the Beach** (Decima engine, DS2 flavour) game data directly:
the streaming graph, the RTTI type system and the object/pointer/link model.

It is a *reader*, not a re-implementation of the GUI. Its output is meant to be
**byte-identical** to odradek's own JSON export, and that is what it is tested against.

The Java reference implementation is included as a git submodule at
[`external/odradek`](https://github.com/ShadelessFox/odradek) (pinned to a commit), so the code being
ported is right next to the port. It is empty unless you clone with `--recurse-submodules`.

[中文说明](#中文说明) · [状态](#status) · [构建](#build) · [用法](#usage) · [性能](#performance) · [已知限制](#known-limitations) · [许可](#license)

## Status

| Type | Verified against odradek's export |
|---|---|
| `WwiseWemResource` | **7,838/7,838 byte-identical** |
| `WwiseID` | 400/400 byte-identical |
| `WwiseBankResource` | 74/74 byte-identical |
| `GraphSoundResource` | 400/400 byte-identical |
| `GraphProgramResource` | 401/401 byte-identical |
| `NodeConstantsResource` | 400/400 byte-identical |

The previously reported diffs on the three pointer-bearing types (394/400, 395/401, 390/401) were
caused by one reader defect and are gone; see *Known limitations*.

## Requirements

* .NET 10 SDK
* A Death Stranding 2 installation (the tool reads `LocalCacheWinGame\package\streaming_graph.core`
  plus the `package.*.core.stream` files and `streaming_links.stream`)
* `Data/types.json` and `Data/extensions.json` — the RTTI type definitions. They are shipped here
  because odradek ships them too: odradek extracts them from the game and keeps them in its own
  repository (`odradek-game-ds2/src/main/resources`).

## Build

```
git clone --recurse-submodules https://github.com/laiyoi/OdradekSharp.git
# already cloned:
git submodule update --init --recursive

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
| All 7,838 `WwiseWemResource` | ~45 s | ~3.9 GB |
| 400 `GraphSoundResource` (30 groups) | 0.7 s | ~0.9 GB |
| 400 `WwiseID` (33 groups) | 0.4 s | ~0.9 GB |
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

1. **All DS2 `MsgReadBinary` callbacks used by this game are ported**, including
   `PhysicsShapeResource` and `PhysicsRagdollResource` (Jolt) and
   `FacialRigSettingWithLODResource` (RigLogic). Nothing truncates on the real data: exporting every
   `WwiseWemResource` reports `0 object(s) past a truncation`, and the sound-chain walk over all
   5,700 `GraphSoundResource` reports no truncated group either. A group that *did* hit an
   unimplemented callback would still be read up to that object and reported as
   `N object(s) past a truncation` / listed in `soundmap_report.txt`.
2. **Fixed: the link cursor used to desynchronize on re-parsed objects.** A span is read through a
   sliding window; when an object ran past the window it was re-parsed from its start, but the
   group-wide link/locator cursors were not rewound, so the aborted attempt's links stayed consumed.
   One retry = one extra link = every later pointer in that group shifted by one.
   The trigger was reproducible: group `31127`, object `3386` is a 323 KB `SkeletonAnimationResource`
   spanning bytes `8,151,140..8,474,207`, i.e. it straddles the 8 MiB window boundary.
   `StreamingObjectReader` now snapshots the cursors per object and rewinds them on the retry.
   Verified by exporting `31127:3386` and diffing against `odradek export` itself: 15/15 fields equal.
3. `cptr` pointers are rendered as `<cptr to G:I>`; odradek has no `toString()` override for `CPtr`,
   so it prints a Java identity string that cannot be reproduced.

## License

**GPL-3.0**, because this is a derivative work of odradek, which is licensed under GPL-3.0.
See `LICENSE`.

The type definitions in `Data/` originate from the game and are redistributed the same way odradek
redistributes them.

---

# 中文说明

## 这是什么

把 [ShadelessFox/odradek](https://github.com/ShadelessFox/odradek)（Java）里**读取部分**移植到 C# / .NET 10：
直接解析 **《死亡搁浅 2：冥滩之上》**（Decima 引擎 DS2 分支）的游戏数据 —— streaming graph、
RTTI 类型系统、对象/指针/link 模型。

它是一个**读取器**，不是 GUI 的复刻。衡量标准只有一条：导出的 JSON 与 odradek 自己的导出**逐字节相同**。

Java 参考实现在 [`external/odradek`](https://github.com/ShadelessFox/odradek) 以 **git 子模块**方式引入
（固定在某个 commit），被移植的源码就在移植代码旁边。**不写 `--recurse-submodules` 克隆的话，这个目录是空的。**

## 状态

| 类型 | 与 odradek 导出比对 |
|---|---|
| `WwiseWemResource` | **7,838/7,838 逐字节相同** |
| `WwiseID` | 400/400 逐字节相同 |
| `WwiseBankResource` | 74/74 逐字节相同 |
| `GraphSoundResource` | 400/400 逐字节相同 |
| `GraphProgramResource` | 401/401 逐字节相同 |
| `NodeConstantsResource` | 400/400 逐字节相同 |

之前报的三个含指针类型的差异（394/400、395/401、390/401）出自同一个读取器缺陷，现已修复，见「已知限制」。

## 环境要求

* .NET 10 SDK
* 一份《死亡搁浅 2》游戏本体（读取 `LocalCacheWinGame\package\streaming_graph.core`、
  各 `package.*.core.stream` 与 `streaming_links.stream`）
* `Data/types.json` 与 `Data/extensions.json` —— RTTI 类型定义。放在仓库里，是因为 odradek 也是这么做的：
  它从游戏里提取这两个文件并提交在自己的仓库（`odradek-game-ds2/src/main/resources`）。

## 构建

```
git clone --recurse-submodules https://github.com/laiyoi/OdradekSharp.git
# 已经克隆过的话：
git submodule update --init --recursive

dotnet build -c Release
# -> bin/Release/net10.0/odradeksharp.exe
```

## 用法

```
odradeksharp info    <游戏目录>                              图统计信息
odradeksharp groups  <游戏目录> [--limit N]                  列出所有组
odradeksharp types   <游戏目录> [--limit N]                  列出类型及对象数量
odradeksharp find    <游戏目录> <类型> [--exact] [--limit N]  定位某类型的对象
odradeksharp read    <游戏目录> <组>:<下标> [--json] [--out 文件] [--no-subgroups]
odradeksharp group   <游戏目录> <组号>                       组的元数据（span / 子组 / 类型表）
odradeksharp trace   <游戏目录> <组>:<下标>                   逐对象的流内偏移
odradeksharp hex     <游戏目录> <文件> <偏移> <长度>           原始字节
odradeksharp rtti    <游戏目录>                              类型工厂统计
odradeksharp dump    <游戏目录> <类型> --out <目录> [--exact] [--no-subgroups] [--threads N] [--limit N]
```

例：导出全部 `WwiseWemResource`：

```
odradeksharp dump "K:\...\DEATH STRANDING 2 - ON THE BEACH" WwiseWemResource ^
    --out out --exact --no-subgroups
```

## 性能

单机实测（游戏数据在本地 SSD，4 线程）：

| 任务 | 耗时 | 峰值内存 |
|---|---|---|
| 全部 7,838 个 `WwiseWemResource` | ~45 s | ~3.9 GB |
| 400 个 `GraphSoundResource`（30 个组） | 0.7 s | ~0.9 GB |
| 400 个 `WwiseID`（33 个组） | 0.4 s | ~0.9 GB |
| `read 组 499 对象 1173`（组 499 = 124,219 个对象，单个 286 MB span） | 1.2 s | ~1.4 GB |

时间到底花在哪：

* 对象在流里**没有偏移表**，要取第 *k* 个对象就必须顺序解析 `0..k`。和 odradek 一样，目前是整组解析。
* 所以成本取决于**你碰了哪些组**，而不是你要几个对象：同样规模的工作，40 个小组约 0.3 s，
  一个 12 万对象的组约 8 s。
* 库里有**有界的组缓存**。走引用链时复用它才是省时间的关键；如果每处理完一组就把结果丢掉
  （批量导出可能会这样），大组就会被反复重解析。

## 格式要点

* `ObjectId` = `(组号, 组内下标)`，组号通过组表解析。
* 指针 = 1 个 presence 字节 + 组 link 表里的一条记录
  （大端 7 位一组的 varint，首字节 `0x40` 表示带组字段）。
* 字符串是「长度 + CRC32 校验」前缀；枚举按声明的字节宽度读取。
* 回调（`ExtraBinaryDataHolder` / `MsgReadBinary`）**会被派生类继承**，并且必须写进类型的扩展字段
  （`extensions.json`）—— 因为 odradek 的 JSON 导出正是沿着这些字段走的。
* 精确复刻了 Java 的 `Float.toString` / `Double.toString` 排版（例如 `7.796708E-4`）。

## 已知限制

1. **本游戏用到的 DS2 `MsgReadBinary` 回调已全部移植**，包括 `PhysicsShapeResource`、
   `PhysicsRagdollResource`（Jolt）与 `FacialRigSettingWithLODResource`（RigLogic）。
   真实数据上不再发生截断：全量导出 `WwiseWemResource` 报 `0 object(s) past a truncation`，
   遍历全部 5,700 个 `GraphSoundResource` 的跳链也没有任何组被截断。
   万一将来某组真的撞上未实现的回调，行为仍是「读到该对象为止」并明确报出
   `N object(s) past a truncation` / 写进 `soundmap_report.txt`。
2. **已修复：对象被重新解析时 link 游标会错位。** span 是用滑动窗口读的；某个对象越过窗口时，会
   从对象起点重新解析，但**组级的 link / locator 游标没有回卷**，于是那次中止的尝试消耗掉的 link
   被白算了一次。重试一次 = 多消费一条 link = 该组之后所有指针整体错开一个。
   触发点可复现：组 `31127` 的对象 `3386` 是 323 KB 的 `SkeletonAnimationResource`，字节区间
   `8,151,140..8,474,207`，正好横跨 8 MiB 的窗口边界。
   现在 `StreamingObjectReader` 每个对象记一次游标快照，重试前回卷。
   验证：导出 `31127:3386` 与 `odradek export` 自己导出的结果比对，15/15 个字段完全相等。
3. `cptr` 指针输出为 `<cptr to 组:下标>`；odradek 的 `CPtr` 没有重写 `toString()`，
   会打印不可复刻的 Java 对象身份串。

## 许可

**GPL-3.0**：本项目是 odradek 的衍生作品，而 odradek 采用 GPL-3.0。详见 `LICENSE`。

`Data/` 里的类型定义来自游戏，分发方式与 odradek 一致。
