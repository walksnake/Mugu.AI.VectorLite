// ============================================================
//  Mugu.AI.VectorLite — 快速入门示例
//  演示：创建数据库 → 插入向量 → 语义搜索 → 元数据过滤
// ============================================================

using Mugu.AI.VectorLite;
using Mugu.AI.VectorLite.Engine;
using System.Reflection;

// ── 1. 准备模拟数据 ──────────────────────────────────────────
// 实际场景中向量由 Embedding 模型生成（如 text-embedding-3-small 输出 1536 维），
// 此处用 8 维随机向量演示流程。
const int dimensions = 8;

var documents = new (string Title, string Category, long Priority, float[] Embedding)[]
{
    ("如何训练一只猫",       "宠物", 5, RandomVector(dimensions, seed: 1)),
    ("Python 机器学习入门",  "技术", 8, RandomVector(dimensions, seed: 2)),
    ("周末露营装备清单",     "生活", 3, RandomVector(dimensions, seed: 3)),
    ("深度学习与神经网络",   "技术", 9, RandomVector(dimensions, seed: 4)),
    ("家庭烘焙面包教程",     "生活", 4, RandomVector(dimensions, seed: 5)),
    ("猫咪常见疾病预防",     "宠物", 7, RandomVector(dimensions, seed: 6)),
    ("Transformer 架构详解", "技术", 10, RandomVector(dimensions, seed: 7)),
    ("室内植物养护指南",     "生活", 6, RandomVector(dimensions, seed: 8)),
};

// ── 2. 创建 / 打开数据库 ────────────────────────────────────
var dbPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "quickstart_demo.vldb");
using var db = new VectorLiteDB(dbPath);

Console.WriteLine($"数据库已打开: {dbPath}");
Console.WriteLine();

// ── 3. 创建集合并批量插入记录 ────────────────────────────────
var collection = db.GetOrCreateCollection("articles", dimensions);

var records = documents.Select(doc => new VectorRecord
{
    Vector = doc.Embedding,
    Metadata = new Dictionary<string, object>
    {
        ["title"] = doc.Title,
        ["category"] = doc.Category,
        ["priority"] = doc.Priority,
    },
    Text = doc.Title,
}).ToArray();

var ids = await collection.InsertBatchAsync(records);
Console.WriteLine($"已插入 {ids.Count} 条记录到集合 \"{collection.Name}\"");
Console.WriteLine();

// ── 4. 基础语义搜索：查找与查询向量最相似的 3 条记录 ─────────
Console.WriteLine("═══ 基础语义搜索 (Top-3) ═══");
var queryVector = RandomVector(dimensions, seed: 2); // 与"Python 机器学习入门"同源

var results = await collection.Query(queryVector)
    .TopK(3)
    .ToListAsync();

foreach (var r in results)
{
    Console.WriteLine($"  [{r.Score:F4}] {r.Record.Metadata!["title"]}  (距离={r.Distance:F4})");
}
Console.WriteLine();

// ── 5. 混合查询：元数据过滤 + 语义搜索 ──────────────────────

// 5a. 精确匹配：只在"技术"分类中搜索
Console.WriteLine("═══ 混合查询：category = \"技术\" ═══");
var techResults = await collection.Query(queryVector)
    .Where("category", "技术")
    .TopK(5)
    .ToListAsync();

foreach (var r in techResults)
{
    Console.WriteLine($"  [{r.Score:F4}] {r.Record.Metadata!["title"]}  category={r.Record.Metadata["category"]}");
}
Console.WriteLine();

// 5b. 范围过滤：优先级 >= 7 的高优记录
Console.WriteLine("═══ 混合查询：priority >= 7 ═══");
var highPriorityResults = await collection.Query(queryVector)
    .Where(new RangeFilter("priority", lowerBound: 7L))
    .TopK(5)
    .ToListAsync();

foreach (var r in highPriorityResults)
{
    Console.WriteLine($"  [{r.Score:F4}] {r.Record.Metadata!["title"]}  priority={r.Record.Metadata["priority"]}");
}
Console.WriteLine();

// 5c. 组合过滤：技术类 + 优先级 >= 8
Console.WriteLine("═══ 组合过滤：category=\"技术\" AND priority >= 8 ═══");
var combinedResults = await collection.Query(queryVector)
    .Where("category", "技术")
    .Where(new RangeFilter("priority", lowerBound: 8L))
    .TopK(5)
    .ToListAsync();

foreach (var r in combinedResults)
{
    var meta = r.Record.Metadata!;
    Console.WriteLine($"  [{r.Score:F4}] {meta["title"]}  category={meta["category"]} priority={meta["priority"]}");
}
Console.WriteLine();

// ── 6. 单条记录操作：查询 / 删除 ────────────────────────────
var firstId = ids[0];
var record = await collection.GetAsync(firstId);
Console.WriteLine($"按 ID 查询: id={firstId} → \"{record?.Text}\"");

var deleted = await collection.DeleteAsync(firstId);
Console.WriteLine($"删除记录:   id={firstId} → {(deleted ? "成功" : "失败")}");
Console.WriteLine($"集合剩余记录数: {collection.Count}");
Console.WriteLine();

// ── 7. 数据库管理 ────────────────────────────────────────────
Console.WriteLine($"所有集合: [{string.Join(", ", db.GetCollectionNames())}]");
db.Checkpoint(); // 手动触发检查点
Console.WriteLine("检查点已完成，数据已持久化。");
Console.WriteLine();
Console.WriteLine("✅ QuickStart 示例运行完成！");

// ── 清理临时文件 ─────────────────────────────────────────────
db.Dispose();
try { File.Delete(dbPath); } catch { }
try { File.Delete(dbPath + "-wal"); } catch { }

// ── 辅助方法 ─────────────────────────────────────────────────
static float[] RandomVector(int dims, int seed)
{
    var rng = new Random(seed);
    var vec = new float[dims];
    var sumSq = 0f;
    for (var i = 0; i < dims; i++)
    {
        vec[i] = (float)(rng.NextDouble() * 2 - 1);
        sumSq += vec[i] * vec[i];
    }

    // 归一化（余弦距离要求）
    var norm = MathF.Sqrt(sumSq);
    if (norm > float.Epsilon)
    {
        for (var i = 0; i < dims; i++)
            vec[i] /= norm;
    }
    return vec;
}
