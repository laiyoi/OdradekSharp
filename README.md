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
| `WwiseWemResource` | 400/400、7,816/7,816 逐字节相同 |
| `WwiseID` | 401/401 逐字节相同 |
| `WwiseBankResource` | 74/74 逐字节相同 |
| `GraphSoundResource` | 394/400 |
| `GraphProgramResource` | 395/401 |
| `NodeConstantsResource` | 390/401 |

剩下的差异全部出现在**含指针**的对象上，且成因只有一个（见下面"已知限制"第 2 条）。

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
| 全部 7,838 个 `WwiseWemResource` | ~30 s | ~4.9 GB |
| 400 个 `GraphSoundResource`（38 个组） | 0.6 s | ~1.0 GB |
| 400 个 `WwiseID`（48 个组） | 0.3 s | ~0.9 GB |
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

1. **`PhysicsRagdollResource` 与 `FacialRigSettingWithLODResource` 两个回调尚未移植**
   （需要 Jolt / RigLogic）。含这两个对象的组会读到该对象为止然后**截断**，
   其后同组对象都取不到。程序会明确报出 `N object(s) past a truncation`。
   `PhysicsShapeResource` **已经**移植。
2. **link 游标缺陷**：`group 31127` 从对象 `3386` 起，会多消费一条 link，导致该组之后所有指针整体错开一个。
   现象是 `SkeletonAnimationResource` 的 `[1]Skeleton` 属性被读了两次。复现：

   ```
   ODRADEKSHARP_TRACE_LINKS=1 odradeksharp read <游戏目录> 31127:3386 --json --no-subgroups
   ```

   判据（odradek 自己建的 link 数据库 `%LOCALAPPDATA%\Odradek\links-*.db`）：
   对象 `31127:3386` 恰好两个指针 —— `[attr1] -> 77626:2164`、`[attr7] -> 31127:1159`。
3. `cptr` 指针输出为 `<cptr to 组:下标>`；odradek 的 `CPtr` 没有重写 `toString()`，
   会打印不可复刻的 Java 对象身份串。

## 许可

**GPL-3.0**：本项目是 odradek 的衍生作品，而 odradek 采用 GPL-3.0。详见 `LICENSE`。

`Data/` 里的类型定义来自游戏，分发方式与 odradek 一致。
