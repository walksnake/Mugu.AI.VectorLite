# VectorLite QuickStart 快速入门

本示例演示了 `Mugu.AI.VectorLite` 的核心用法：

1. **创建数据库** — 单文件嵌入式，零配置
2. **插入向量记录** — 支持向量 + 元数据 + 原文
3. **语义搜索** — Top-K 近邻检索
4. **混合查询** — 元数据过滤 + 向量搜索
5. **记录管理** — 按 ID 查询 / 删除

## 运行方式

```bash
cd examples/QuickStart
dotnet run
```

## 示例输出

```
数据库已打开: C:\Users\...\quickstart_demo.vldb

已插入 8 条记录到集合 "articles"

═══ 基础语义搜索 (Top-3) ═══
  [1.0000] Python 机器学习入门  (距离=0.0000)
  [0.7234] 深度学习与神经网络  (距离=0.2766)
  ...

═══ 混合查询：category = "技术" ═══
  [1.0000] Python 机器学习入门  category=技术
  ...

═══ 组合过滤：category="技术" AND priority >= 8 ═══
  [1.0000] Python 机器学习入门  category=技术 priority=8
  ...

✅ QuickStart 示例运行完成！
```

## 在你的项目中使用

```csharp
using Mugu.AI.VectorLite;

// 打开或创建数据库（单文件）
using var db = new VectorLiteDB("my_memory.db");

// 创建集合（指定向量维度）
var collection = db.GetOrCreateCollection("notes", 1536);

// 插入记录
await collection.InsertAsync(new VectorRecord
{
    Vector = embeddingFromModel,     // float[]，由你的 Embedding 模型生成
    Metadata = new() { ["tag"] = "工作", ["priority"] = 5L },
    Text = "今天的会议纪要...",
});

// 语义搜索 + 元数据过滤
var results = await collection.Query(queryEmbedding)
    .Where("tag", "工作")
    .TopK(10)
    .ToListAsync();
```
