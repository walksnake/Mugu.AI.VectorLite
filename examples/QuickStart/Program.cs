// ============================================================
//  Mugu.AI.VectorLite — 快速入门示例
//  完整演示数据库全部核心功能：
//    1. 创建数据库与集合
//    2. 插入记录（单条 / 批量）
//    3. 语义搜索（Top-K）
//    4. 混合查询（元数据过滤 + 语义搜索）
//    5. 记录管理（查询 / 删除 / Upsert）
//    6. 持久化与崩溃恢复
//    7. 多集合管理
//    8. 数据库配置与检查点
// ============================================================

using Mugu.AI.VectorLite;
using Mugu.AI.VectorLite.Engine;

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
    var norm = MathF.Sqrt(sumSq);
    if (norm > float.Epsilon)
        for (var i = 0; i < dims; i++)
            vec[i] /= norm;
    return vec;
}

static void PrintSeparator(string title)
{
    Console.WriteLine();
    Console.WriteLine($"═══ {title} ═══");
}

// ── 准备临时数据库路径 ───────────────────────────────────────
var dbPath = Path.Combine(Path.GetTempPath(), $"vlite_quickstart_{Guid.NewGuid():N}.vldb");

try
{
    await RunQuickStartAsync(dbPath);
}
finally
{
    // 清理临时文件
    try { File.Delete(dbPath); } catch { }
    try { File.Delete(dbPath + "-wal"); } catch { }
}

