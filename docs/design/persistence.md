# 数据持久化详细设计

> 父文档：[详细设计索引](index.md)
> 状态：**待审核（修订版 v2）**

## 1. 现状分析

### 1.1 已有基础设施

存储层（`FileStorage` / `PageManager` / `Wal`）已完整实现：

| 组件 | 状态 | 能力 |
|------|------|------|
| `FileHeader` | ✅ 已实现 | 76 字节文件头，含 `CollectionRootPage` 字段（当前恒为 0） |
| `PageManager` | ✅ 已实现 | mmap 读写、页分配/释放、空闲链表、文件自动增长 |
| `Wal` | ✅ 已实现 | WAL 追加、CRC32 校验、事务提交/回滚、检查点、崩溃恢复重放 |
| `FileStorage` | ✅ 已实现 | 门面封装，`WriteTransaction` 原子事务 API |
| `PageType` 枚举 | ✅ 已定义 | `CollectionMeta`, `VectorData`, `HNSWGraph`, `ScalarIndex`, `Overflow` |
| `HNSWIndex.Serialize/Deserialize` | ✅ 已实现 | 二进制流式序列化/反序列化 |

### 1.2 内存数据冗余分析

当前 `Collection` 内存中存在**三份数据冗余**：

| 数据 | 位置 1 | 位置 2 | 位置 3 |
|------|--------|--------|--------|
| Vector | `_records[id].Vector` | `HNSWNode.Vector` | — |
| Metadata | `_records[id].Metadata` | `ScalarIndex._recordMetadata[id]` | — |
| Text | `_records[id].Text` | — | — |
| RecordId | `_records` 键 | `HNSWNode.RecordId` | `ScalarIndex._recordMetadata` 键 |

**结论**：`_records` 字典中只有 **Text** 是唯一的，Vector 和 Metadata 均已在索引中存储副本。

### 1.3 当前缺口

1. `Collection` 不持有 `FileStorage` 引用，无法读写页。
2. 无任何序列化逻辑（VectorRecord / ScalarIndex / 集合目录）。
3. `FileHeader.CollectionRootPage` 始终为 0。
4. `VectorLiteDB` 打开已有文件时不加载历史集合。
5. `_nextRecordId` 不在重启后恢复。
6. 每次写入操作不产生任何持久化记录。

## 2. 设计目标与约束

### 2.1 目标

| 优先级 | 目标 |
|--------|------|
| **P0** | **零数据丢失**：每条写操作（Insert/Delete）在方法返回前即持久化到 WAL |
| **P0** | 崩溃恢复：任何时刻断电/崩溃，重启后数据完整还原到最后一次成功操作 |
| P0 | 检查点操作原子性——中途崩溃不损坏数据文件 |
| **P1** | **按需加载**：启动时仅加载索引（HNSW + ScalarIndex），记录文本从页延迟读取 |
| P1 | 对现有公开 API（`ICollection`、`VectorLiteDB`）**零破坏性变更** |
| P1 | 加载性能：10 万条 128 维记录冷启动 < 5 秒 |
| P2 | 检查点增量优化（仅脏集合刷盘） |

### 2.2 约束

| 约束 | 说明 |
|------|------|
| 持久化粒度 | **逐条 WAL**。每次 Insert/Delete 追加逻辑 WAL 记录，确保零丢失。 |
| 索引持久化粒度 | **检查点快照**。HNSW/ScalarIndex 仅在检查点时序列化到页。 |
| 内存模型 | HNSW 向量 + ScalarIndex 元数据**常驻内存**；Text **按需从页加载**。 |
| 并发模型 | 维持现有 SWMR 模型（`VectorLiteDB._rwLock`）。 |

### 2.3 设计决策：混合持久化策略

本设计采用 **「逐条 WAL + 检查点快照」混合方案**：

```text
写操作 → 逻辑 WAL 记录（即时持久化，保证零丢失）
          ↓
检查点  → 全量快照写入页（HNSW/ScalarIndex/Text/目录）
          ↓
          截断 WAL
          ↓
恢复    → 加载最近检查点快照 + 重放检查点后的 WAL 记录
```

**为什么不全部逐条 WAL？**

HNSW 图结构在每次 Insert 时产生复杂的多层连接变更（入口点变化、多层邻居列表增删、裁剪），难以用简洁的逻辑操作描述。将 HNSW 视为内存数据结构、仅在检查点快照保存是更可靠的策略。

**为什么不仅检查点快照？**

用户明确要求零数据丢失。逐条 WAL 记录 Insert/Delete 操作，成本仅为一次顺序磁盘写入（~10-100μs），可完全消除数据丢失窗口。

## 3. 整体方案

### 3.1 三层存储模型

```text
┌──────────────────────────────────────────────┐
│  Hot Tier (常驻内存)                          │
│  ├── HNSW 图结构 + 向量数据 (搜索热路径)       │
│  ├── ScalarIndex 倒排索引 + 元数据 (过滤热路径) │
│  └── 记录ID集合 (存在性检查)                   │
├──────────────────────────────────────────────┤
│  Warm Tier (内存，WAL 保护)                   │
│  ├── 最近插入的记录文本 (_pendingTexts)        │
│  └── WAL 逻辑记录 (Insert/Delete)             │
├──────────────────────────────────────────────┤
│  Cold Tier (页存储，按需加载)                  │
│  ├── 记录文本 (TextStore 页链)                │
│  ├── HNSW 快照 (HNSWGraph 页链)              │
│  ├── ScalarIndex 快照 (ScalarIndex 页链)      │
│  └── 集合目录 (CollectionMeta 页链)           │
└──────────────────────────────────────────────┘
```

### 3.2 消除 `_records` 字典

当前 `Collection._records` (`Dictionary<ulong, VectorRecord>`) 是最大的内存冗余源。
新设计**移除 `_records`**，VectorRecord 从现有索引 + TextStore 按需组装：

| VectorRecord 字段 | 来源 | 内存状态 |
|-------------------|------|----------|
| `Id` | HNSW 节点 `RecordId` | 常驻 |
| `Vector` | `HNSWNode.Vector` | 常驻（搜索必需） |
| `Metadata` | `ScalarIndex._recordMetadata[id]` | 常驻（过滤必需） |
| `Text` | `TextStore` (页 + 内存缓冲) | **按需加载** |

```csharp
// 记录组装（概念代码）
internal VectorRecord AssembleRecord(ulong id)
{
    var node = _hnswIndex.GetNode(id);
    var metadata = _scalarIndex.GetRecordMetadata(id);
    var text = _textStore.GetText(id); // 可能从页读取

    return new VectorRecord
    {
        Id = id,
        Vector = node.Vector.ToArray(),
        Metadata = metadata != null ? new Dictionary<string, object>(metadata) : null,
        Text = text
    };
}
```

