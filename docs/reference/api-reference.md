# 公共 API 参考

> 本章列出所有 `public` 类型的完整签名。
> 命名空间：`Mugu.AI.VectorLite`（主库）/ `Mugu.AI.VectorLite.Engine`（过滤器）。

---

## VectorLiteDB — 数据库入口

```csharp
namespace Mugu.AI.VectorLite;

public sealed class VectorLiteDB : IDisposable
{
    /// <summary>数据库文件路径</summary>
    public string FilePath { get; }

    /// <summary>创建或打开数据库</summary>
    /// <param name="filePath">.vldb 文件路径（不存在时自动创建）</param>
    /// <param name="options">可选配置（为 null 时使用全部默认值）</param>
    public VectorLiteDB(string filePath, VectorLiteOptions? options = null);

    /// <summary>获取或创建集合</summary>
    /// <exception cref="CollectionException">名称已存在但维度不匹配时抛出</exception>
    public ICollection GetOrCreateCollection(string name, int dimensions);

    /// <summary>获取已有集合（不存在时返回 null）</summary>
    public ICollection? GetCollection(string name);

    /// <summary>列出所有集合名称</summary>
    public IReadOnlyList<string> GetCollectionNames();

    /// <summary>检查集合是否存在</summary>
    public bool CollectionExists(string name);

    /// <summary>删除集合</summary>
    /// <returns>true=删除成功，false=不存在</returns>
    public bool DeleteCollection(string name);

    /// <summary>手动触发检查点（WAL → 主文件合并）</summary>
    public void Checkpoint();

    /// <summary>释放资源（自动执行最终检查点）</summary>
    public void Dispose();
}
```

### 关键行为说明

| 行为 | 说明 |
|------|------|
| 并发控制 | 内部使用 `ReaderWriterLockSlim`，读操作并行，写操作互斥 |
| 自动检查点 | 默认每 5 分钟执行一次，通过 `VectorLiteOptions.CheckpointInterval` 配置 |
| Dispose 语义 | 关闭前自动执行最终检查点（尽力而为），然后释放 mmap 和文件句柄 |

---

## VectorLiteOptions — 数据库配置

```csharp
namespace Mugu.AI.VectorLite;

public sealed class VectorLiteOptions
{
    // ── 存储 ──
    public uint PageSize { get; init; } = 8192;          // 必须为 4096 的整数倍
    public uint MaxDimensions { get; init; } = 4096;

    // ── HNSW 参数 ──
    public int HnswM { get; init; } = 16;                // 每节点最大邻居数（非零层）
    public int HnswEfConstruction { get; init; } = 200;  // 构建时动态候选列表大小
    public int HnswEfSearch { get; init; } = 50;         // 搜索默认 ef（可被查询级覆盖）

    // ── 距离度量 ──
    public DistanceMetric DefaultDistanceMetric { get; init; } = DistanceMetric.Cosine;

    // ── 检查点 ──
    public TimeSpan CheckpointInterval { get; init; } = TimeSpan.FromMinutes(5);
    //   设为 Timeout.InfiniteTimeSpan 可禁用自动检查点

    // ── 日志 ──
    public ILoggerFactory? LoggerFactory { get; init; }   // null = 不输出日志
}
```

### 参数调优指南

| 参数 | 增大效果 | 减小效果 | 建议 |
|------|----------|----------|------|
| `HnswM` | 召回率↑ 插入速度↓ 内存↑ | 召回率↓ 插入速度↑ 内存↓ | 通用场景 16 足够 |
| `HnswEfConstruction` | 索引质量↑ 构建时间↑ | 索引质量↓ 构建时间↓ | 100~300，一次构建多次查询场景用较大值 |
| `HnswEfSearch` | 召回率↑ 查询延迟↑ | 召回率↓ 查询延迟↓ | 默认 50，精度要求高可设 100~200 |

---

## ICollection — 集合接口

```csharp
namespace Mugu.AI.VectorLite;

public interface ICollection
{
    /// <summary>集合名称</summary>
    string Name { get; }

    /// <summary>向量维度（创建时指定，不可变）</summary>
    int Dimensions { get; }

    /// <summary>当前记录数（不含已删除）</summary>
    int Count { get; }

    /// <summary>插入单条记录，返回分配的 ID</summary>
    /// <exception cref="DimensionMismatchException">向量维度不匹配时抛出</exception>
    Task<ulong> InsertAsync(VectorRecord record, CancellationToken ct = default);

    /// <summary>批量插入，返回所有分配的 ID</summary>
    Task<IReadOnlyList<ulong>> InsertBatchAsync(
        IEnumerable<VectorRecord> records, CancellationToken ct = default);

    /// <summary>按 ID 查询（不存在返回 null）</summary>
    Task<VectorRecord?> GetAsync(ulong id, CancellationToken ct = default);

    /// <summary>按 ID 删除（不存在返回 false）</summary>
    Task<bool> DeleteAsync(ulong id, CancellationToken ct = default);

    /// <summary>创建查询构建器</summary>
    /// <exception cref="DimensionMismatchException">查询向量维度不匹配时抛出</exception>
    IQueryBuilder Query(float[] queryVector);
}
```

