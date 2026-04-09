# API层详细设计

> 父文档：[详细设计索引](index.md)

## 1. 设计目标

- 提供简洁的 Fluent API，使开发者能以最少代码完成向量的增删改查。
- 通过独立项目实现 Semantic Kernel `IMemoryStore` 适配，保持主库零外部依赖。
- 所有公开 API 均为 `async`，支持 `CancellationToken`。

## 2. 配置模型

```csharp
namespace Mugu.AI.VectorLite;

/// <summary>数据库全局配置，在 VectorLiteDB 构造时传入，运行时不可变。</summary>
public sealed class VectorLiteOptions
{
    /// <summary>页大小（字节），必须为 4096 的整数倍，默认 8192</summary>
    public uint PageSize { get; init; } = 8192;

    /// <summary>最大向量维度，默认 4096</summary>
    public uint MaxDimensions { get; init; } = 4096;

    // ── HNSW 参数 ──

    /// <summary>每节点最大邻居数（非零层），默认 16</summary>
    public int HnswM { get; init; } = 16;

    /// <summary>构建时动态候选列表大小，默认 200</summary>
    public int HnswEfConstruction { get; init; } = 200;

    /// <summary>搜索时动态候选列表大小（默认值，可被单次查询覆盖），默认 50</summary>
    public int HnswEfSearch { get; init; } = 50;

    /// <summary>默认距离度量方式，默认余弦</summary>
    public DistanceMetric DefaultDistanceMetric { get; init; } = DistanceMetric.Cosine;

    // ── 检查点 ──

    /// <summary>自动检查点间隔，默认 5 分钟。设为 Timeout.InfiniteTimeSpan 禁用自动检查点</summary>
    public TimeSpan CheckpointInterval { get; init; } = TimeSpan.FromMinutes(5);

    // ── 日志 ──

    /// <summary>日志工厂，为 null 时不输出日志</summary>
    public ILoggerFactory? LoggerFactory { get; init; }
}
```

## 3. 核心数据模型

### 3.1 VectorRecord

```csharp
namespace Mugu.AI.VectorLite;

/// <summary>一条向量记录，包含向量数据、元数据和可选文本。</summary>
public sealed class VectorRecord
{
    /// <summary>
    /// 记录ID。插入时为 0（由数据库自动分配），
    /// 读取和 Upsert 时由调用方指定。
    /// </summary>
    public ulong Id { get; set; }

    /// <summary>向量数据</summary>
    public ReadOnlyMemory<float> Vector { get; set; }

    /// <summary>
    /// 结构化元数据，用于标量索引过滤。
    /// 值类型仅支持 string / long / double / bool。
    /// </summary>
    public Dictionary<string, object>? Metadata { get; set; }

    /// <summary>可选的原始文本（用于展示，不参与索引）</summary>
    public string? Text { get; set; }
}
```

### 3.2 SearchResult

```csharp
namespace Mugu.AI.VectorLite;

/// <summary>搜索结果条目</summary>
public sealed class SearchResult
{
    /// <summary>匹配到的记录</summary>
    public VectorRecord Record { get; init; } = null!;

    /// <summary>
    /// 相似度分数，范围 [0, 1]（Cosine/Euclidean）或无上界（DotProduct）。
    /// 值越大越相似。
    /// </summary>
    public float Score { get; init; }
}
```

## 4. VectorLiteDB（数据库入口）

```csharp
namespace Mugu.AI.VectorLite;

/// <summary>
/// VectorLite 嵌入式向量数据库入口。
/// 每个实例独占一个 .vldb 文件，不支持多进程并发访问同一文件。
/// </summary>
public sealed class VectorLiteDB : IDisposable, IAsyncDisposable
{
    private readonly FileStorage _storage;
    private readonly ReaderWriterLockSlim _lock;
    private readonly Timer? _checkpointTimer;
    private readonly ConcurrentDictionary<string, Collection> _collections;
    private readonly ILogger<VectorLiteDB> _logger;

    /// <summary>
    /// 打开或创建数据库。
    /// </summary>
    /// <param name="filePath">.vldb 文件路径，不存在时自动创建</param>
    /// <param name="options">配置项，null 使用默认值</param>
    public VectorLiteDB(string filePath, VectorLiteOptions? options = null);

    /// <summary>获取已有集合，不存在则抛出 CollectionNotFoundException</summary>
    public ICollection GetCollection(string name);

    /// <summary>获取或创建集合</summary>
    public ICollection GetOrCreateCollection(string name, int dimensions);

    /// <summary>列出所有集合名</summary>
    public Task<IReadOnlyList<string>> ListCollectionsAsync(
        CancellationToken ct = default);

    /// <summary>删除集合及其全部数据</summary>
    public Task DeleteCollectionAsync(string name,
        CancellationToken ct = default);

    /// <summary>手动触发检查点（将WAL刷入主文件）</summary>
    public void Checkpoint();

    public void Dispose();
    public ValueTask DisposeAsync();
}
```

