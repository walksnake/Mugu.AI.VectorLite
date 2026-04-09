# 内部架构参考

> 本章面向深度开发者和 Code Agent，详细描述 VectorLite 的内部实现。
> 所有此处提到的类型均为 `internal`，通过 `InternalsVisibleTo` 对测试项目可见。

---

## 1. 分层架构

```
┌──────────────────────────────────────────────────┐
│  API 层 (public)                                 │
│  VectorLiteDB · Collection · QueryBuilder        │
├──────────────────────────────────────────────────┤
│  核心引擎层 (internal)                           │
│  HNSWIndex · ScalarIndex · QueryEngine           │
│  DistanceFunctions · MemoryManager               │
├──────────────────────────────────────────────────┤
│  存储层 (internal)                               │
│  FileStorage · PageManager · Wal                 │
│  FileHeader · PageHeader                         │
└──────────────────────────────────────────────────┘
```

---

## 2. 存储层

### 2.1 文件格式

单个 `.vldb` 文件 + 伴随的 `.vldb-wal` 日志文件。

**文件头 (`FileHeader`)** — 固定 64 字节：

| 偏移 | 长度 | 字段 | 说明 |
|------|------|------|------|
| 0 | 8 | Magic | `0x56_4C_49_54_45_44_42_01`（"VLITEDB\x01"） |
| 8 | 4 | Version | `uint`，当前为 1 |
| 12 | 4 | PageSize | `uint`，默认 8192 |
| 16 | 4 | PageCount | `uint`，当前总页数 |
| 20 | 4 | FreeListPageId | `uint`，空闲链表首页 |
| 24 | 4 | RootPageId | `uint`，根数据页 |
| 28 | 4 | MaxDimensions | `uint`，最大向量维度 |
| 32 | 28 | Reserved | 保留字节（全零） |
| 60 | 4 | Checksum | `uint`，CRC32C 校验 |

关键方法：
- `FileHeader.WriteTo(Span<byte>)` / `FileHeader.ReadFrom(ReadOnlySpan<byte>)`
- 校验使用 `System.IO.Hashing.Crc32C`

**数据页 (`PageHeader`)** — 每页首部 16 字节：

| 偏移 | 长度 | 字段 | 说明 |
|------|------|------|------|
| 0 | 4 | PageId | 页 ID |
| 4 | 1 | PageType | 枚举：Data/Index/Overflow/Free |
| 5 | 4 | NextPageId | 链表中下一页（溢出页或空闲链表） |
| 9 | 2 | ItemCount | 页内数据项数 |
| 11 | 2 | UsedBytes | 已使用字节数 |
| 13 | 3 | Reserved | 保留 |

### 2.2 PageManager（mmap 页管理）

```csharp
internal sealed class PageManager : IDisposable
{
    PageManager(string filePath, uint pageSize, uint maxDimensions,
                ILogger<PageManager>? logger);

    uint AllocatePage(PageType type);     // 从空闲链表或文件末尾分配
    void FreePage(uint pageId);           // 归还到空闲链表
    Span<byte> GetPage(uint pageId);      // 获取页的可写视图
    ReadOnlySpan<byte> GetPageReadOnly(uint pageId);
    void Flush();                         // 刷盘
    ref FileHeader Header { get; }        // 直接操作 mmap 中的文件头
}
```

**文件增长策略**：当前大小 < 1MB 时翻倍，否则增长 25%。

### 2.3 WAL（预写日志）

WAL 文件格式：每条记录为 `[Length:4][Type:1][Data:N][CRC32:4]`。

```csharp
internal sealed class Wal : IDisposable
{
    Wal(string walFilePath, ILogger<Wal>? logger);

    void Append(WalOperationType type, ReadOnlySpan<byte> data);
    void Commit();    // 写入 Commit 标记 + Flush
    void Rollback();  // 写入 Rollback 标记 + Flush
    void Checkpoint(PageManager pageManager);  // 重放已提交操作到数据页
    void Replay(Action<WalOperationType, ReadOnlyMemory<byte>> handler);
}
```

`WalOperationType` 枚举：`PageWrite | Insert | Delete | Commit | Rollback | Checkpoint`

### 2.4 FileStorage（门面）

```csharp
internal sealed class FileStorage : IDisposable
{
    FileStorage(string filePath, VectorLiteOptions options, ILoggerFactory? loggerFactory);

    PageManager PageManager { get; }
    Wal Wal { get; }
    void Checkpoint();
}
```

---

## 3. HNSW 索引

### 3.1 核心结构

```csharp
internal sealed class HNSWNode
{
    ulong Id;
    float[] Vector;
    int MaxLayer;
    List<ulong>[] Neighbors;     // 每层一个邻居列表
    bool IsDeleted;              // 惰性删除标记
}

internal sealed class HNSWGraph
{
    ConcurrentDictionary<ulong, HNSWNode> Nodes;
    ulong EntryPointId;
    int MaxLayer;
}

internal sealed class HNSWIndex
{
    HNSWIndex(int dimensions, int M, int efConstruction, int efSearch,
              IDistanceFunction distanceFunction);

    void Insert(ulong id, float[] vector);
    List<(ulong Id, float Distance)> Search(float[] query, int k,
        int efSearch, HashSet<ulong>? candidateIds);
    void Delete(ulong id);       // 惰性删除
    byte[] Serialize();
    static HNSWIndex Deserialize(byte[] data, IDistanceFunction distanceFunction);
}
```

### 3.2 关键参数