### 3.3 数据流总览

```text
写入路径 (每次操作):
  InsertAsync
    → WAL 追加 RecordInsert 逻辑记录 (含 Vector + Metadata + Text) → fsync
    → 内存更新: HNSW.Insert + ScalarIndex.Add + TextStore.AddPending
    → 返回 (数据已持久化)

  DeleteAsync
    → WAL 追加 RecordDelete 逻辑记录 (仅 RecordId) → fsync
    → 内存更新: HNSW.MarkDeleted + ScalarIndex.Remove + TextStore.RemovePending
    → 返回

检查点路径 (定时 / 手动):
  Checkpoint
    → 遍历集合 → 序列化 HNSW/ScalarIndex/TextStore → WriteTransaction 写入页
    → 更新集合目录 → 刷新 FileHeader
    → 截断 WAL

加载路径 (启动):
  Open
    → WAL 物理重放 (页级恢复)
    → 读取集合目录
    → 对每个集合:
        加载 HNSW (从 HNSWGraph 页链反序列化)
        加载 ScalarIndex (从 ScalarIndex 页链反序列化)
        加载 TextStore 索引 (仅 recordId→位置映射，不加载文本内容)
    → 重放 WAL 逻辑记录 (应用检查点后的 Insert/Delete)
```

### 3.4 页面组织结构

```text
┌───────────────────────────────────┐
│  Page 0: FileHeader               │ ← CollectionRootPage 指向集合目录首页
├───────────────────────────────────┤
│  Page N: CollectionMeta (0x01)    │ ← 集合目录条目（页链）
│    → HNSWRootPage ────────────────┼──┐
│    → ScalarIndexRootPage ─────────┼──┤
│    → TextStoreRootPage ───────────┼──┤
├───────────────────────────────────┤  │
│  Page A: HNSWGraph (0x03)         │◄─┘  HNSW 快照页链
│  Page A+1: HNSWGraph (0x03)       │
│  ...                              │
├───────────────────────────────────┤
│  Page B: ScalarIndex (0x04)       │     标量索引快照页链
│  Page B+1: ScalarIndex (0x04)     │
│  ...                              │
├───────────────────────────────────┤
│  Page C: VectorData (0x02)        │     文本存储页链（带前置索引）
│  Page C+1: VectorData (0x02)      │
│  ...                              │
├───────────────────────────────────┤
│  Free pages (链表)                │
└───────────────────────────────────┘
```

## 4. 逻辑 WAL（零数据丢失）

### 4.1 新增 WAL 操作类型

在现有 `WalOperationType` 枚举中新增逻辑操作：

```csharp
internal enum WalOperationType : byte
{
    // ... 现有的页级操作保持不变 ...
    PageWrite   = 0x01,
    PageAlloc   = 0x02,
    PageFree    = 0x03,
    TxBegin     = 0x10,
    TxCommit    = 0x11,
    TxRollback  = 0x12,
    Checkpoint  = 0x20,

    // 新增：逻辑操作
    RecordInsert = 0x30,  // 插入记录（Data = 序列化的完整记录）
    RecordDelete = 0x31,  // 删除记录（Data = CollectionName + RecordId）
}
```

### 4.2 RecordInsert WAL 记录格式

```text
TargetPageId 字段：不使用（设为 0）
Data 部分：
  [0..3]      CollectionNameLength (uint, UTF-8 字节数)
  [4..N]      CollectionName (UTF-8)
  [N+1..N+8]  RecordId (ulong)
  [N+9..N+12] Dimensions (uint)
  [N+13..]    Vector (float[], Dimensions × 4 字节)
  [...]       MetadataLength (uint) + MetadataJson (UTF-8)
  [...]       TextLength (uint) + Text (UTF-8)
```

### 4.3 RecordDelete WAL 记录格式

```text
TargetPageId 字段：不使用（设为 0）
Data 部分：
  [0..3]      CollectionNameLength (uint, UTF-8 字节数)
  [4..N]      CollectionName (UTF-8)
  [N+1..N+8]  RecordId (ulong)
```

### 4.4 写入路径变更

```csharp
// InsertAsync 新流程（概念代码）
public Task<ulong> InsertAsync(VectorRecord record, CancellationToken ct)
{
    // 1. 分配 ID
    ulong id;
    lock (_writeLock)
    {
        id = _nextRecordId++;
        record.Id = id;
    }

    // 2. 序列化并写入 WAL（fsync 保证持久化）
    var walData = WalRecordSerializer.SerializeInsert(Name, record);
    _storage.LogLogicalOperation(WalOperationType.RecordInsert, walData);

    // 3. 更新内存索引
    lock (_writeLock)
    {
        _hnswIndex.Insert(id, record.Vector);
        _scalarIndex.Add(id, record.Metadata);
        _textStore.AddPending(id, record.Text);
    }

    _isDirty = true;
    return Task.FromResult(id);
}
```

> **关键**：WAL 写入（步骤 2）在内存更新（步骤 3）之前完成。即使步骤 3 执行到一半崩溃，WAL 重放时也会完整执行此操作。

### 4.5 WAL 单记录事务

每条逻辑操作自成一个微事务：`TxBegin` → `RecordInsert/RecordDelete` → `TxCommit`。对外表现为一次 WAL 调用，内部自动包装事务边界。

在 `FileStorage` 中新增便捷方法：

```csharp
/// <summary>
/// 写入一条逻辑操作记录（自动包装为微事务并 fsync）。
/// 用于 Insert/Delete 等需要即时持久化的操作。
/// </summary>
internal void LogLogicalOperation(WalOperationType opType, ReadOnlySpan<byte> data);
```

### 4.6 WAL 增长控制

两次检查点之间，WAL 按以下速率增长：

| 操作 | 单条 WAL 记录大小（128 维、100B 元数据、200B 文本） |
|------|-----------------------------------------------------|
| RecordInsert | ~37(头) + 4(集合名) + 8(ID) + 4(Dims) + 512(Vec) + 104(Meta) + 204(Text) ≈ **873 字节** |
| RecordDelete | ~37(头) + 4(集合名) + 8(ID) ≈ **49 字节** |

默认 5 分钟检查点间隔内，1 万次 Insert ≈ **8.5 MB** WAL 增长。完全可接受。

### 4.7 崩溃恢复流程（修订）