### 注意事项

- **ID 由数据库分配**：`VectorRecord.Id` 在 `InsertAsync` 后自动赋值（从 1 递增），请勿手动设置。
- **删除为惰性删除**：HNSW 索引中先标记为已删除，删除节点超过 20% 时触发压缩。
- **线程安全**：Collection 内部使用 `lock` 保护写操作，支持并发读写。

---

## IQueryBuilder — 查询构建器

```csharp
namespace Mugu.AI.VectorLite;

public interface IQueryBuilder
{
    /// <summary>精确匹配过滤（可多次调用，自动 AND 组合）</summary>
    IQueryBuilder Where(string field, object value);

    /// <summary>自定义过滤表达式（可多次调用，自动 AND 组合）</summary>
    IQueryBuilder Where(FilterExpression filter);

    /// <summary>设置返回的最大结果数（默认 10）</summary>
    /// <exception cref="ArgumentOutOfRangeException">k &lt; 1 时抛出</exception>
    IQueryBuilder TopK(int k);

    /// <summary>设置最低相似度得分（Score = 1 - Distance，仅对余弦距离有意义）</summary>
    IQueryBuilder WithMinScore(float minScore);

    /// <summary>覆盖本次查询的 efSearch 参数</summary>
    /// <exception cref="ArgumentOutOfRangeException">efSearch &lt; 1 时抛出</exception>
    IQueryBuilder WithEfSearch(int efSearch);

    /// <summary>执行查询并返回结果列表（按距离升序）</summary>
    Task<IReadOnlyList<SearchResult>> ToListAsync(CancellationToken ct = default);
}
```

### 链式调用规则

```csharp
// 多个 .Where() 自动用 AND 组合
var results = await collection.Query(vec)
    .Where("category", "技术")                     // EqualFilter
    .Where(new RangeFilter("score", lowerBound: 80L))  // RangeFilter
    .TopK(10)
    .WithEfSearch(100)        // 本次查询用 ef=100（覆盖全局默认的 50）
    .WithMinScore(0.7f)       // 仅返回 Score >= 0.7 的结果
    .ToListAsync();
```

---

## VectorRecord — 向量记录

```csharp
namespace Mugu.AI.VectorLite;

public sealed class VectorRecord
{
    /// <summary>记录 ID（插入后由数据库分配，只读）</summary>
    public ulong Id { get; internal set; }

    /// <summary>向量数据（必填）</summary>
    public required float[] Vector { get; init; }

    /// <summary>元数据键值对，值支持 string / long / double / bool</summary>
    public Dictionary<string, object>? Metadata { get; init; }

    /// <summary>可选的原文内容</summary>
    public string? Text { get; init; }
}
```

### 元数据值类型约束

| 类型 | 支持的过滤方式 | 示例 |
|------|---------------|------|
| `string` | Equal / NotEqual / In | `["tag"] = "工作"` |
| `long` | Equal / NotEqual / In / Range | `["priority"] = 5L` |
| `double` | Equal / Range | `["score"] = 0.95` |
| `bool` | Equal / NotEqual | `["active"] = true` |

> ⚠️ Range 过滤要求值实现 `IComparable`。`string` 可用 Range 但按字典序比较。

---

## SearchResult — 搜索结果

```csharp
namespace Mugu.AI.VectorLite;

public sealed class SearchResult
{
    /// <summary>匹配的向量记录（含完整元数据和文本）</summary>
    public required VectorRecord Record { get; init; }

    /// <summary>与查询向量的距离（越小越相似）</summary>
    public float Distance { get; init; }

    /// <summary>相似度得分 = 1 - Distance（仅余弦距离有意义，范围 [-1, 1]）</summary>
    public float Score => 1f - Distance;
}
```

---

## DistanceMetric — 距离度量枚举

```csharp
namespace Mugu.AI.VectorLite.Engine.Distance;

public enum DistanceMetric : byte
{
    Cosine      = 0,  // 余弦距离 = 1 - cos(a,b)，范围 [0, 2]
    Euclidean   = 1,  // 欧几里得距离 = sqrt(Σ(a-b)²)，范围 [0, +∞)
    DotProduct  = 2,  // 负点积 = -dot(a,b)，归一化向量时等价于余弦距离
}
```

> 所有距离函数统一为"越小越相似"语义。`DotProduct` 内部返回 `-dot` 以保持一致。
