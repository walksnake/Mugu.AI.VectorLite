# 核心引擎层详细设计

> 父文档：[详细设计索引](index.md)

## 1. 设计目标

- 提供高性能的 HNSW 向量近邻检索，支持 SIMD 硬件加速。
- 提供元数据标量索引，支持先过滤后检索的混合查询。
- 通过对象池和内存预算控制 GC 压力和内存上限。

## 2. 向量距离计算

### 2.1 接口定义

```csharp
namespace Mugu.AI.VectorLite.Engine.Distance;

/// <summary>向量距离度量类型</summary>
public enum DistanceMetric : byte
{
    /// <summary>余弦相似度（返回 1 - cosine，值越小越相似）</summary>
    Cosine = 0,
    /// <summary>欧几里得距离</summary>
    Euclidean = 1,
    /// <summary>点积（值越大越相似，内部取负以统一为"越小越好"语义）</summary>
    DotProduct = 2
}

/// <summary>
/// 向量距离计算接口。所有实现必须保证线程安全（无状态纯函数）。
/// 返回值语义统一为：值越小表示越相似。
/// </summary>
internal interface IDistanceFunction
{
    DistanceMetric Metric { get; }

    /// <summary>计算两个等长向量之间的距离</summary>
    float Calculate(ReadOnlySpan<float> a, ReadOnlySpan<float> b);
}
```

### 2.2 SIMD 加速策略

所有距离实现均提供三级回退：

| 优先级 | 条件 | 实现方式 |
|--------|------|----------|
| 1 | `Vector512.IsHardwareAccelerated` | AVX-512，每次处理 16 个 float |
| 2 | `Vector256.IsHardwareAccelerated` | AVX2，每次处理 8 个 float |
| 3 | 始终可用 | `Vector<float>` 自适应宽度 |

运行时在构造函数中通过 `RuntimeIntrinsics` 检测一次，选定最快路径后缓存委托，后续调用无分支判断开销。

### 2.3 余弦距离实现示意

```csharp
internal sealed class CosineDistance : IDistanceFunction
{
    public DistanceMetric Metric => DistanceMetric.Cosine;

    public float Calculate(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        // 使用 SIMD 同时累加 dotAB, normA, normB
        // 最终返回 1.0f - (dotAB / (sqrt(normA) * sqrt(normB)))
        // 分母为零时返回 1.0f（完全不相似）
    }
}
```

### 2.4 距离函数工厂

```csharp
internal static class DistanceFunctionFactory
{
    /// <summary>根据枚举值返回对应的距离计算实例（单例复用）</summary>
    internal static IDistanceFunction Create(DistanceMetric metric) => metric switch
    {
        DistanceMetric.Cosine     => CosineDistance.Instance,
        DistanceMetric.Euclidean  => EuclideanDistance.Instance,
        DistanceMetric.DotProduct => DotProductDistance.Instance,
        _ => throw new ArgumentOutOfRangeException(nameof(metric))
    };
}
```

## 3. HNSW 索引

### 3.1 核心参数

| 参数 | 默认值 | 说明 |
|------|--------|------|
| `M` | 16 | 每个节点在非零层的最大邻居连接数 |
| `Mmax0` | `2 × M` = 32 | 第 0 层的最大邻居连接数 |
| `efConstruction` | 200 | 构建时动态候选列表大小 |
| `efSearch` | 50 | 搜索时动态候选列表大小（可每次查询覆盖） |
| `mL` | `1 / ln(M)` ≈ 0.36 | 层级生成因子 |

### 3.2 节点数据结构

```csharp
namespace Mugu.AI.VectorLite.Engine;

/// <summary>
/// HNSW 图中的单个节点。
/// 存储向量引用（不持有数据，通过 RecordId 到存储层获取）
/// 以及各层的邻居连接。
/// </summary>
internal sealed class HNSWNode
{
    /// <summary>对应的向量记录ID</summary>
    internal ulong RecordId { get; }

    /// <summary>该节点所在的最大层级</summary>
    internal int MaxLayer { get; }

    /// <summary>
    /// 各层的邻居列表。Neighbors[i] 为第 i 层的邻居 RecordId 列表。
    /// 长度 = MaxLayer + 1。
    /// </summary>
    internal List<ulong>[] Neighbors { get; }

    /// <summary>节点向量的缓存副本（常驻内存以避免重复IO）</summary>
    internal ReadOnlyMemory<float> Vector { get; }
}
```