```text
RECOVERY(filePath):
    1. 物理恢复：重放 WAL 中的 PageWrite 操作（现有逻辑不变）
    2. 加载检查点快照：从页读取集合目录、HNSW、ScalarIndex、TextStore 索引
    3. 逻辑恢复：扫描 WAL 中的 RecordInsert / RecordDelete 记录
       - 仅处理已提交事务中的逻辑操作
       - 按 SequenceNumber 升序重放
       - RecordInsert → Collection.ReplayInsert(record)
       - RecordDelete → Collection.ReplayDelete(recordId)
    4. 逻辑恢复完成后，内存状态与崩溃前一致
```

> **注意**：逻辑恢复发生在检查点快照加载之后。因此只需重放「最近一次检查点之后」的 WAL 逻辑记录。检查点时在 WAL 中写入 `Checkpoint` 标记，恢复时从最后一个 `Checkpoint` 标记之后开始重放逻辑操作。

## 5. 文本存储 (TextStore)

### 5.1 设计理念

Text 是唯一不被索引层冗余持有的字段，也是最可能包含大量数据的字段（RAG 场景中存储原文）。将 Text 独立管理，实现按需加载。

### 5.2 TextStore 类设计

```csharp
namespace Mugu.AI.VectorLite.Storage;

/// <summary>
/// 文本存储：管理记录文本的持久化和按需加载。
/// 冷文本存储在 VectorData 页链中，热文本（最近写入）在内存缓冲。
/// </summary>
internal sealed class TextStore
{
    // 内存缓冲：最近插入但尚未检查点的文本
    private readonly Dictionary<ulong, string?> _pendingTexts = new();

    // 文本位置索引：recordId → 在页链字节流中的偏移
    private readonly Dictionary<ulong, long> _textOffsets = new();

    // 页链页ID序列（用于按偏移随机访问）
    private readonly List<ulong> _chainPageIds = new();

    // 页可用空间大小
    private readonly int _usablePageSize;

    // FileStorage 引用（用于读取页数据）
    private readonly FileStorage _storage;

    /// <summary>添加待写入文本（Insert 时调用）</summary>
    internal void AddPending(ulong recordId, string? text);

    /// <summary>移除待写入文本（Delete 时调用）</summary>
    internal void RemovePending(ulong recordId);

    /// <summary>
    /// 获取文本。优先从内存缓冲读取，其次从页存储按需读取。
    /// </summary>
    internal string? GetText(ulong recordId);

    /// <summary>
    /// 序列化全部文本（检查点时调用）。
    /// 返回包含索引 + 文本数据的字节数组，写入 VectorData 页链。
    /// </summary>
    internal byte[] Serialize(IEnumerable<ulong> allRecordIds);

    /// <summary>
    /// 从页链加载文本索引（启动时调用）。
    /// 仅读取索引部分，不加载文本内容。
    /// </summary>
    internal static TextStore LoadIndex(
        FileStorage storage, ulong textStoreRootPage);
}
```

### 5.3 TextStore 页链格式

```text
[0..7]            EntryCount (ulong, 有文本的记录数)
[8..15]           DataSectionOffset (ulong, 文本数据区起始字节偏移)
[16..16+N*16-1]   TextIndex (按 RecordId 升序排列):
    For each entry:
      [+0..+7]    RecordId (ulong)
      [+8..+15]   TextByteOffset (ulong, 相对于 DataSectionOffset 的偏移)
[DataSectionOffset..] TextData:
    For each entry (与 TextIndex 顺序一致):
      [+0..+3]    TextLength (uint, UTF-8 字节数)
      [+4..+N]    Text (UTF-8)
```

### 5.4 按需读取算法

```
GetText(recordId):
    // 优先从内存缓冲读取
    if _pendingTexts.TryGetValue(recordId, out text):
        return text

    // 从页存储按需读取
    if not _textOffsets.TryGetValue(recordId, out offset):
        return null  // 无文本

    absoluteOffset = _dataSectionOffset + offset
    (pageIndex, pageOffset) = DivMod(absoluteOffset, _usablePageSize)
    pageId = _chainPageIds[pageIndex]

    // 先读取 TextLength (4 字节)
    lengthBytes = ReadFromChain(pageId, pageOffset, 4)
    textLength = ReadUInt32(lengthBytes)

    if textLength == 0: return null

    // 读取文本内容（可能跨页）
    textBytes = ReadFromChain(pageId, pageOffset + 4, textLength)
    return Encoding.UTF8.GetString(textBytes)
```

### 5.5 跨页读取

文本数据可能横跨两个相邻页。`ReadFromChain` 方法处理这种情况：

```
ReadFromChain(startPageId, offsetInPage, length):
    if offsetInPage + length <= _usablePageSize:
        // 单页内，直接从 mmap 读取
        return storage.ReadPageDataRange(startPageId, offsetInPage, length)

    // 跨页读取
    result = new byte[length]
    firstPartLen = _usablePageSize - offsetInPage
    storage.ReadPageDataRange(startPageId, offsetInPage, firstPartLen) → result[0..]

    remaining = length - firstPartLen
    nextPageIndex = _chainPageIds.IndexOf(startPageId) + 1
    nextPageId = _chainPageIds[nextPageIndex]
    storage.ReadPageDataRange(nextPageId, 0, remaining) → result[firstPartLen..]

    return result
```

### 5.6 启动时加载策略

```
LoadIndex(storage, textStoreRootPage):
    if textStoreRootPage == 0: return empty TextStore

    // 1. 遍历页链，缓存页ID序列
    chainPageIds = WalkPageChain(storage, textStoreRootPage)

    // 2. 仅读取头部（EntryCount + DataSectionOffset + TextIndex）
    entryCount = ReadUInt64FromChain(offset=0)
    dataSectionOffset = ReadUInt64FromChain(offset=8)
    indexSize = 16 + entryCount * 16  // 头部 + 索引

    // 3. 解析 TextIndex，构建 _textOffsets
    for i in 0..entryCount-1:
        recordId = ReadUInt64FromChain(16 + i * 16)
        textOffset = ReadUInt64FromChain(16 + i * 16 + 8)
        _textOffsets[recordId] = textOffset

    // 文本数据本身不加载！
    return textStore
```

**内存开销**：10 万条记录的文本索引 = 100K × 16 字节 = **1.6 MB**。极低。

## 6. 页链工具 (PageChainIO)

HNSW 索引、标量索引、文本存储等均需将任意长度字节流分割写入多个页。为此引入 `PageChainIO` 工具类。

### 6.1 类设计

