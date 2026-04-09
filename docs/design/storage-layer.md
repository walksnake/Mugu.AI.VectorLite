# 存储层详细设计

> 父文档：[详细设计索引](index.md)

## 1. 设计目标

- 将所有数据存储在**单个 `.vldb` 文件**中，辅以一个 `.vldb-wal` 预写日志文件。
- 通过 WAL 保证写入操作的**原子性与持久性**，支持崩溃恢复。
- 通过 `MemoryMappedFile` 将数据文件映射到虚拟地址空间，由操作系统管理页面缓存，简化缓冲区管理。
- 固定大小页（默认 8 KB）组织数据，便于空间管理与对齐。

## 2. 文件头格式 (FileHeader)

文件头占据**页 0** 的前 76 字节，其余空间用零填充。

```text
偏移    大小(字节)  字段                 类型      说明
────────────────────────────────────────────────────────────────
0x00    4           Magic                uint32    魔数 0x54494C56 ("VLIT" LE)
0x04    4           Version              uint32    文件格式版本，当前 = 1
0x08    4           PageSize             uint32    页大小，默认 8192
0x0C    8           TotalPages           ulong     文件中总页数
0x14    8           FreePageListHead     ulong     空闲页链表头页ID (0 = 无空闲页)
0x1C    8           CollectionRootPage   ulong     集合元数据根页ID
0x24    4           MaxDimensions        uint32    本数据库允许的最大向量维度
0x28    8           CreatedAt            ulong     创建时间 (Unix毫秒)
0x30    8           LastCheckpoint       ulong     最后检查点时间 (Unix毫秒)
0x38    16          Reserved             byte[16]  预留扩展
0x48    4           HeaderChecksum       uint32    CRC32 校验（覆盖 0x00-0x47）
────────────────────────────────────────────────────────────────
合计: 76 字节
```

### 2.1 FileHeader 类设计

```csharp
namespace Mugu.AI.VectorLite.Storage;

/// <summary>
/// 数据库文件头，占据页0前76字节。
/// 使用 StructLayout 保证内存布局与磁盘格式一致。
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct FileHeader
{
    public const uint MagicNumber = 0x54494C56; // "VLIT" LE
    public const uint CurrentVersion = 1;
    public const int SizeInBytes = 76;

    public uint Magic;
    public uint Version;
    public uint PageSize;
    public ulong TotalPages;
    public ulong FreePageListHead;
    public ulong CollectionRootPage;
    public uint MaxDimensions;
    public ulong CreatedAt;
    public ulong LastCheckpoint;
    private unsafe fixed byte Reserved[16];
    public uint HeaderChecksum;

    /// <summary>验证魔数与版本号</summary>
    public readonly bool IsValid()
        => Magic == MagicNumber && Version <= CurrentVersion;

    /// <summary>计算并写入 CRC32 校验和</summary>
    public void UpdateChecksum() { /* CRC32 覆盖 0x00-0x47 */ }
}
```

## 3. 页面结构 (Page)

### 3.1 页面类型枚举

```csharp
internal enum PageType : byte
{
    Free            = 0x00,  // 空闲页（链表节点）
    CollectionMeta  = 0x01,  // 集合元数据
    VectorData      = 0x02,  // 向量记录数据
    HNSWGraph       = 0x03,  // HNSW图节点
    ScalarIndex     = 0x04,  // 标量倒排索引
    Overflow        = 0x06   // 溢出页（大记录续页）
}
```

### 3.2 页面头 (PageHeader)

每一页的前 25 字节为公共头部：

```text
偏移    大小    字段         类型      说明
────────────────────────────────────────────────
0x00    8       PageId       ulong     页面唯一ID（从1开始，0为文件头页）
0x08    1       PageType     byte      页面类型
0x09    4       UsedBytes    uint      本页已使用的有效字节数（不含页头）
0x0D    8       NextPageId   ulong     下一页ID（溢出链/空闲链，0表示无）
0x15    4       Checksum     uint      CRC32 校验（覆盖本页全部数据）
────────────────────────────────────────────────
合计: 25 字节
```

**可用数据空间** = `PageSize - 25`。以默认 8192 字节页为例，可用空间为 **8167 字节**。

### 3.3 向量数据页布局 (PageType = 0x02)

页头之后的数据区域：

```text
偏移(相对数据区)  大小          字段
───────────────────────────────────────
0x00              4             RecordCount (uint)
0x04              变长          Record[] (紧密排列)
```

每条 VectorRecord 的磁盘布局：

```text
偏移(相对记录起始)  大小              字段
─────────────────────────────────────────
0x00                8                 RecordId (ulong, 全局自增)
0x08                4                 Dimensions (uint)
0x0C                Dimensions × 4    Vector (float[])
...                 4                 MetadataLength (uint)
...                 MetadataLength    Metadata (UTF-8 JSON)
...                 4                 TextLength (uint, 0表示无文本)
...                 TextLength        Text (UTF-8)
...                 1                 Flags (bit0=已删除标记)
```