// ══════════════════════════════════════════════════════════════
//  完整示例流程
// ══════════════════════════════════════════════════════════════
async Task RunQuickStartAsync(string path)
{
    const int dimensions = 8;

    // ── 1. 准备模拟数据 ──────────────────────────────────────
    // 实际场景中向量由 Embedding 模型生成（如 text-embedding-3-small 输出 1536 维），
    // 此处用 8 维随机向量演示流程。
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

    // ══════════════════════════════════════════════════════════
    //  PART A：基本 CRUD + 查询
    // ══════════════════════════════════════════════════════════

    PrintSeparator("1. 创建数据库与集合");

    // 可自定义配置（此处展示默认值，通常直接 new VectorLiteDB(path) 即可）
    var options = new VectorLiteOptions
    {
        PageSize = 8192,           // 页大小（字节）
        HnswM = 16,               // HNSW 邻居数
        HnswEfConstruction = 200,  // HNSW 构建精度
        HnswEfSearch = 50,         // HNSW 搜索精度（默认值，可逐查询覆盖）
        CheckpointInterval = TimeSpan.FromMinutes(5), // 自动检查点间隔
    };

    using var db = new VectorLiteDB(path, options);
    Console.WriteLine($"  数据库已创建: {path}");

    var collection = db.GetOrCreateCollection("articles", dimensions);
    Console.WriteLine($"  集合已创建: \"{collection.Name}\" (维度={collection.Dimensions})");

    // ── 2. 插入记录 ──────────────────────────────────────────
    PrintSeparator("2. 插入记录");

    // 2a. 单条插入
    var singleRecord = new VectorRecord
    {
        Vector = documents[0].Embedding,
        Metadata = new Dictionary<string, object>
        {
            ["title"] = documents[0].Title,
            ["category"] = documents[0].Category,
            ["priority"] = documents[0].Priority,
        },
        Text = documents[0].Title,
    };
    var singleId = await collection.InsertAsync(singleRecord);
    Console.WriteLine($"  单条插入: id={singleId}, \"{documents[0].Title}\"");

    // 2b. 批量插入（跳过第一条，已单独插入）
    var batchRecords = documents.Skip(1).Select(doc => new VectorRecord
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

    var batchIds = await collection.InsertBatchAsync(batchRecords);
    Console.WriteLine($"  批量插入: {batchIds.Count} 条记录");
    Console.WriteLine($"  集合总记录数: {collection.Count}");

    // ── 3. 基础语义搜索 ──────────────────────────────────────
    PrintSeparator("3. 基础语义搜索 (Top-3)");

    var queryVector = RandomVector(dimensions, seed: 2); // 与"Python 机器学习入门"同源
    var results = await collection.Query(queryVector)
        .TopK(3)
        .ToListAsync();

    foreach (var r in results)
        Console.WriteLine($"  [{r.Score:F4}] {r.Record.Metadata!["title"]}  (距离={r.Distance:F4})");

    // ── 4. 混合查询 ──────────────────────────────────────────

    // 4a. 精确匹配过滤
    PrintSeparator("4a. 混合查询: category = \"技术\"");

    var techResults = await collection.Query(queryVector)
        .Where("category", "技术")
        .TopK(5)
        .ToListAsync();

    foreach (var r in techResults)
        Console.WriteLine($"  [{r.Score:F4}] {r.Record.Metadata!["title"]}");

    // 4b. 范围过滤
    PrintSeparator("4b. 混合查询: priority >= 7");

    var highPriResults = await collection.Query(queryVector)
        .Where(new RangeFilter("priority", lowerBound: 7L))
        .TopK(5)
        .ToListAsync();

    foreach (var r in highPriResults)
        Console.WriteLine($"  [{r.Score:F4}] {r.Record.Metadata!["title"]}  priority={r.Record.Metadata!["priority"]}");

    // 4c. 组合过滤（AND）
    PrintSeparator("4c. 组合过滤: category=\"技术\" AND priority >= 8");

    var combinedResults = await collection.Query(queryVector)
        .Where("category", "技术")
        .Where(new RangeFilter("priority", lowerBound: 8L))
        .TopK(5)
        .ToListAsync();

    foreach (var r in combinedResults)
    {
        var m = r.Record.Metadata!;
        Console.WriteLine($"  [{r.Score:F4}] {m["title"]}  category={m["category"]} priority={m["priority"]}");
    }

    // 4d. 最低分数阈值
    PrintSeparator("4d. 最低分数过滤: minScore = 0.5");

    var minScoreResults = await collection.Query(queryVector)
        .WithMinScore(0.5f)
        .TopK(10)
        .ToListAsync();

    Console.WriteLine($"  满足 Score >= 0.5 的结果: {minScoreResults.Count} 条");
    foreach (var r in minScoreResults)
        Console.WriteLine($"  [{r.Score:F4}] {r.Record.Metadata!["title"]}");

    // 4e. 自定义 efSearch（搜索精度 vs 速度权衡）
    PrintSeparator("4e. 自定义 efSearch (精度 vs 速度)");

    var preciseResults = await collection.Query(queryVector)
        .TopK(3)
        .WithEfSearch(200) // 更高精度，更慢
        .ToListAsync();

    Console.WriteLine($"  efSearch=200 搜索结果: {preciseResults.Count} 条");
    foreach (var r in preciseResults)
        Console.WriteLine($"  [{r.Score:F4}] {r.Record.Metadata!["title"]}");

    // ── 5. 记录管理 ──────────────────────────────────────────

    // 5a. 按 ID 查询
    PrintSeparator("5a. 按 ID 查询记录");

    var fetched = await collection.GetAsync(singleId);
    Console.WriteLine($"  id={singleId}: \"{fetched?.Text}\"");
    Console.WriteLine($"  向量维度: {fetched?.Vector.Length}");
    Console.WriteLine($"  元数据: {string.Join(", ", fetched?.Metadata?.Select(kv => $"{kv.Key}={kv.Value}") ?? [])}");

    // 5b. 按元数据字段查找 ID
    PrintSeparator("5b. 按元数据查找 ID");

    var petIds = await collection.FindIdsByMetadataAsync("category", "宠物");
    Console.WriteLine($"  category=\"宠物\" 的记录ID: [{string.Join(", ", petIds)}]");

    // 5c. 删除记录
    PrintSeparator("5c. 删除记录");

    var deleted = await collection.DeleteAsync(singleId);
    Console.WriteLine($"  删除 id={singleId}: {(deleted ? "成功" : "失败")}");
    Console.WriteLine($"  集合剩余记录数: {collection.Count}");

    // 再次尝试获取已删除记录
    var deletedRecord = await collection.GetAsync(singleId);
    Console.WriteLine($"  重新获取 id={singleId}: {(deletedRecord == null ? "null (已删除)" : "仍存在")}");

    // 5d. Upsert（按元数据键去重插入或更新）
    PrintSeparator("5d. Upsert 操作");

    var upsertRecord = new VectorRecord
    {
        Vector = RandomVector(dimensions, seed: 100),
        Metadata = new Dictionary<string, object>
        {
            ["title"] = "Transformer 架构详解（修订版）",
            ["category"] = "技术",
            ["priority"] = 10L,
        },
        Text = "Transformer 架构详解（修订版）",
    };

    var beforeCount = collection.Count;
    var upsertId = await collection.UpsertAsync(upsertRecord, "title");
    Console.WriteLine($"  Upsert 结果: 新 id={upsertId}");
    Console.WriteLine($"  记录数变化: {beforeCount} → {collection.Count}");

    // ── 6. 多集合管理 ────────────────────────────────────────
    PrintSeparator("6. 多集合管理");

    var collection2 = db.GetOrCreateCollection("images", 128);
    Console.WriteLine($"  创建第二个集合: \"{collection2.Name}\" (维度={collection2.Dimensions})");

    // 插入一些数据到第二个集合
    for (var i = 0; i < 3; i++)
    {
        await collection2.InsertAsync(new VectorRecord
        {
            Vector = RandomVector(128, seed: 200 + i),
            Metadata = new Dictionary<string, object> { ["label"] = $"image_{i}" },
            Text = $"图片描述 #{i}",
        });
    }

    Console.WriteLine($"  所有集合: [{string.Join(", ", db.GetCollectionNames())}]");
    Console.WriteLine($"  articles 记录数: {collection.Count}");
    Console.WriteLine($"  images 记录数: {collection2.Count}");

    // 检查集合存在性
    Console.WriteLine($"  CollectionExists(\"articles\"): {db.CollectionExists("articles")}");
    Console.WriteLine($"  CollectionExists(\"missing\"): {db.CollectionExists("missing")}");

    // 删除集合
    var collDeleted = db.DeleteCollection("images");
    Console.WriteLine($"  DeleteCollection(\"images\"): {collDeleted}");
    Console.WriteLine($"  剩余集合: [{string.Join(", ", db.GetCollectionNames())}]");

    // ── 7. 手动检查点 ────────────────────────────────────────
    PrintSeparator("7. 手动检查点");
    db.Checkpoint();
    Console.WriteLine("  检查点已完成，集合数据和索引已持久化到磁盘。");

    // ══════════════════════════════════════════════════════════
    //  PART B：持久化与恢复演示
    // ══════════════════════════════════════════════════════════
    PrintSeparator("8. 持久化与恢复演示");

    // 使用独立的数据库路径演示持久化
    var persistPath = path + ".persist_demo";
    ulong persistedRecordId;

    try
    {
        // 第一阶段：写入数据并关闭（Dispose 时自动检查点）
        Console.WriteLine("  [写入阶段]");
        {
            using var persistDb = new VectorLiteDB(persistPath);
            var coll = persistDb.GetOrCreateCollection("memories", dimensions);

            var memoryRecord = new VectorRecord
            {
                Vector = RandomVector(dimensions, seed: 999),
                Metadata = new Dictionary<string, object>
                {
                    ["topic"] = "AI",
                    ["importance"] = 10L,
                },
                Text = "人工智能正在改变世界的方方面面",
            };

            persistedRecordId = await coll.InsertAsync(memoryRecord);
            Console.WriteLine($"    写入记录 id={persistedRecordId}: \"{memoryRecord.Text}\"");

            // 再写入几条用于搜索验证
            await coll.InsertAsync(new VectorRecord
            {
                Vector = RandomVector(dimensions, seed: 1000),
                Metadata = new Dictionary<string, object> { ["topic"] = "cooking" },
                Text = "如何做一碗好吃的番茄鸡蛋面",
            });
            await coll.InsertAsync(new VectorRecord
            {
                Vector = RandomVector(dimensions, seed: 1001),
                Metadata = new Dictionary<string, object> { ["topic"] = "AI" },
                Text = "深度强化学习在机器人控制中的应用",
            });

            Console.WriteLine($"    共写入 {coll.Count} 条记录，关闭数据库...");
        } // Dispose → 自动检查点

        // 第二阶段：重新打开，验证数据完整恢复
        Console.WriteLine("  [恢复阶段]");
        {
            using var persistDb = new VectorLiteDB(persistPath);

            // 验证集合存在
            var collNames = persistDb.GetCollectionNames();
            Console.WriteLine($"    恢复的集合: [{string.Join(", ", collNames)}]");

            var coll = persistDb.GetOrCreateCollection("memories", dimensions);
            Console.WriteLine($"    memories 记录数: {coll.Count}");

            // 按 ID 获取之前写入的记录
            var restored = await coll.GetAsync(persistedRecordId);
            Console.WriteLine($"    恢复记录 id={persistedRecordId}: \"{restored?.Text}\"");
            Console.WriteLine($"    元数据 topic={restored?.Metadata?["topic"]}");

            // 恢复后的向量搜索
            var searchVec = RandomVector(dimensions, seed: 999); // 与第一条同源
            var searchResults = await coll.Query(searchVec)
                .TopK(3)
                .ToListAsync();

            Console.WriteLine("    恢复后搜索结果:");
            foreach (var r in searchResults)
                Console.WriteLine($"      [{r.Score:F4}] {r.Record.Text}");
        }
    }
    finally
    {
        try { File.Delete(persistPath); } catch { }
        try { File.Delete(persistPath + "-wal"); } catch { }
    }

    // ══════════════════════════════════════════════════════════
    //  完成
    // ══════════════════════════════════════════════════════════
    Console.WriteLine();
    Console.WriteLine("✅ QuickStart 示例运行完成！");
    Console.WriteLine();
    Console.WriteLine("覆盖功能总结:");
    Console.WriteLine("  ✓ 数据库创建与配置");
    Console.WriteLine("  ✓ 集合的创建、查询、删除");
    Console.WriteLine("  ✓ 记录的插入（单条 / 批量）");
    Console.WriteLine("  ✓ 语义向量搜索（Top-K）");
    Console.WriteLine("  ✓ 混合查询（精确匹配 / 范围过滤 / 组合 AND）");
    Console.WriteLine("  ✓ 最低分数阈值 / 自定义 efSearch");
    Console.WriteLine("  ✓ 按 ID 查询 / 按元数据查找");
    Console.WriteLine("  ✓ 记录删除与 Upsert");
    Console.WriteLine("  ✓ 多集合管理");
    Console.WriteLine("  ✓ 手动检查点");
    Console.WriteLine("  ✓ 持久化与崩溃恢复（关闭→重开→数据完整）");
}