```csharp
namespace Mugu.AI.VectorLite.Storage;

/// <summary>
/// 页链读写工具：将任意长度字节数据写入/读出页面链。
/// 页间通过 PageHeader.NextPageId 串联。
/// </summary>
internal static class PageChainIO
{
    /// <summary>
    /// 将数据写入新分配的页链，返回首页 ID。
    /// 所有页通过 ctx 在 WriteTransaction 内分配和写入。
    /// </summary>
    internal static ulong WriteChain(
        FileStorage.WriteContext ctx,
        FileStorage storage,
        PageType pageType,
        ReadOnlySpan<byte> data);

    /// <summary>
    /// 从页链读取全部数据到连续字节数组。
    /// 用于 HNSW / ScalarIndex 的一次性加载。
    /// </summary>
    internal static byte[] ReadChain(FileStorage storage, ulong firstPageId);

    /// <summary>
    /// 遍历页链，返回所有页ID的有序列表。
    /// 用于 TextStore 建立随机访问映射。
    /// </summary>
    internal static List<ulong> WalkChain(FileStorage storage, ulong firstPageId);

    /// <summary>
    /// 从页链的指定字节偏移处读取指定长度的数据（随机访问）。
    /// chainPageIds 为 WalkChain 返回的页ID序列。
    /// </summary>
    internal static int ReadAt(
        FileStorage storage,
        List<ulong> chainPageIds,
        long byteOffset,
        Span<byte> destination);

    /// <summary>释放整个页链的所有页。</summary>
    internal static void FreeChain(
        FileStorage.WriteContext ctx,
        FileStorage storage,
        ulong firstPageId);
}
```

### 6.2 写入算法

```
WriteChain(ctx, storage, pageType, data):
    // 前向分配：先分配所有页获得 ID 列表
    pageCount = CeilDiv(data.Length, storage.UsablePageSize)
    pageIds = [ctx.AllocatePage(pageType) for i in 0..pageCount-1]

    // 逐页写入，设置 NextPageId
    for i in 0..pageCount-1:
        offset = i * storage.UsablePageSize
        chunk = data[offset..min(offset+usable, data.Length)]
        nextPageId = (i < pageCount-1) ? pageIds[i+1] : 0

        header = PageHeader {
            PageId = pageIds[i],
            PageType = pageType,
            UsedBytes = chunk.Length,
            NextPageId = nextPageId,
            Checksum = 0
        }
        fullPage = header.Bytes ++ chunk ++ zeroPadding
        ctx.WritePage(pageIds[i], fullPage)

    return pageIds[0]
```

### 6.3 ReadChain（全量读取）

```
ReadChain(storage, firstPageId):
    result = MemoryStream()
    currentPageId = firstPageId

    while currentPageId != 0:
        header = storage.ReadPageHeader(currentPageId)
        data = new byte[header.UsedBytes]
        storage.ReadPageData(currentPageId, data)
        result.Write(data[..header.UsedBytes])
        currentPageId = header.NextPageId

    return result.ToArray()
```

### 6.4 ReadAt（随机访问）

```
ReadAt(storage, chainPageIds, byteOffset, destination):
    usable = storage.UsablePageSize
    pageIndex = byteOffset / usable
    offsetInPage = byteOffset % usable
    bytesRead = 0

    while bytesRead < destination.Length and pageIndex < chainPageIds.Count:
        pageId = chainPageIds[pageIndex]
        availableInPage = usable - offsetInPage
        toRead = min(destination.Length - bytesRead, availableInPage)

        storage.ReadPageDataRange(pageId, offsetInPage, toRead)
            → destination[bytesRead..bytesRead+toRead]

        bytesRead += toRead
        pageIndex++
        offsetInPage = 0  // 后续页从头开始

    return bytesRead
```

### 6.5 FreeChain

```
FreeChain(ctx, storage, firstPageId):
    currentPageId = firstPageId
    while currentPageId != 0:
        header = storage.ReadPageHeader(currentPageId)
        nextPageId = header.NextPageId
        ctx.FreePage(currentPageId)
        currentPageId = nextPageId
```

## 7. 记录序列化（WAL 记录格式）

WAL 的 RecordInsert 操作携带完整记录数据，使用 `RecordSerializer` 进行二进制编码。

### 7.1 单条 VectorRecord 二进制布局

```text
偏移(相对记录起始)  大小              字段
─────────────────────────────────────────────────────
0x00                8                 RecordId (ulong)
0x08                4                 Dimensions (uint)
0x0C                Dimensions × 4    Vector (float[], 小端序)
...                 4                 MetadataLength (uint, JSON UTF-8 字节数, 0=无元数据)
...                 MetadataLength    MetadataJson (UTF-8 JSON)
...                 4                 TextLength (uint, UTF-8 字节数, 0=无文本)
...                 TextLength        Text (UTF-8)
─────────────────────────────────────────────────────
```

### 7.2 元数据 JSON 序列化规则

`Metadata` 字段类型为 `Dictionary<string, object>`，值支持 `string` / `long` / `double` / `bool`。使用 `System.Text.Json.JsonSerializer` 序列化为 JSON 字符串的 UTF-8 字节。

- `null` 或空字典 → `MetadataLength = 0`，不写入 JSON。
- 反序列化时使用 `JsonElement` 按类型还原：
  - `JsonValueKind.String` → `string`
  - `JsonValueKind.Number`（含小数点）→ `double`，否则 → `long`
  - `JsonValueKind.True / False` → `bool`

### 7.3 RecordSerializer 类设计

```csharp
namespace Mugu.AI.VectorLite.Storage;

/// <summary>
/// 向量记录的二进制序列化/反序列化。
/// 同时服务于 WAL 逻辑记录和检查点快照。
/// </summary>
internal static class RecordSerializer
{
    /// <summary>将一条记录序列化到 BinaryWriter</summary>
    internal static void Write(BinaryWriter writer, VectorRecord record);

    /// <summary>从 BinaryReader 反序列化一条记录</summary>
    internal static VectorRecord Read(BinaryReader reader);

    /// <summary>
    /// 序列化 WAL RecordInsert 数据：CollectionName + Record
    /// </summary>
    internal static byte[] SerializeInsert(string collectionName, VectorRecord record);

    /// <summary>
    /// 序列化 WAL RecordDelete 数据：CollectionName + RecordId
    /// </summary>
    internal static byte[] SerializeDelete(string collectionName, ulong recordId);

    /// <summary>反序列化 WAL RecordInsert 数据</summary>
    internal static (string CollectionName, VectorRecord Record)
        DeserializeInsert(ReadOnlySpan<byte> data);

    /// <summary>反序列化 WAL RecordDelete 数据</summary>
    internal static (string CollectionName, ulong RecordId)
        DeserializeDelete(ReadOnlySpan<byte> data);
}
```