**溢出处理**：当单条记录超过页可用空间时，在当前页写入能容纳的部分，将 `NextPageId` 指向一个 `Overflow` 类型页，后续数据写入溢出页链，直至记录写完。

### 3.4 集合元数据页布局 (PageType = 0x01)

存储集合基本信息，每个集合占一条记录。根页通过链表串联所有集合。

```text
偏移    大小        字段
──────────────────────────────────────────
0x00    4           NameLength (uint)
0x04    NameLength  Name (UTF-8)
...     4           Dimensions (uint)
...     1           DistanceMetric (byte, 0=Cosine/1=Euclidean/2=DotProduct)
...     8           VectorDataRootPage (ulong, 该集合向量数据首页)
...     8           HNSWRootPage (ulong, 该集合HNSW索引首页)
...     8           ScalarIndexRootPage (ulong, 该集合标量索引首页)
...     8           RecordCount (ulong)
...     8           NextRecordId (ulong, 下一条记录的自增ID)
```

## 4. PageManager 设计

```csharp
namespace Mugu.AI.VectorLite.Storage;

/// <summary>
/// 页面管理器：负责页的分配、回收、读写。
/// 所有页操作均通过 mmap 视图完成。
/// </summary>
internal sealed class PageManager : IDisposable
{
    private readonly MemoryMappedFile _mmf;
    private MemoryMappedViewAccessor _accessor;
    private readonly uint _pageSize;
    private FileHeader _header;

    /// <summary>读取指定页的头部信息</summary>
    internal PageHeader ReadPageHeader(ulong pageId);

    /// <summary>将整页数据读入 destination（长度 = PageSize - 页头大小）</summary>
    internal int ReadPageData(ulong pageId, Span<byte> destination);

    /// <summary>将数据写入指定页（仅操作内存映射，需配合WAL使用）</summary>
    internal void WritePageData(ulong pageId, ReadOnlySpan<byte> source);

    /// <summary>
    /// 分配一个新页。优先从空闲链表取，链表为空时扩展文件。
    /// </summary>
    internal ulong AllocatePage(PageType type);

    /// <summary>将页标记为 Free 并加入空闲链表头部</summary>
    internal void FreePage(ulong pageId);

    /// <summary>
    /// 扩展文件大小。按当前大小的25%增长，最小增长1MB。
    /// 扩展后需调用 RemapFile() 重建 mmap 映射。
    /// </summary>
    private void GrowFile();

    /// <summary>关闭并重新创建 MemoryMappedFile 和 ViewAccessor</summary>
    private void RemapFile();

    public void Dispose();
}
```

### 4.1 空闲页链表

空闲页复用 `NextPageId` 字段串联为单向链表，链表头记录在 `FileHeader.FreePageListHead`。

- **分配**：取链表头页，更新 `FreePageListHead` 为该页的 `NextPageId`。
- **释放**：将被释放页的 `NextPageId` 设为当前 `FreePageListHead`，再将 `FreePageListHead` 指向被释放页。

### 4.2 文件增长策略

| 条件 | 增长量 |
|------|--------|
| 空闲页耗尽 | max(当前文件大小 × 25%, 1 MB) |
| 初始文件大小 | 1 MB (128 页 @ 8 KB) |

增长后必须调用 `RemapFile()` 重新创建 `MemoryMappedFile`，因为 .NET 的 `MemoryMappedFile` 不支持在不关闭映射的前提下扩展。

## 5. WAL (预写日志) 设计

### 5.1 WAL 文件格式

WAL 文件为顺序追加的日志记录流，每条记录格式如下：

```text
偏移    大小          字段
──────────────────────────────────────────────
0x00    4             RecordLength (uint, 含本字段在内的总长度)
0x04    8             TransactionId (ulong)
0x0C    8             SequenceNumber (ulong, 全局递增)
0x14    1             OperationType (byte)
0x15    8             TargetPageId (ulong)
0x1D    4             DataLength (uint)
0x21    DataLength    Data (原始页数据)
...     4             Checksum (CRC32, 覆盖 0x00 到 Data 末尾)
```

### 5.2 操作类型

```csharp
internal enum WalOperationType : byte
{
    PageWrite   = 0x01,  // 整页写入
    PageAlloc   = 0x02,  // 页分配（Data 为空，仅记录新页ID和类型）
    PageFree    = 0x03,  // 页释放
    TxBegin     = 0x10,  // 事务开始
    TxCommit    = 0x11,  // 事务提交
    TxRollback  = 0x12,  // 事务回滚
    Checkpoint  = 0x20   // 检查点标记
}
```

### 5.3 Wal 类设计

