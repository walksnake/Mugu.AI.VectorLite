# Semantic Kernel 集成

> 通过 `VectorLiteMemoryStore` 将 VectorLite 无缝接入 Microsoft Semantic Kernel。
>
> NuGet 包：`Mugu.AI.VectorLite.SemanticKernel`
> 命名空间：`Mugu.AI.VectorLite.SemanticKernel`

---

## 快速集成

```csharp
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Memory;
using Mugu.AI.VectorLite;
using Mugu.AI.VectorLite.SemanticKernel;

// 1. 创建 VectorLite 数据库实例
using var db = new VectorLiteDB("sk_memory.vldb");

// 2. 创建 MemoryStore
var memoryStore = new VectorLiteMemoryStore(db);

// 3. 注入 Semantic Kernel
var memory = new MemoryBuilder()
    .WithMemoryStore(memoryStore)
    .WithTextEmbeddingGeneration(embeddingService) // 你的 Embedding 服务
    .Build();

// 4. 使用 SK Memory API
await memory.SaveInformationAsync("notes", "今天的会议讨论了…", "meeting-001");

var results = memory.SearchAsync("notes", "会议内容", limit: 5);
await foreach (var result in results)
{
    Console.WriteLine($"[{result.Relevance:F4}] {result.Metadata.Text}");
}
```

---

## VectorLiteMemoryStore API

```csharp
public sealed class VectorLiteMemoryStore : IMemoryStore, IDisposable
{
    /// <summary>构造函数</summary>
    /// <param name="db">VectorLiteDB 实例（调用者管理生命周期）</param>
    /// <param name="defaultDimensions">首次自动创建集合时使用的维度，默认 1536</param>
    public VectorLiteMemoryStore(VectorLiteDB db, int defaultDimensions = 1536);

    // ── 集合管理 ──
    Task CreateCollectionAsync(string collectionName, CancellationToken ct);
    Task DeleteCollectionAsync(string collectionName, CancellationToken ct);
    Task<bool> DoesCollectionExistAsync(string collectionName, CancellationToken ct);
    IAsyncEnumerable<string> GetCollectionsAsync(CancellationToken ct);

    // ── 记录操作 ──
    Task<string> UpsertAsync(string collectionName, MemoryRecord record, CancellationToken ct);
    IAsyncEnumerable<string> UpsertBatchAsync(string collectionName,
        IAsyncEnumerable<MemoryRecord> records, CancellationToken ct);
    Task<MemoryRecord?> GetAsync(string collectionName, string key,
        bool withEmbedding, CancellationToken ct);
    IAsyncEnumerable<MemoryRecord> GetBatchAsync(string collectionName,
        IEnumerable<string> keys, bool withEmbedding, CancellationToken ct);
    Task RemoveAsync(string collectionName, string key, CancellationToken ct);
    Task RemoveBatchAsync(string collectionName, IEnumerable<string> keys,
        CancellationToken ct);

    // ── 语义搜索 ──
    Task<(MemoryRecord, double)?> GetNearestMatchAsync(string collectionName,
        ReadOnlyMemory<float> embedding, double minRelevanceScore,
        bool withEmbedding, CancellationToken ct);
    IAsyncEnumerable<(MemoryRecord, double)> GetNearestMatchesAsync(
        string collectionName, ReadOnlyMemory<float> embedding, int limit,
        double minRelevanceScore, bool withEmbedding, CancellationToken ct);

    void Dispose();
}
```

---

## 映射规则

### SK Key → VectorLite ID

SK 使用 `string` 类型的 key，VectorLite 使用 `ulong` 类型的 ID。映射策略：

| 方向 | 规则 |
|------|------|
| SK → VectorLite | 将 key 存储到元数据 `_sk_key` 字段，VectorLite 自动分配 ulong ID |
| VectorLite → SK | 从元数据 `_sk_key` 字段还原 key |
| 查找 by key | 通过标量索引查询 `_sk_key = key` |

### MemoryRecord → VectorRecord 映射

```
MemoryRecord                    →  VectorRecord
──────────────────────────────────────────────────
.Embedding.ToArray()            →  .Vector
.Metadata.Id                    →  .Metadata["_sk_key"]
.Metadata.Text                  →  .Text
.Metadata.Description           →  .Metadata["_sk_description"]
.Metadata.AdditionalMetadata    →  .Metadata["_sk_additional"]
.Metadata.ExternalSourceName    →  .Metadata["_sk_source"]
.Metadata.IsReference           →  .Metadata["_sk_is_reference"]
```

### Upsert 语义

SK 的 `UpsertAsync` 需要 "存在则更新，不存在则插入" 语义：

1. 查找元数据 `_sk_key` 等于给定 key 的记录
2. 如存在，先删除旧记录
3. 插入新记录（保留原始 key）
4. 返回 key（string 类型）

---

## 注意事项

| 项目 | 说明 |
|------|------|
| 实验性 API | `IMemoryStore` 属于 SK 实验性 API（SKEXP0001），编译时有警告（已通过 NoWarn 抑制） |
| 维度自动推断 | 首次 `UpsertAsync` 时如果集合不存在，自动以 `defaultDimensions` 创建 |
| withEmbedding 参数 | VectorLite 始终存储完整向量；`withEmbedding=false` 时返回的 MemoryRecord 不含 Embedding |
| 相关性分数 | SK 的 `Relevance` = VectorLite 的 `Score`（= `1 - Distance`），范围与距离度量相关 |
| 线程安全 | `VectorLiteMemoryStore` 本身无锁，依赖底层 `VectorLiteDB` 的并发控制 |
| 生命周期 | `VectorLiteMemoryStore.Dispose()` 不会释放传入的 `VectorLiteDB`，调用者需自行管理 |

---

## 不使用 SK 的替代方式

如果项目不需要 SK，可直接使用原生 API（参见 [公共 API 参考](api-reference.md)），
无需引用 `Mugu.AI.VectorLite.SemanticKernel` 包。
