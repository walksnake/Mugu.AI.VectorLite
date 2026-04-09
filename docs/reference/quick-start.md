# 快速入门

> 5 分钟完成：创建数据库 → 插入向量 → 语义搜索 → 元数据过滤。

---

## 1. 添加项目引用

```xml
<ProjectReference Include="..\..\src\Mugu.AI.VectorLite\Mugu.AI.VectorLite.csproj" />
```

> 若已发布 NuGet 包，使用 `<PackageReference Include="Mugu.AI.VectorLite" Version="x.y.z" />`。

## 2. 最小示例

```csharp
using Mugu.AI.VectorLite;

// 打开或创建数据库（单文件，零配置）
using var db = new VectorLiteDB("my_memory.vldb");

// 创建集合（名称 + 向量维度）
var notes = db.GetOrCreateCollection("notes", 1536);

// 插入一条记录
var id = await notes.InsertAsync(new VectorRecord
{
    Vector   = embedding,   // float[]，由你的 Embedding 模型生成
    Metadata = new() { ["tag"] = "工作", ["priority"] = 5L },
    Text     = "今天的会议纪要…",
});

// 语义搜索 Top-5
var results = await notes.Query(queryEmbedding)
    .TopK(5)
    .ToListAsync();

foreach (var r in results)
    Console.WriteLine($"[{r.Score:F4}] {r.Record.Text}");
```

## 3. 混合查询（过滤 + 向量搜索）

```csharp
// 精确匹配
var work = await notes.Query(queryEmbedding)
    .Where("tag", "工作")
    .TopK(10)
    .ToListAsync();

// 范围过滤
using Mugu.AI.VectorLite.Engine;

var important = await notes.Query(queryEmbedding)
    .Where(new RangeFilter("priority", lowerBound: 7L))
    .TopK(10)
    .ToListAsync();

// 组合过滤（链式 .Where 自动 AND）
var filtered = await notes.Query(queryEmbedding)
    .Where("tag", "工作")
    .Where(new RangeFilter("priority", lowerBound: 8L))
    .TopK(5)
    .ToListAsync();
```

## 4. 记录管理

```csharp
// 按 ID 查询
var record = await notes.GetAsync(id);

// 删除
var deleted = await notes.DeleteAsync(id);

// 批量插入
var ids = await notes.InsertBatchAsync(manyRecords);
```

## 5. 数据库管理

```csharp
// 列出所有集合
var names = db.GetCollectionNames();  // IReadOnlyList<string>

// 检查集合是否存在
bool exists = db.CollectionExists("notes");

// 删除集合
db.DeleteCollection("notes");

// 手动检查点（将 WAL 合并到主文件）
db.Checkpoint();
```

## 6. 自定义配置

```csharp
using var db = new VectorLiteDB("my.vldb", new VectorLiteOptions
{
    PageSize             = 8192,                             // 页大小（字节）
    MaxDimensions        = 4096,                             // 最大向量维度
    HnswM                = 16,                               // HNSW 邻居数
    HnswEfConstruction   = 200,                              // 构建时候选集大小
    HnswEfSearch         = 50,                               // 搜索时候选集大小
    DefaultDistanceMetric = DistanceMetric.Cosine,           // 距离度量
    CheckpointInterval   = TimeSpan.FromMinutes(5),          // 自动检查点间隔
    LoggerFactory        = loggerFactory,                    // ILoggerFactory（可选）
});
```

## 7. 完整示例项目

参见 [`examples/QuickStart/`](../../examples/QuickStart/)，包含以下场景演示：

| # | 场景 | 说明 |
|---|------|------|
| 1 | 创建数据库 | 单文件零配置 |
| 2 | 批量插入 | 8 条带元数据的向量记录 |
| 3 | 基础搜索 | Top-3 语义近邻 |
| 4 | 精确过滤 | `category = "技术"` |
| 5 | 范围过滤 | `priority >= 7` |
| 6 | 组合过滤 | `category = "技术" AND priority >= 8` |
| 7 | 单条 CRUD | 按 ID 查询 / 删除 |
| 8 | 检查点 | 手动持久化 |

运行方式：

```bash
cd examples/QuickStart
dotnet run
```