```csharp
namespace Mugu.AI.VectorLite.Storage;

/// <summary>
/// 预写日志：保证写操作的原子性和崩溃恢复能力。
/// WAL文件路径 = 数据库文件路径 + "-wal" 后缀。
/// </summary>
internal sealed class Wal : IDisposable
{
    private readonly FileStream _walStream;
    private readonly ILogger<Wal> _logger;
    private ulong _nextSequenceNumber;
    private ulong _nextTransactionId;

    /// <summary>开始新事务，返回事务ID</summary>
    internal ulong BeginTransaction();

    /// <summary>追加页写入日志</summary>
    internal void LogPageWrite(ulong txId, ulong pageId, ReadOnlySpan<byte> pageData);

    /// <summary>追加页分配日志</summary>
    internal void LogPageAlloc(ulong txId, ulong pageId, PageType type);

    /// <summary>追加页释放日志</summary>
    internal void LogPageFree(ulong txId, ulong pageId);

    /// <summary>提交事务（写入TxCommit记录并 Flush）</summary>
    internal void Commit(ulong txId);

    /// <summary>回滚事务（写入TxRollback记录）</summary>
    internal void Rollback(ulong txId);

    /// <summary>
    /// 检查点操作：将WAL中已提交事务的页写入刷入主文件，截断WAL。
    /// </summary>
    internal void Checkpoint(PageManager pageManager);

    /// <summary>
    /// 崩溃恢复：读取WAL，重放所有已提交但未检查点的事务。
    /// 回滚所有未提交事务。在数据库打开时调用。
    /// </summary>
    internal void Replay(PageManager pageManager);

    public void Dispose();
}
```

### 5.4 检查点算法

```text
CHECKPOINT(wal, pageManager):
    1. 从WAL文件头开始扫描所有记录
    2. 收集所有 TxCommit 对应的已提交事务ID集合 committedTxIds
    3. 再次扫描，将属于 committedTxIds 的 PageWrite 操作
       通过 pageManager.WritePageData() 刷入主文件
    4. 调用 pageManager.Flush() 确保数据落盘
    5. 更新 FileHeader.LastCheckpoint
    6. 截断 WAL 文件为零长度
    7. 写入 Checkpoint 标记作为新 WAL 的首条记录
```

### 5.5 崩溃恢复算法

```text
REPLAY(wal, pageManager):
    1. 若 WAL 文件不存在或为空，直接返回
    2. 扫描 WAL，收集每个事务的状态：
       - 遇到 TxBegin → 标记为 pending
       - 遇到 TxCommit → 标记为 committed
       - 遇到 TxRollback → 标记为 rolledback
    3. 对 committed 事务：按 SequenceNumber 升序重放其 PageWrite
    4. 对 pending 事务（未提交也未回滚）：丢弃，不重放
    5. 重放完成后执行 Checkpoint 清理 WAL
```

## 6. FileStorage 设计

```csharp
namespace Mugu.AI.VectorLite.Storage;

/// <summary>
/// 存储层门面：整合 PageManager 和 WAL，提供完整的存储生命周期管理。
/// </summary>
internal sealed class FileStorage : IDisposable
{
    private readonly string _filePath;
    private readonly VectorLiteOptions _options;

    internal PageManager PageManager { get; private set; }
    internal Wal Wal { get; private set; }
    internal FileHeader Header => PageManager.Header;

    public FileStorage(string filePath, VectorLiteOptions options);

    /// <summary>
    /// 打开数据库文件。若文件不存在则创建并初始化文件头。
    /// 打开后自动调用 WAL.Replay() 进行崩溃恢复。
    /// </summary>
    internal void Open();

    /// <summary>执行检查点</summary>
    internal void Checkpoint();

    /// <summary>关闭文件，释放 mmap 和 WAL 资源</summary>
    public void Dispose();
}
```

### 6.1 打开流程

```text
OPEN(filePath):
    若文件不存在:
        1. 创建文件，预分配 1MB
        2. 写入 FileHeader 到页0
        3. 分配页1作为 CollectionMeta 根页，写入空集合列表
        4. 创建 mmap 映射
        5. 创建空 WAL 文件
    若文件已存在:
        1. 读取并校验 FileHeader（魔数、版本、CRC32）
        2. 创建 mmap 映射
        3. 打开 WAL 文件
        4. 调用 Wal.Replay(pageManager) 进行崩溃恢复
```

## 7. 线程安全

存储层使用 `ReaderWriterLockSlim` 实现**单写多读 (SWMR)** 模型：

| 操作 | 锁类型 | 说明 |
|------|--------|------|
| ReadPageHeader / ReadPageData | 读锁 | 多个读操作可并发 |
| WritePageData / AllocatePage / FreePage | 写锁 | WAL追加 + mmap写入串行执行 |
| Checkpoint | 写锁 | 阻塞所有读写，完成后释放 |
| Replay | 独占 | 仅在 Open() 期间调用，此时无并发 |

上层（核心引擎层）负责获取锁后调用存储层方法，存储层自身不主动获取锁。