### 3.3 图结构

```csharp
internal sealed class HNSWGraph
{
    /// <summary>入口点节点ID（最高层的节点）</summary>
    internal ulong EntryPointId { get; set; }

    /// <summary>当前图的最大层级</summary>
    internal int MaxLayer { get; set; }

    /// <summary>所有节点的快速查找表</summary>
    internal Dictionary<ulong, HNSWNode> Nodes { get; }

    /// <summary>节点总数</summary>
    internal int Count => Nodes.Count;
}
```

### 3.4 插入算法

```text
INSERT(graph, recordId, vector, distFunc, M, Mmax0, efConstruction, mL):
    创建新节点 q，层级 l = floor(-ln(random()) × mL)

    若 graph 为空:
        graph.EntryPointId = q.RecordId
        graph.MaxLayer = l
        返回

    ep = graph.EntryPointId
    L  = graph.MaxLayer

    ── 阶段1：从顶层贪心下降到 l+1 层 ──
    FOR lc = L DOWNTO l+1:
        W = SEARCH_LAYER(graph, vector, ep, ef=1, lc, distFunc)
        ep = W 中距离最近的节点

    ── 阶段2：在 l 层到第 0 层执行插入 ──
    FOR lc = l DOWNTO 0:
        W = SEARCH_LAYER(graph, vector, ep, efConstruction, lc, distFunc)
        Mmax_cur = (lc == 0) ? Mmax0 : M
        neighbors = SELECT_NEIGHBORS_HEURISTIC(vector, W, Mmax_cur, distFunc)

        FOR EACH e IN neighbors:
            添加双向连接 q ↔ e（第 lc 层）

            若 e.Neighbors[lc].Count > Mmax_cur:
                e.Neighbors[lc] = SELECT_NEIGHBORS_HEURISTIC(
                    e.Vector, e.Neighbors[lc], Mmax_cur, distFunc)

        ep = W 中距离最近的节点

    若 l > L:
        graph.EntryPointId = q.RecordId
        graph.MaxLayer = l
```

### 3.5 搜索算法

```text
SEARCH(graph, queryVector, K, ef, distFunc, candidateIds=null):
    ep = graph.EntryPointId
    L  = graph.MaxLayer

    ── 阶段1：贪心下降到第 1 层 ──
    FOR lc = L DOWNTO 1:
        W = SEARCH_LAYER(graph, queryVector, ep, ef=1, lc, distFunc)
        ep = W 中距离最近的节点

    ── 阶段2：在第 0 层搜索 ──
    ef_actual = max(ef, K)
    W = SEARCH_LAYER(graph, queryVector, ep, ef_actual, 0, distFunc, candidateIds)

    返回 W 中距离最小的 K 个结果

SEARCH_LAYER(graph, queryVector, entryId, ef, layer, distFunc, candidateIds=null):
    visited = HashSet { entryId }
    candidates = MinHeap { (dist(queryVector, entry.Vector), entryId) }
    results   = MaxHeap { 同上 }（容量 ef）

    WHILE candidates 非空:
        (cDist, cId) = candidates.ExtractMin()
        fDist = results 中最远距离

        若 cDist > fDist 且 results.Count >= ef:
            跳出循环

        FOR EACH neighborId IN graph.Nodes[cId].Neighbors[layer]:
            若 neighborId 已在 visited 中: 跳过
            visited.Add(neighborId)

            若 candidateIds != null 且 neighborId 不在 candidateIds 中:
                跳过（前置过滤）

            nDist = dist(queryVector, neighbor.Vector)
            fDist = results 中最远距离

            若 results.Count < ef 或 nDist < fDist:
                candidates.Insert(nDist, neighborId)
                results.Insert(nDist, neighborId)
                若 results.Count > ef:
                    results.ExtractMax()

    返回 results
```

### 3.6 删除策略

采用**惰性删除 + 后台压缩**：

1. **标记删除**：将记录的 `Flags` 标记位设为已删除，从标量索引移除。
2. **搜索时跳过**：SEARCH_LAYER 中遇到已删除节点时跳过，不计入结果。
3. **后台压缩**：当已删除节点比例超过阈值（默认 20%）时，触发索引重建：
   - 收集所有未删除节点
   - 构建新的 HNSW 图
   - 原子替换旧图引用

### 3.7 序列化与持久化

HNSW 图序列化为 `HNSWGraph` 类型页（0x03），格式：