### 4.1 生命周期

```text
var db = new VectorLiteDB("path.vldb", options);
   │
   ├── FileStorage.Open()          // 创建或打开文件, mmap映射, WAL恢复
   ├── 加载所有集合元数据到内存
   ├── 对每个集合: 反序列化 HNSWIndex + ScalarIndex
   ├── 启动自动检查点定时器
   │
   │   ... 使用数据库 ...
   │
   db.Dispose()
   │
   ├── 停止检查点定时器
   ├── 执行最终检查点
   ├── 释放所有集合的内存索引
   └── FileStorage.Dispose()       // 关闭mmap, 关闭WAL
```

## 5. ICollection 接口与 Collection 实现

### 5.1 接口定义

```csharp
namespace Mugu.AI.VectorLite;

/// <summary>向量集合操作接口</summary>
public interface ICollection
{
    /// <summary>集合名称</summary>
    string Name { get; }

    /// <summary>向量维度（创建时指定，不可变）</summary>
    int Dimensions { get; }

    /// <summary>插入记录，返回自动分配的ID</summary>
    Task<ulong> InsertAsync(VectorRecord record,
        CancellationToken ct = default);

    /// <summary>插入或更新记录（按 record.Id 匹配）</summary>
    Task UpsertAsync(VectorRecord record,
        CancellationToken ct = default);

    /// <summary>按ID删除记录</summary>
    Task DeleteAsync(ulong recordId,
        CancellationToken ct = default);

    /// <summary>按ID获取记录</summary>
    Task<VectorRecord?> GetAsync(ulong recordId,
        CancellationToken ct = default);

    /// <summary>获取集合中的记录总数</summary>
    Task<long> CountAsync(CancellationToken ct = default);

    /// <summary>发起向量相似度查询</summary>
    IQueryBuilder Query(ReadOnlyMemory<float> vector);
}
```

### 5.2 内部实现要点

```csharp
internal sealed class Collection : ICollection
{
    private readonly FileStorage _storage;
    private readonly HNSWIndex _hnswIndex;
    private readonly ScalarIndex _scalarIndex;
    private readonly QueryEngine _queryEngine;
    private readonly ReaderWriterLockSlim _lock;  // 来自 VectorLiteDB
    private readonly MemoryManager _memoryManager;

    // InsertAsync 流程:
    // 1. 校验 record.Vector.Length == Dimensions
    // 2. 获取写锁
    // 3. WAL.BeginTransaction()
    // 4. 分配 RecordId, 序列化记录写入 VectorData 页
    // 5. HNSWIndex.Insert(recordId, vector)
    // 6. ScalarIndex.Add(recordId, metadata)
    // 7. 更新集合元数据（RecordCount, NextRecordId）
    // 8. WAL.Commit()
    // 9. 释放写锁
}
```

## 6. Fluent 查询构建器

### 6.1 IQueryBuilder 接口

```csharp
namespace Mugu.AI.VectorLite;

/// <summary>链式查询构建器</summary>
public interface IQueryBuilder
{
    /// <summary>添加过滤条件（FilterExpression）</summary>
    IQueryBuilder Where(FilterExpression filter);

    /// <summary>快捷方式：精确匹配单个字段</summary>
    IQueryBuilder Where(string field, object value);

    /// <summary>覆盖本次查询使用的距离度量方式</summary>
    IQueryBuilder WithDistance(DistanceMetric metric);

    /// <summary>设置最低相似度阈值，低于此值的结果将被过滤</summary>
    IQueryBuilder WithMinScore(float minScore);

    /// <summary>覆盖本次查询的 efSearch 参数</summary>
    IQueryBuilder WithEfSearch(int efSearch);

    /// <summary>返回前 K 个最相似的结果，默认 10</summary>
    IQueryBuilder TopK(int k);

    /// <summary>执行查询并返回结果列表</summary>
    Task<IReadOnlyList<SearchResult>> ToListAsync(
        CancellationToken ct = default);
}
```

### 6.2 内部实现