## 8. 标量索引序列化 (ScalarIndexSerializer)

### 8.1 设计策略

**只需序列化 `_recordMetadata`**。加载时遍历 `_recordMetadata` 即可重建 `_index`。

### 8.2 二进制布局

```text
[0..3]    RecordCount (uint)
For each record:
  [+0..+7]   RecordId (ulong)
  [+8..+11]  FieldCount (uint)
  For each field:
    [+0..+3]       FieldNameLength (uint, UTF-8 字节数)
    [+4..+N]       FieldName (UTF-8)
    [+N+1]         ValueType (byte: 0=string, 1=long, 2=double, 3=bool)
    [+N+2..]       Value:
                      string → [4字节长度 + UTF-8]
                      long   → 8字节小端
                      double → 8字节小端
                      bool   → 1字节 (0/1)
```

### 8.3 ScalarIndexSerializer 类设计

```csharp
namespace Mugu.AI.VectorLite.Storage;

internal static class ScalarIndexSerializer
{
    internal static byte[] Serialize(ScalarIndex index);
    internal static ScalarIndex Deserialize(ReadOnlySpan<byte> data);
}
```

需要在 `ScalarIndex` 中新增：

```csharp
internal IReadOnlyDictionary<ulong, Dictionary<string, object>> RecordMetadata => _recordMetadata;
internal void BulkLoad(Dictionary<ulong, Dictionary<string, object>> recordMetadata);
```

`BulkLoad` 接收反序列化得到的 `_recordMetadata`，遍历重建 `_index`。

## 9. HNSW 索引持久化

复用现有 `HNSWIndex.Serialize()` / `Deserialize()`。

- **检查点**：`Serialize()` → `PageChainIO.WriteChain(PageType.HNSWGraph)`
- **启动**：`PageChainIO.ReadChain()` → `Deserialize()`
- **空索引**：`HNSWRootPage = 0`，不分配页链。

## 10. 集合目录 (CollectionCatalog)

### 10.1 目录格式

集合目录序列化为连续字节流，通过 `PageChainIO` 写入 `CollectionMeta` 页链。

```text
[0..3]    CollectionCount (uint)
For each collection:
  [+0..+3]     NameLength (uint, UTF-8 字节数)
  [+4..+N]     Name (UTF-8)
  [+N..+N+3]   Dimensions (uint)
  [+N+4]       DistanceMetric (byte: 0=Cosine, 1=Euclidean, 2=DotProduct)
  [+N+5..+N+8] HnswM (int32)
  [+N+9..+N+12] HnswEfConstruction (int32)
  [+N+13..+N+20] NextRecordId (ulong, 下一条记录的自增ID)
  [+N+21..+N+28] HNSWRootPage (ulong, 0=无索引)
  [+N+29..+N+36] ScalarIndexRootPage (ulong, 0=无索引)
  [+N+37..+N+44] TextStoreRootPage (ulong, 0=无文本)
```

### 10.2 CollectionCatalog 类设计

```csharp
namespace Mugu.AI.VectorLite.Storage;

internal struct CollectionCatalogEntry
{
    internal string Name;
    internal int Dimensions;
    internal DistanceMetric DistanceMetric;
    internal int HnswM;
    internal int HnswEfConstruction;
    internal ulong NextRecordId;
    internal ulong HNSWRootPage;
    internal ulong ScalarIndexRootPage;
    internal ulong TextStoreRootPage;
}

internal static class CollectionCatalog
{
    internal static byte[] Serialize(IReadOnlyList<CollectionCatalogEntry> entries);
    internal static List<CollectionCatalogEntry> Deserialize(ReadOnlySpan<byte> data);
}
```

## 11. 刷盘算法 (Flush / Checkpoint)

### 11.1 Collection 层变更

`Collection` 新增以下成员：

```csharp
internal sealed class Collection : ICollection
{
    // 新增字段
    private bool _isDirty;
    private ulong _hnswRootPage;
    private ulong _scalarIndexRootPage;
    private ulong _textStoreRootPage;
    private TextStore _textStore;           // 替代 _records 中的 Text

    // _records 字典移除！ 以下字段不再 readonly：
    private HNSWIndex _hnswIndex;
    private ScalarIndex _scalarIndex;
    private QueryEngine _queryEngine;

    // 新增属性
    internal bool IsDirty => _isDirty;
    internal DistanceMetric DistanceMetric { get; }
    internal int HnswM { get; }
    internal int HnswEfConstruction { get; }

    /// <summary>将集合数据刷盘到 FileStorage</summary>
    internal void FlushToStorage(FileStorage storage);

    /// <summary>从 FileStorage 加载集合数据（启动时）</summary>
    internal static Collection LoadFromStorage(
        FileStorage storage, CollectionCatalogEntry entry,
        VectorLiteOptions options, ILogger? logger);

    /// <summary>WAL 逻辑重放：插入记录（启动恢复时调用）</summary>
    internal void ReplayInsert(VectorRecord record);

    /// <summary>WAL 逻辑重放：删除记录（启动恢复时调用）</summary>
    internal void ReplayDelete(ulong recordId);

    /// <summary>根据 ID 组装完整的 VectorRecord</summary>
    internal VectorRecord? AssembleRecord(ulong id);
}
```

### 11.2 FlushToStorage 算法

```
FlushToStorage(storage):
    if not _isDirty: return

    storage.WriteTransaction(ctx =>
        // 1. 释放旧页链
        if _hnswRootPage != 0:
            PageChainIO.FreeChain(ctx, storage, _hnswRootPage)
        if _scalarIndexRootPage != 0:
            PageChainIO.FreeChain(ctx, storage, _scalarIndexRootPage)
        if _textStoreRootPage != 0:
            PageChainIO.FreeChain(ctx, storage, _textStoreRootPage)

        // 2. 序列化并写入新页链
        if _hnswIndex.Count > 0:
            hnswBytes = _hnswIndex.Serialize()
            _hnswRootPage = PageChainIO.WriteChain(
                ctx, storage, PageType.HNSWGraph, hnswBytes)

            scalarBytes = ScalarIndexSerializer.Serialize(_scalarIndex)
            _scalarIndexRootPage = PageChainIO.WriteChain(
                ctx, storage, PageType.ScalarIndex, scalarBytes)

            // 合并 pending texts 到全量 TextStore 并写入
            allRecordIds = _hnswIndex.GetActiveNodeIds()
            textBytes = _textStore.Serialize(allRecordIds)
            _textStoreRootPage = PageChainIO.WriteChain(
                ctx, storage, PageType.VectorData, textBytes)
        else:
            _hnswRootPage = 0
            _scalarIndexRootPage = 0
            _textStoreRootPage = 0

        _isDirty = false
    )
```