```text
每个 HNSWGraph 页:
    4   NodeCount (uint)
    变长 HNSWNodeEntry[]

每个 HNSWNodeEntry:
    8   RecordId (ulong)
    4   MaxLayer (int)
    FOR EACH layer (0..MaxLayer):
        4   NeighborCount (uint)
        NeighborCount × 8   NeighborIds (ulong[])
```

图信息跨越多个页时通过页头 `NextPageId` 链接。启动时一次性将所有 HNSWGraph 页加载到内存中重建 `HNSWGraph` 对象。

### 3.8 HNSWIndex 类设计

```csharp
namespace Mugu.AI.VectorLite.Engine;

internal sealed class HNSWIndex
{
    private readonly HNSWGraph _graph;
    private readonly IDistanceFunction _distFunc;
    private readonly int _m;
    private readonly int _mmax0;
    private readonly int _efConstruction;
    private readonly double _mL;
    private readonly Random _random;

    internal HNSWIndex(IDistanceFunction distFunc, HNSWOptions options);

    /// <summary>插入一个向量节点到索引中</summary>
    internal void Insert(ulong recordId, ReadOnlyMemory<float> vector);

    /// <summary>标记删除（惰性）</summary>
    internal void MarkDeleted(ulong recordId);

    /// <summary>
    /// 搜索最近邻。
    /// candidateIds 非空时启用前置过滤（仅在候选集合内搜索）。
    /// </summary>
    internal IReadOnlyList<(ulong RecordId, float Distance)> Search(
        ReadOnlySpan<float> queryVector,
        int topK,
        int efSearch,
        HashSet<ulong>? candidateIds = null);

    /// <summary>序列化到页数据（供存储层持久化）</summary>
    internal byte[] Serialize();

    /// <summary>从页数据反序列化重建索引</summary>
    internal static HNSWIndex Deserialize(
        ReadOnlySpan<byte> data,
        IDistanceFunction distFunc,
        HNSWOptions options);

    /// <summary>当前节点数</summary>
    internal int Count => _graph.Count;
}
```

## 4. 标量索引

### 4.1 数据结构

采用**嵌套字典**实现倒排索引：

```text
字段名 (string)
  └── 字段值 (object, 支持 string / long / double / bool)
        └── 记录ID集合 (HashSet<ulong>)
```

### 4.2 支持的过滤操作

| 操作 | 说明 | 示例 |
|------|------|------|
| `Equal` | 精确匹配 | `doc_type == "note"` |
| `NotEqual` | 不等于 | `status != "deleted"` |
| `In` | 集合包含 | `tag IN ["a","b","c"]` |
| `GreaterThan` / `LessThan` | 范围比较（仅数值） | `score > 0.8` |
| `And` / `Or` / `Not` | 逻辑组合 | 任意嵌套 |

### 4.3 过滤表达式 AST

```csharp
namespace Mugu.AI.VectorLite.Engine;

/// <summary>过滤表达式基类</summary>
public abstract class FilterExpression
{
    /// <summary>对标量索引求值，返回符合条件的记录ID集合</summary>
    internal abstract HashSet<ulong> Evaluate(ScalarIndex index);
}

public sealed class EqualFilter : FilterExpression
{
    public string Field { get; }
    public object Value { get; }
    // ...
}

public sealed class InFilter : FilterExpression
{
    public string Field { get; }
    public IReadOnlyList<object> Values { get; }
    // ...
}

public sealed class RangeFilter : FilterExpression
{
    public string Field { get; }
    public object? LowerBound { get; }    // null 表示无下界
    public object? UpperBound { get; }    // null 表示无上界
    public bool LowerInclusive { get; }
    public bool UpperInclusive { get; }
    // ...
}

public sealed class AndFilter : FilterExpression
{
    public IReadOnlyList<FilterExpression> Operands { get; }
    internal override HashSet<ulong> Evaluate(ScalarIndex index)
    {
        // 所有子表达式求值结果取交集
    }
}

public sealed class OrFilter : FilterExpression { /* 取并集 */ }
public sealed class NotFilter : FilterExpression { /* 从全集减去子表达式结果 */ }
```

### 4.4 ScalarIndex 类设计