| 参数 | 默认值 | 说明 |
|------|--------|------|
| `M` | 16 | 非零层每节点最大邻居数 |
| `Mmax0` | 2 × M = 32 | 零层最大邻居数 |
| `efConstruction` | 200 | 构建时动态候选列表大小 |
| `efSearch` | 50 | 搜索时动态候选列表大小 |
| `mL` | 1 / ln(M) | 层级概率分布参数 |

### 3.3 并发安全设计

HNSW 的邻居列表在并发读写场景下需要保护：

```csharp
// 写入时加锁
lock (node.Neighbors[layer])
{
    node.Neighbors[layer].Add(neighborId);
}

// 读取时快照
ulong[] snapshot;
lock (node.Neighbors[layer])
{
    snapshot = node.Neighbors[layer].ToArray();
}
// 在快照上遍历（无需持锁）
```

### 3.4 惰性删除与压缩

- `Delete(id)` 仅设置 `IsDeleted = true`
- 搜索时跳过已删除节点
- 当已删除节点占比超过 20% 时，下次操作触发**压缩**（compact）
- 压缩 = 收集所有活跃节点 → 重建新索引

---

## 4. 标量索引（倒排索引）

```csharp
internal sealed class ScalarIndex
{
    void Index(ulong id, Dictionary<string, object>? metadata);
    void Remove(ulong id, Dictionary<string, object>? metadata);
    HashSet<ulong>? Search(FilterExpression filter);  // null = 无过滤
}
```

内部结构：`Dictionary<string, Dictionary<object, HashSet<ulong>>>`

即 `field → value → {recordId1, recordId2, ...}`。

---

## 5. SIMD 距离计算

### 5.1 加速策略

距离函数在运行时自动选择最优 SIMD 指令集：

```
AVX-512（512-bit，16 个 float）
  ↓ 不支持时降级
AVX2（256-bit，8 个 float）
  ↓ 不支持时降级
Vector<T>（.NET 通用 SIMD，宽度取决于硬件）
```

### 5.2 各距离函数实现

| 距离函数 | 公式 | 返回值 | 范围 |
|----------|------|--------|------|
| Cosine | `1 - (a·b) / (‖a‖·‖b‖)` | 余弦距离 | [0, 2] |
| Euclidean | `√Σ(aᵢ-bᵢ)²` | 欧几里得距离 | [0, +∞) |
| DotProduct | `-Σ(aᵢ·bᵢ)` | 负点积 | (-∞, +∞) |

### 5.3 工厂方法

```csharp
internal static class DistanceFunctionFactory
{
    static IDistanceFunction Create(DistanceMetric metric);
    //  Cosine     → CosineDistance 实例
    //  Euclidean  → EuclideanDistance 实例
    //  DotProduct → DotProductDistance 实例
}
```

---

## 6. 查询引擎

```csharp
internal sealed class QueryEngine
{
    QueryEngine(HNSWIndex hnswIndex, ScalarIndex scalarIndex,
                Dictionary<ulong, VectorRecord> records);

    List<SearchResult> Search(float[] queryVector, FilterExpression? filter,
                              int topK, int efSearch, float minScore);
}
```

执行流程：

1. 若有过滤器 → `ScalarIndex.Search(filter)` 获取候选 ID 集合
2. `HNSWIndex.Search(query, k, efSearch, candidateIds)` 向量搜索
3. 补充完整的 `VectorRecord` 数据
4. 按 Distance 升序排序
5. 计算 Score = 1 - Distance，过滤 < minScore 的结果
6. 返回 `List<SearchResult>`

---

## 7. 内存管理器

```csharp
internal sealed class MemoryManager
{
    float[] RentVector(int dimensions);     // 从对象池租借
    void ReturnVector(float[] vector);      // 归还到对象池
    void Clear();                           // 清空对象池
}
```

使用 `ArrayPool<float>.Shared`，减少高频向量分配的 GC 压力。

---

## 8. Collection 内部实现

```csharp
// 简化伪代码
internal sealed class Collection : ICollection
{
    HNSWIndex _hnswIndex;
    ScalarIndex _scalarIndex;
    QueryEngine _queryEngine;
    Dictionary<ulong, VectorRecord> _records;
    ulong _nextId = 1;

    Task<ulong> InsertAsync(VectorRecord record, CancellationToken ct)
    {
        var id = Interlocked.Increment(ref _nextId) - 1;
        record.Id = id;
        _records[id] = record;
        _hnswIndex.Insert(id, record.Vector);
        _scalarIndex.Index(id, record.Metadata);
        return Task.FromResult(id);
    }
}
```

---

## 9. Code Agent 使用指南

### 9.1 修改内部类型时

1. 内部类型在 `src/Mugu.AI.VectorLite/` 下
2. 通过 `InternalsVisibleTo` 对以下项目可见：
   - `Mugu.AI.VectorLite.Tests`
   - `Mugu.AI.VectorLite.QualityGate`
   - `Mugu.AI.VectorLite.SemanticKernel`
3. 修改后运行 `dotnet build` 和 `dotnet test` 确保不破坏现有功能

### 9.2 添加新的距离函数

1. 在 `Engine/Distance/` 下创建新类，实现 `IDistanceFunction`
2. 在 `DistanceMetric` 枚举中添加新值
3. 在 `DistanceFunctionFactory.Create` 中添加 case
4. 在 `tests/.../Benchmarks/DistanceBenchmark.cs` 中添加对应基准测试

### 9.3 添加新的过滤器类型

1. 在 `Engine/FilterExpression.cs` 中添加新的 `sealed class`（继承 `FilterExpression`）
2. 在 `ScalarIndex.Search` 中添加对应的处理分支
3. 在 `tests/.../Baselines/ScalarFilterBaseline.cs` 中添加测试