### 11.3 VectorLiteDB.Checkpoint 变更

```
Checkpoint():
    _rwLock.EnterWriteLock()
    try:
        // 1. 刷盘所有脏集合
        foreach collection in _collections.Values:
            collection.FlushToStorage(_storage)

        // 2. 序列化并写入集合目录
        FlushCatalog()

        // 3. WAL 检查点（截断 WAL）
        _storage.Checkpoint()
    finally:
        _rwLock.ExitWriteLock()
```

### 11.4 脏标记管理

以下操作设置 `_isDirty = true`：`InsertAsync`、`InsertBatchAsync`、`DeleteAsync`、`UpsertAsync`、`ReplayInsert`、`ReplayDelete`。


## 12. 加载算法 (Load)

### 12.1 VectorLiteDB 构造函数变更

```
VectorLiteDB(filePath, options):
    _storage = OpenOrCreate(filePath, options)

    // 阶段1：物理 WAL 恢复（已有逻辑，重放 PageWrite）
    _storage.RecoverFromWal()

    // 阶段2：加载检查点快照
    if _storage.Header.CollectionRootPage != 0:
        LoadCollectionsFromStorage()

    // 阶段3：逻辑 WAL 恢复（新增）
    ReplayLogicalWal()

    // 启动检查点定时器
    StartCheckpointTimer()
```

### 12.2 LoadCollectionsFromStorage 算法

```
LoadCollectionsFromStorage():
    catalogData = PageChainIO.ReadChain(_storage, _storage.Header.CollectionRootPage)
    entries = CollectionCatalog.Deserialize(catalogData)

    foreach entry in entries:
        collection = Collection.LoadFromStorage(_storage, entry, _options, logger)
        _collections[entry.Name] = collection
```

### 12.3 Collection.LoadFromStorage 算法

```
LoadFromStorage(storage, entry, options, logger):
    collection = new Collection(entry.Name, entry.Dimensions,
        entry.DistanceMetric, entry.HnswM, entry.HnswEfConstruction, ...)

    collection._nextRecordId = entry.NextRecordId

    // 1. 加载 HNSW 索引（全量加载到内存，搜索需要）
    if entry.HNSWRootPage != 0:
        hnswData = PageChainIO.ReadChain(storage, entry.HNSWRootPage)
        collection._hnswIndex = HNSWIndex.Deserialize(
            hnswData, distFunc, entry.HnswM, entry.HnswEfConstruction)

    // 2. 加载标量索引（全量加载到内存，过滤需要）
    if entry.ScalarIndexRootPage != 0:
        scalarData = PageChainIO.ReadChain(storage, entry.ScalarIndexRootPage)
        collection._scalarIndex = ScalarIndexSerializer.Deserialize(scalarData)

    // 3. 加载 TextStore 索引（仅索引，不加载文本内容）
    if entry.TextStoreRootPage != 0:
        collection._textStore = TextStore.LoadIndex(storage, entry.TextStoreRootPage)

    // 4. 记录页链根页
    collection._hnswRootPage = entry.HNSWRootPage
    collection._scalarIndexRootPage = entry.ScalarIndexRootPage
    collection._textStoreRootPage = entry.TextStoreRootPage
    collection._isDirty = false

    // 5. 重建 QueryEngine
    collection.RebuildQueryEngine()

    return collection
```

### 12.4 逻辑 WAL 恢复

```
ReplayLogicalWal():
    // 扫描 WAL 中最后一个 Checkpoint 标记之后的逻辑操作
    logicalOps = _storage.Wal.ReadLogicalOperationsSinceLastCheckpoint()

    foreach op in logicalOps:
        switch op.Type:
            case RecordInsert:
                (collName, record) = RecordSerializer.DeserializeInsert(op.Data)
                if _collections.TryGetValue(collName, out collection):
                    collection.ReplayInsert(record)

            case RecordDelete:
                (collName, recordId) = RecordSerializer.DeserializeDelete(op.Data)
                if _collections.TryGetValue(collName, out collection):
                    collection.ReplayDelete(recordId)
```

### 12.5 ReplayInsert / ReplayDelete

```
ReplayInsert(record):
    // 幂等：如果节点已存在（来自快照），跳过
    if _hnswIndex.ContainsNode(record.Id): return

    _hnswIndex.Add(record.Id, record.Vector)
    if record.Metadata != null:
        _scalarIndex.Add(record.Id, record.Metadata)
    if record.Text != null:
        _textStore.SetPending(record.Id, record.Text)
    if record.Id >= _nextRecordId:
        _nextRecordId = record.Id + 1
    _isDirty = true

ReplayDelete(recordId):
    // 幂等：如果节点不存在，跳过
    if not _hnswIndex.ContainsNode(recordId): return

    _hnswIndex.Delete(recordId)
    _scalarIndex.Remove(recordId)
    _textStore.Remove(recordId)
    _isDirty = true
```

## 13. AssembleRecord（按需组装记录）

消除 `_records` 字典后，查询结果中的 `VectorRecord` 从三个来源实时组装：

```
AssembleRecord(recordId):
    node = _hnswIndex.GetNode(recordId)
    if node == null: return null

    vector = node.Vector
    metadata = _scalarIndex.GetRecordMetadata(recordId)
    text = _textStore.GetText(recordId)  // 可能触发页 I/O

    return new VectorRecord(recordId, vector, metadata, text)
```

**调用时机**：`ExecuteQuery` 构造 `SearchResult` 时调用。原来从 `_records[id]` 读取，改为 `AssembleRecord(id)`。

**注意**：`GetText()` 可能涉及磁盘 I/O（从页链随机读取），但单次文本读取通常只涉及 1-2 个页，延迟可控（< 1ms on SSD）。Top-K 结果通常 K <= 100，总额外 I/O < 100ms。

## 14. DeleteCollection 持久化

```
DeleteCollection(name):
    _rwLock.EnterWriteLock()
    try:
        if _collections.TryGetValue(name, out collection):
            _storage.WriteTransaction(ctx =>
                if collection._hnswRootPage != 0:
                    PageChainIO.FreeChain(ctx, storage, collection._hnswRootPage)
                if collection._scalarIndexRootPage != 0:
                    PageChainIO.FreeChain(ctx, storage, collection._scalarIndexRootPage)
                if collection._textStoreRootPage != 0:
                    PageChainIO.FreeChain(ctx, storage, collection._textStoreRootPage)
            )
            _collections.Remove(name)
            return true
        return false
    finally:
        _rwLock.ExitWriteLock()
```