```csharp
internal sealed class ScalarIndex
{
    private readonly Dictionary<string, Dictionary<object, HashSet<ulong>>> _index = new();

    /// <summary>为一条记录的所有元数据字段建立索引</summary>
    internal void Add(ulong recordId, Dictionary<string, object>? metadata);

    /// <summary>移除一条记录的所有索引条目</summary>
    internal void Remove(ulong recordId, Dictionary<string, object>? metadata);

    /// <summary>通过过滤表达式求值</summary>
    internal HashSet<ulong> Filter(FilterExpression expression)
        => expression.Evaluate(this);

    /// <summary>获取所有已索引的记录ID（用于 NotFilter 的全集）</summary>
    internal HashSet<ulong> GetAllRecordIds();

    /// <summary>精确查找某字段某值对应的记录集合</summary>
    internal HashSet<ulong> Lookup(string field, object value);

    /// <summary>范围查找（需要遍历该字段所有值进行比较）</summary>
    internal HashSet<ulong> RangeLookup(string field, object? lower, object? upper,
        bool lowerInclusive, bool upperInclusive);

    /// <summary>序列化到字节数组（供存储层持久化）</summary>
    internal byte[] Serialize();

    /// <summary>反序列化</summary>
    internal static ScalarIndex Deserialize(ReadOnlySpan<byte> data);
}
```

## 5. 查询处理器

```csharp
namespace Mugu.AI.VectorLite.Engine;

/// <summary>
/// 查询引擎：协调标量索引和 HNSW 索引完成混合查询。
/// </summary>
internal sealed class QueryEngine
{
    private readonly HNSWIndex _hnswIndex;
    private readonly ScalarIndex _scalarIndex;

    internal QueryEngine(HNSWIndex hnswIndex, ScalarIndex scalarIndex);

    /// <summary>
    /// 执行混合查询。
    /// 策略：先通过标量索引过滤得到候选ID集合，再将候选集传入HNSW搜索。
    /// 若无过滤条件则直接执行全量HNSW搜索。
    /// </summary>
    internal IReadOnlyList<SearchResult> Search(
        ReadOnlySpan<float> queryVector,
        int topK,
        int efSearch,
        FilterExpression? filter = null,
        float minScore = 0f)
    {
        HashSet<ulong>? candidateIds = null;

        if (filter is not null)
        {
            candidateIds = _scalarIndex.Filter(filter);
            if (candidateIds.Count == 0)
                return Array.Empty<SearchResult>();
        }

        var rawResults = _hnswIndex.Search(queryVector, topK, efSearch, candidateIds);

        // 将距离转换为相似度分数（针对不同度量方式）并应用 minScore 阈值
        // 返回 SearchResult 列表
    }
}
```

### 5.1 查询执行流程图

```text
QueryAsync(vector, filter, topK)
    │
    ├── filter != null ?
    │   ├── YES → ScalarIndex.Filter(filter) → candidateIds
    │   │         candidateIds 为空? → 返回空结果
    │   └── NO  → candidateIds = null
    │
    ├── HNSWIndex.Search(vector, topK, efSearch, candidateIds)
    │   └── 返回 (recordId, distance)[]
    │
    ├── 距离 → 分数转换
    │   ├── Cosine:     score = 1.0 - distance
    │   ├── Euclidean:  score = 1.0 / (1.0 + distance)
    │   └── DotProduct: score = -distance (内部已取负)
    │
    ├── 过滤 score < minScore 的结果
    │
    └── 构建 SearchResult[] 返回
```

## 6. 内存管理器

```csharp
namespace Mugu.AI.VectorLite.Engine;

/// <summary>
/// 管理向量数组和临时列表的对象池，降低GC压力。
/// </summary>
internal sealed class MemoryManager : IDisposable
{
    private readonly ArrayPool<float> _vectorPool;
    private readonly ObjectPool<List<ulong>> _neighborListPool;

    internal MemoryManager();

    /// <summary>从池中租借指定维度的 float 数组</summary>
    internal float[] RentVector(int dimensions);

    /// <summary>归还 float 数组到池中</summary>
    internal void ReturnVector(float[] vector);

    /// <summary>租借临时邻居ID列表</summary>
    internal List<ulong> RentNeighborList();

    /// <summary>归还邻居ID列表</summary>
    internal void ReturnNeighborList(List<ulong> list);

    public void Dispose();
}
```

**使用约束**：

- `RentVector` 返回的数组长度可能大于请求维度（`ArrayPool` 特性），调用方必须使用 `Span<float>` 切片到实际维度。
- HNSW 搜索过程中的临时候选列表通过 `RentNeighborList` 获取，搜索完成后归还。
- `MemoryManager` 为线程安全类（`ArrayPool` 和 `ObjectPool` 均线程安全）。