```csharp
internal sealed class QueryBuilder : IQueryBuilder
{
    private readonly Collection _collection;
    private readonly ReadOnlyMemory<float> _queryVector;

    private FilterExpression? _filter;
    private DistanceMetric? _distanceOverride;
    private float _minScore;
    private int? _efSearchOverride;
    private int _topK = 10;

    internal QueryBuilder(Collection collection, ReadOnlyMemory<float> queryVector);

    public IQueryBuilder Where(FilterExpression filter)
    {
        // 若已有 filter 则组合为 AndFilter
        _filter = _filter is null ? filter : new AndFilter([_filter, filter]);
        return this;
    }

    public IQueryBuilder Where(string field, object value)
        => Where(new EqualFilter(field, value));

    // 其他方法：设置对应字段后返回 this

    public Task<IReadOnlyList<SearchResult>> ToListAsync(CancellationToken ct)
    {
        // 1. 获取读锁
        // 2. 调用 QueryEngine.Search(...)
        // 3. 释放读锁
        // 4. 返回结果
    }
}
```

### 6.3 使用示例

```csharp
using var db = new VectorLiteDB("my_memory.vldb");
var collection = db.GetOrCreateCollection("notes", dimensions: 1536);

// 插入
var record = new VectorRecord
{
    Vector = embeddingVector,
    Metadata = new() { ["source"] = "email", ["importance"] = 5L },
    Text = "明天下午三点开会讨论Q2规划"
};
ulong id = await collection.InsertAsync(record);

// 查询
var results = await collection
    .Query(queryVector)
    .Where("source", "email")
    .Where(new RangeFilter("importance", lowerBound: 3L, upperBound: null,
        lowerInclusive: true, upperInclusive: false))
    .WithMinScore(0.7f)
    .TopK(5)
    .ToListAsync();

foreach (var r in results)
    Console.WriteLine($"[{r.Score:F3}] {r.Record.Text}");
```

## 7. Semantic Kernel 集成

### 7.1 项目隔离策略

`Mugu.AI.VectorLite.SemanticKernel` 作为**独立 NuGet 包/项目**，依赖：

- `Mugu.AI.VectorLite` (主库)
- `Microsoft.SemanticKernel.Abstractions` (仅抽象层)

主库 `Mugu.AI.VectorLite` **不依赖** Semantic Kernel 的任何包。

### 7.2 VectorLiteMemoryStore

```csharp
namespace Mugu.AI.VectorLite.SemanticKernel;

/// <summary>
/// 将 VectorLiteDB 适配为 Semantic Kernel 的 IMemoryStore。
/// </summary>
public sealed class VectorLiteMemoryStore : IMemoryStore, IDisposable
{
    private readonly VectorLiteDB _db;
    private readonly bool _ownsDb;  // 若为 true 则 Dispose 时同时释放 _db

    /// <summary>创建新实例，内部创建并管理 VectorLiteDB</summary>
    public VectorLiteMemoryStore(string filePath, VectorLiteOptions? options = null);

    /// <summary>包装已有的 VectorLiteDB 实例（不负责释放）</summary>
    public VectorLiteMemoryStore(VectorLiteDB db);

    // ── IMemoryStore 集合操作 ──

    public Task CreateCollectionAsync(string collectionName,
        CancellationToken ct = default);

    public IAsyncEnumerable<string> GetCollectionsAsync(
        CancellationToken ct = default);

    public Task<bool> DoesCollectionExistAsync(string collectionName,
        CancellationToken ct = default);

    public Task DeleteCollectionAsync(string collectionName,
        CancellationToken ct = default);

    // ── IMemoryStore 记录操作 ──

    public Task<string> UpsertAsync(string collectionName,
        MemoryRecord record, CancellationToken ct = default);

    public IAsyncEnumerable<string> UpsertBatchAsync(string collectionName,
        IEnumerable<MemoryRecord> records, CancellationToken ct = default);

    public Task<MemoryRecord?> GetAsync(string collectionName, string key,
        bool withEmbedding = false, CancellationToken ct = default);

    public IAsyncEnumerable<MemoryRecord> GetBatchAsync(string collectionName,
        IEnumerable<string> keys, bool withEmbedding = false,
        CancellationToken ct = default);

    // ── IMemoryStore 搜索操作 ──

    public Task<(MemoryRecord, double)?> GetNearestMatchAsync(
        string collectionName, ReadOnlyMemory<float> embedding,
        double minRelevanceScore = 0, bool withEmbedding = false,
        CancellationToken ct = default);

    public IAsyncEnumerable<(MemoryRecord, double)> GetNearestMatchesAsync(
        string collectionName, ReadOnlyMemory<float> embedding, int limit,
        double minRelevanceScore = 0, bool withEmbedding = false,
        CancellationToken ct = default);

    // ── IMemoryStore 删除操作 ──

    public Task RemoveAsync(string collectionName, string key,
        CancellationToken ct = default);

    public Task RemoveBatchAsync(string collectionName,
        IEnumerable<string> keys, CancellationToken ct = default);

    public void Dispose();
}
```