## 15. InsertAsync / DeleteAsync WAL 写入

### 15.1 InsertAsync 变更

```
InsertAsync(record):
    // 1. 分配 ID
    record = record with { Id = _nextRecordId++ }

    // 2. 写入逻辑 WAL（先于内存更新，确保零数据丢失）
    walData = RecordSerializer.SerializeInsert(_name, record)
    _storage.LogLogicalOperation(WalOperationType.RecordInsert, walData)

    // 3. 更新内存索引
    _hnswIndex.Add(record.Id, record.Vector)
    if record.Metadata != null:
        _scalarIndex.Add(record.Id, record.Metadata)
    _textStore.SetPending(record.Id, record.Text)
    _isDirty = true

    return record.Id
```

### 15.2 DeleteAsync 变更

```
DeleteAsync(recordId):
    // 1. 写入逻辑 WAL
    walData = RecordSerializer.SerializeDelete(_name, recordId)
    _storage.LogLogicalOperation(WalOperationType.RecordDelete, walData)

    // 2. 更新内存索引
    _hnswIndex.Delete(recordId)
    _scalarIndex.Remove(recordId)
    _textStore.Remove(recordId)
    _isDirty = true
```

### 15.3 FileStorage.LogLogicalOperation

```csharp
/// <summary>
/// 将逻辑操作写入 WAL（微事务：TxBegin -> op -> TxCommit + fsync）。
/// </summary>
internal void LogLogicalOperation(WalOperationType opType, byte[] data)
{
    _wal.AppendLogical(opType, data);
    _wal.Flush();  // fsync 确保持久化
}
```

## 16. 数据安全性分析

### 16.1 零数据丢失保证

每个 Insert/Delete 操作遵循 **WAL-first** 原则：

```
时间线（单次 InsertAsync）:
  t0: 序列化记录为 WAL 数据
  t1: WAL 写入 RecordInsert 记录 + fsync  <- 数据已持久化
  t2: 更新内存 HNSW/ScalarIndex/TextStore
  t3: 返回成功

  崩溃点:
    t0~t1: WAL 未 fsync -> 数据丢失 -> 操作也未返回成功 -> 一致 OK
    t1~t2: WAL 已持久化 -> 恢复时重放 -> 数据完整 OK
    t2~t3: 同上 + 内存已更新 -> 完整 OK
```

### 16.2 检查点原子性

```
时间线（Checkpoint）:
  t0: WriteTransaction 开始 -> WAL TxBegin
  t1: 序列化 HNSW/ScalarIndex/TextStore -> WAL PageWrite
  t2: WAL TxCommit + fsync -> 检查点数据已持久化
  t3: WAL Checkpoint 标记 -> 截断逻辑记录
  t4: 清空 pending texts

  崩溃点:
    t0~t2: 未提交 -> 重放跳过 -> 回退到上次快照 + 逻辑 WAL 重放 OK
    t2~t3: 已提交 -> 重放 PageWrite -> 快照更新 + 逻辑 WAL 可能重复重放（幂等）OK
    t3~t4: 快照已更新 + WAL 已截断 -> pending texts 重放后为空集 OK
```

### 16.3 崩溃场景汇总

| 场景 | 数据丢失 | 恢复策略 |
|------|----------|----------|
| 正常关闭（Dispose） | 无 | Checkpoint + 关闭 |
| 进程崩溃 | **无** | 物理 WAL 恢复 -> 加载快照 -> 逻辑 WAL 重放 |
| 系统断电 | **无**（WAL fsync 保证） | 同上 |
| WAL fsync 后、内存更新前崩溃 | **无** | 逻辑 WAL 重放补齐 |

### 16.4 幂等性保证

逻辑 WAL 重放必须是幂等的：

- `ReplayInsert`: 如果 HNSW 中已存在该 ID 的节点（来自快照），跳过插入。
- `ReplayDelete`: 如果 HNSW 中不存在该 ID，跳过删除。

## 17. 性能影响分析

### 17.1 写入路径

| 阶段 | 变更前 | 变更后 | 影响 |
|------|--------|--------|------|
| InsertAsync | 纯内存操作 | WAL 序列化 + fsync + 内存更新 | **每次约 0.1-0.5ms** |
| DeleteAsync | 纯内存操作 | WAL 序列化 + fsync + 内存更新 | **每次约 0.05-0.2ms** |
| InsertBatchAsync | N 次内存操作 | N 次 WAL 写入 + N 次内存更新 | **批量时可合并 fsync** |

> fsync 是主要开销。可考虑批量写入时使用 group commit 优化。

### 17.2 检查点路径

以 10 万条 128 维记录为例：

| 步骤 | 数据量估算 | 耗时估算 |
|------|------------|----------|
| HNSWIndex.Serialize | ~100 MB | ~300 ms |
| ScalarIndex Serialize | ~10 MB | ~50 ms |
| TextStore Serialize | ~20 MB (100K x 200B 文本) | ~100 ms |
| PageChainIO.WriteChain (总计) | ~130 MB -> ~15,900 页 | ~400 ms (SSD) |
| WAL Checkpoint | 截断文件 | ~5 ms |
| **合计** | | **~0.85 秒** |

比纯快照方案快 ~15%（不再序列化向量和元数据的冗余副本）。

### 17.3 冷启动路径

| 步骤 | 耗时估算 |
|------|----------|
| PageChainIO.ReadChain (HNSW) | ~300 ms |
| HNSWIndex.Deserialize | ~500 ms |
| ScalarIndex 反序列化 + 重建索引 | ~200 ms |
| TextStore.LoadIndex（仅索引） | ~5 ms |
| 逻辑 WAL 重放（假设 1K 条） | ~50 ms |
| **合计** | **~1.05 秒** |

比全量加载方案快 ~30%（文本懒加载、无冗余记录反序列化）。

### 17.4 查询路径影响

| 场景 | 影响 |
|------|------|
| 向量搜索 | 无影响（HNSW 搜索仍为纯内存操作） |
| 元数据过滤 | 无影响（ScalarIndex 仍为纯内存操作） |
| 结果组装 | Top-K 结果需从 TextStore 读取文本，K x ~0.5ms ≈ 50ms (K=100) |

## 18. 需新增的文件

| 文件 | 位置 | 说明 |
|------|------|------|
| `PageChainIO.cs` | `Storage/` | 页链读写工具（含 WalkChain, ReadAt） |
| `RecordSerializer.cs` | `Storage/` | WAL 记录序列化/反序列化 |
| `ScalarIndexSerializer.cs` | `Storage/` | ScalarIndex 序列化/反序列化 |
| `CollectionCatalog.cs` | `Storage/` | 集合目录序列化/反序列化 |
| `TextStore.cs` | `Storage/` | 文本存储（懒加载 + 缓冲区） |