### 7.3 MemoryRecord ↔ VectorRecord 映射

```csharp
namespace Mugu.AI.VectorLite.SemanticKernel;

/// <summary>
/// Semantic Kernel 的 MemoryRecord 与 VectorLite 的 VectorRecord 之间的双向映射。
/// </summary>
internal static class MemoryRecordMapper
{
    /// <summary>
    /// 将 MemoryRecord 转换为 VectorRecord。
    /// Key → Metadata["_sk_key"]（string）
    /// ExternalSourceName → Metadata["_sk_source"]
    /// Description → Text
    /// AdditionalMetadata → Metadata["_sk_additional"]（JSON string）
    /// </summary>
    internal static VectorRecord ToVectorRecord(MemoryRecord memoryRecord);

    /// <summary>
    /// 将 VectorRecord 还原为 MemoryRecord。
    /// </summary>
    internal static MemoryRecord ToMemoryRecord(VectorRecord vectorRecord,
        bool withEmbedding);
}
```

**SK Key 到数值 ID 的映射**：Semantic Kernel 使用 `string` 类型的 Key，而 VectorLite 使用 `ulong` 类型的 RecordId。通过在元数据中存储 `_sk_key` 字段，并在标量索引中建立 `_sk_key → RecordId` 的映射来桥接。`UpsertAsync` 时先查询 `_sk_key` 是否已存在，存在则更新，不存在则插入。

## 8. 异常体系

```text
VectorLiteException (所有异常的基类)
│
├── StorageException (存储层异常)
│   ├── CorruptedFileException     // 文件头校验失败、页面CRC不匹配
│   ├── WalCorruptedException      // WAL记录损坏、无法完成恢复
│   └── DiskFullException          // 文件扩展失败（磁盘空间不足）
│
├── IndexException (索引相关异常)
│   ├── DimensionMismatchException // 插入/查询向量维度与集合不匹配
│   └── IndexCorruptedException    // 索引反序列化失败
│
└── CollectionException (集合操作异常)
    ├── CollectionNotFoundException    // GetCollection 时集合不存在
    ├── CollectionAlreadyExistsException // 创建同名集合
    └── DuplicateRecordException       // Upsert 时ID冲突（内部错误）
```

所有异常：
- 继承自 `VectorLiteException`，后者继承 `Exception`。
- 包含 `string ErrorCode` 属性（如 `"VLDB_STORAGE_CORRUPT"`），便于程序化处理。
- 构造时通过 `ILogger` 记录 Error 级别日志。

## 9. 并发模型

### 9.1 锁策略

整个数据库使用**一把 `ReaderWriterLockSlim`**：

| 操作类别 | 锁类型 | 说明 |
|----------|--------|------|
| `Query` / `Get` / `Count` / `ListCollections` | 读锁 | 多查询可并发执行 |
| `Insert` / `Upsert` / `Delete` / `DeleteCollection` | 写锁 | 串行写入，阻塞所有读 |
| `Checkpoint` | 写锁 | 阻塞所有读写直到完成 |

### 9.2 为什么选择数据库级锁而非更细粒度的锁

- **简单可靠**：嵌入式场景下写入频率通常较低，数据库级锁足够高效。
- **WAL 一致性**：WAL 追加和 mmap 写入需要与索引更新原子完成。
- **内存索引**：HNSW 图和标量索引为共享可变状态，细粒度锁增加复杂度但收益有限。

### 9.3 异步说明

公开 API 方法标记为 `async Task` / `async ValueTask`，但内部实现在当前版本为**同步执行后包装为 Task**（因 mmap 读写本质是内存操作，不涉及真正的异步IO）。保留 `async` 签名是为了：

1. API 面向未来兼容（后续可能引入真正异步IO路径）。
2. 与 Semantic Kernel 等框架的 `async` 接口保持一致。
3. 调用方可在 `await` 点释放线程。

## 10. 自动检查点

```csharp
// VectorLiteDB 构造函数中
if (options.CheckpointInterval != Timeout.InfiniteTimeSpan)
{
    _checkpointTimer = new Timer(
        _ => Checkpoint(),
        state: null,
        dueTime: options.CheckpointInterval,
        period: options.CheckpointInterval);
}
```

- 定时器在后台线程触发，`Checkpoint()` 内部获取写锁。
- `Dispose()` 时先停止定时器，再执行最终检查点。
- 异常情况下（如磁盘满），检查点失败记录 Error 日志但不抛出异常（下次重试）。