## 19. 需修改的文件

| 文件 | 变更内容 |
|------|----------|
| `Collection.cs` | **重大重构**：移除 `_records`，新增 `_textStore`、脏标记、根页 ID 字段、`FlushToStorage`、`LoadFromStorage`、`AssembleRecord`、`ReplayInsert`/`ReplayDelete`；去 readonly；`ExecuteQuery` 使用 AssembleRecord |
| `VectorLiteDB.cs` | 三阶段恢复；`Checkpoint` 先刷脏集合再 WAL 检查点；`DeleteCollection` 释放页 |
| `ScalarIndex.cs` | 新增 `RecordMetadata` 属性、`BulkLoad`、`GetRecordMetadata(ulong)` |
| `HNSWIndex.cs` | 新增 `GetNode(ulong)` / `GetActiveNodeIds()` / `ContainsNode(ulong)` |
| `FileStorage.cs` | 新增 `LogLogicalOperation()` 方法 |
| `Wal.cs` | 新增 `AppendLogical()`、`ReadLogicalOperationsSinceLastCheckpoint()` |
| `WalOperationType.cs` | 新增 `RecordInsert = 0x30`、`RecordDelete = 0x31` |
| `VectorLiteOptions.cs` | 新增 `FlushOnDispose` 等配置（可选） |

## 20. 测试计划

### 20.1 单元测试

| 测试 | 验证点 |
|------|--------|
| `RecordSerializer_RoundTrip` | WAL Insert/Delete 序列化->反序列化值相等 |
| `RecordSerializer_AllTypes` | 含各种元数据类型和 null 文本 |
| `ScalarIndexSerializer_RoundTrip` | 序列化->反序列化后倒排索引功能正常 |
| `CollectionCatalog_RoundTrip` | 多集合目录序列化->反序列化完整恢复 |
| `PageChainIO_SmallData` | 数据 < 单页时仅分配 1 页 |
| `PageChainIO_LargeData` | 数据跨多页时页链正确串联 |
| `PageChainIO_FreeChain` | 释放后所有页回到空闲链表 |
| `PageChainIO_ReadAt` | 随机访问返回正确数据（含跨页） |
| `TextStore_PendingTexts` | SetPending/GetText/Remove 正确 |
| `TextStore_SerializeAndLoadIndex` | 序列化->LoadIndex->GetText 一致 |

### 20.2 集成测试

| 测试 | 验证点 |
|------|--------|
| `Persistence_InsertAndReopen` | 插入 -> Checkpoint -> 关闭 -> 重开 -> 数据完整（含文本） |
| `Persistence_MultipleCollections` | 多集合独立持久化和恢复 |
| `Persistence_DeleteAndReopen` | 删除记录 -> Checkpoint -> 重开 -> 不存在 |
| `Persistence_DeleteCollectionAndReopen` | 删除集合 -> Checkpoint -> 重开 -> 不存在 |
| `Persistence_SearchAfterReload` | 重开后搜索结果一致 |
| `Persistence_FilterAfterReload` | 重开后过滤结果一致 |
| `Persistence_WalRecovery_Insert` | 插入（无 Checkpoint）-> 崩溃 -> 重开 -> WAL 重放恢复 |
| `Persistence_WalRecovery_Delete` | 插入 -> Checkpoint -> 删除（无 Checkpoint）-> 崩溃 -> 重开 -> 已删除 |
| `Persistence_TextLazyLoading` | 10K 记录 -> 重开 -> 启动快 -> 查询返回正确文本 |
| `Persistence_EmptyDatabase` | 空数据库 -> 关闭 -> 重开 -> 正常 |
| `Persistence_LargeDataset` | 10K 条 -> 检查点 -> 重开 -> 完整恢复 |

### 20.3 质量门禁新增

| 基线测试 | 阈值 |
|----------|------|
| `PersistenceBaseline` | 1K 条 128 维：写入 -> 检查点 -> 重开 -> 完整性验证 |
| `WalRecoveryBaseline` | 500 条写入（无 Checkpoint）-> 崩溃 -> 重开 -> 全部恢复 |
| `CheckpointBenchmark` | 10K 条 128 维检查点耗时 < 3 秒 |
| `ColdStartBenchmark` | 10K 条 128 维冷启动耗时 < 3 秒 |

## 21. 实现顺序

```text
Phase 1: 基础设施
  +-- WalOperationType 扩展（RecordInsert, RecordDelete）
  +-- RecordSerializer（含 WAL 序列化/反序列化）
  +-- PageChainIO（含 WalkChain, ReadAt）
  +-- TextStore
  +-- ScalarIndexSerializer
  +-- CollectionCatalog
  +-- 各自的单元测试

Phase 2: WAL 扩展
  +-- Wal.AppendLogical()
  +-- Wal.ReadLogicalOperationsSinceLastCheckpoint()
  +-- FileStorage.LogLogicalOperation()
  +-- WAL 逻辑操作单元测试

Phase 3: Collection 层改造
  +-- 移除 _records，新增 _textStore
  +-- InsertAsync/DeleteAsync 加入 WAL 写入
  +-- AssembleRecord
  +-- ReplayInsert / ReplayDelete
  +-- FlushToStorage / LoadFromStorage
  +-- ScalarIndex 新增 RecordMetadata / BulkLoad / GetRecordMetadata
  +-- HNSWIndex 新增 GetNode / GetActiveNodeIds / ContainsNode
  +-- ExecuteQuery 使用 AssembleRecord

Phase 4: VectorLiteDB 层改造
  +-- 三阶段恢复（物理 WAL -> 加载快照 -> 逻辑 WAL 重放）
  +-- Checkpoint 流程改造
  +-- DeleteCollection 释放页
  +-- 集成测试

Phase 5: 质量门禁
  +-- 持久化基线测试和基准测试
```

## 22. 未来优化方向

| 方向 | 说明 |
|------|------|
| Group Commit | 批量 Insert 时合并多条 WAL 记录后一次 fsync |
| 增量检查点 | 仅序列化变更的 HNSW 节点和 ScalarIndex 条目 |
| 后台检查点 | 快照后释放写锁，后台线程写页（COW 语义） |
| TextStore LRU 缓存 | 热门文本缓存在内存，减少重复页 I/O |
| 压缩 | 对 TextStore / ScalarIndex 数据进行 LZ4 压缩 |
| 元数据懒加载 | 超大元数据也采用类似 TextStore 的按需加载模式 |
