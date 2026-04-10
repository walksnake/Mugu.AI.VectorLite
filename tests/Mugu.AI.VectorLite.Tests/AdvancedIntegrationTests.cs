using FluentAssertions;
using Mugu.AI.VectorLite.Storage;

namespace Mugu.AI.VectorLite.Tests;

/// <summary>
/// VectorLiteDB 高级场景测试：WAL 恢复、页增长、检查点交错、数据完整性。
/// </summary>
public class AdvancedIntegrationTests : IDisposable
{
    private readonly List<string> _tempFiles = new();

    private string TempDb()
    {
        var path = Path.Combine(Path.GetTempPath(), $"vlite_adv_{Guid.NewGuid():N}.vldb");
        _tempFiles.Add(path);
        _tempFiles.Add(path + "-wal");
        return path;
    }

    private static float[] RandVec(int dims, int seed)
    {
        var rng = new Random(seed);
        var v = new float[dims];
        for (var i = 0; i < dims; i++) v[i] = (float)rng.NextDouble();
        return v;
    }

    // ===== 页增长测试 =====

    [Fact]
    public async Task LargeInsertBatch_ShouldForcePageGrowth()
    {
        // 插入大量数据触发 PageManager.GrowFile
        var path = TempDb();
        using var db = new VectorLiteDB(path, new VectorLiteOptions { PageSize = 4096 });
        var coll = db.GetOrCreateCollection("growth", 64);

        // 每条记录含 64 维向量(256 bytes) + 元数据 + 文本
        // 插入 200 条，应触发文件增长
        var records = Enumerable.Range(0, 200).Select(i => new VectorRecord
        {
            Vector = RandVec(64, i),
            Metadata = new() { ["idx"] = (long)i, ["tag"] = $"record_{i}" },
            Text = $"这是第 {i} 条记录的文本内容，用于触发页增长测试。"
        }).ToList();

        var ids = await coll.InsertBatchAsync(records);
        ids.Should().HaveCount(200);
        coll.Count.Should().Be(200);

        // 检查点以触发 FlushToStorage 写入大量页数据
        db.Checkpoint();

        // 重新打开验证数据完整性
        db.Dispose();
        using var db2 = new VectorLiteDB(path);
        var coll2 = db2.GetOrCreateCollection("growth", 64);
        coll2.Count.Should().Be(200);

        // 验证首尾数据
        var first = await coll2.GetAsync(1);
        first!.Metadata!["idx"].Should().Be(0L);

        var last = await coll2.GetAsync(200);
        last!.Metadata!["idx"].Should().Be(199L);
    }

    // ===== WAL 损坏恢复 =====

    [Fact]
    public async Task CorruptedWal_ShouldRecoverGracefully()
    {
        var path = TempDb();
        var walPath = path + "-wal";

        // 第一阶段：写入并检查点
        {
            using var db = new VectorLiteDB(path);
            var coll = db.GetOrCreateCollection("wal_corrupt", 4);
            await coll.InsertAsync(new VectorRecord
            {
                Vector = new float[] { 1, 2, 3, 4 },
                Text = "已检查点数据"
            });
            db.Checkpoint();
        }

        // 第二阶段：写入新数据但不检查点，然后损坏 WAL
        {
            using var db = new VectorLiteDB(path);
            var coll = db.GetOrCreateCollection("wal_corrupt", 4);
            await coll.InsertAsync(new VectorRecord
            {
                Vector = new float[] { 5, 6, 7, 8 },
                Text = "未检查点数据"
            });
            // 不调用 Checkpoint，Dispose 会尝试检查点
        }

        // 损坏 WAL 文件（追加垃圾数据）
        if (File.Exists(walPath))
        {
            var rng = new Random(42);
            var garbage = new byte[256];
            rng.NextBytes(garbage);
            using var fs = new FileStream(walPath, FileMode.Append, FileAccess.Write);
            fs.Write(garbage, 0, garbage.Length);
        }

        // 第三阶段：重新打开，应能恢复已检查点的数据
        var act = () =>
        {
            using var db = new VectorLiteDB(path);
            var coll = db.GetOrCreateCollection("wal_corrupt", 4);
            // 至少已检查点的第一条记录应存在
            coll.Count.Should().BeGreaterThanOrEqualTo(1);
        };

        // 应不抛致命异常（WAL 截断恢复）
        act.Should().NotThrow();
    }

    // ===== 交错写入和检查点 =====

    [Fact]
    public async Task InterleavedInsertsAndCheckpoints_ShouldPreserveAllData()
    {
        var path = TempDb();
        using var db = new VectorLiteDB(path);
        var coll = db.GetOrCreateCollection("interleave", 4);

        // 交错插入和检查点
        await coll.InsertAsync(new VectorRecord { Vector = new float[] { 1, 0, 0, 0 }, Text = "A" });
        db.Checkpoint();

        await coll.InsertAsync(new VectorRecord { Vector = new float[] { 0, 1, 0, 0 }, Text = "B" });
        await coll.InsertAsync(new VectorRecord { Vector = new float[] { 0, 0, 1, 0 }, Text = "C" });
        db.Checkpoint();

        await coll.DeleteAsync(1); // 删除 A
        db.Checkpoint();

        await coll.InsertAsync(new VectorRecord { Vector = new float[] { 0, 0, 0, 1 }, Text = "D" });
        // 最后不检查点

        coll.Count.Should().Be(3); // B, C, D

        // 重新打开
        db.Dispose();
        using var db2 = new VectorLiteDB(path);
        var coll2 = db2.GetOrCreateCollection("interleave", 4);
        coll2.Count.Should().Be(3);
    }

    // ===== 删除后重新使用集合 =====

    [Fact]
    public async Task DeleteAndRecreateCollection_ShouldWork()
    {
        var path = TempDb();
        {
            using var db = new VectorLiteDB(path);
            var coll = db.GetOrCreateCollection("ephemeral", 4);
            await coll.InsertAsync(new VectorRecord { Vector = new float[] { 1, 1, 1, 1 } });
            db.Checkpoint();

            db.DeleteCollection("ephemeral");
            db.Checkpoint();

            // 重新创建同名集合
            var coll2 = db.GetOrCreateCollection("ephemeral", 4);
            await coll2.InsertAsync(new VectorRecord { Vector = new float[] { 2, 2, 2, 2 }, Text = "新数据" });
        }

        // 重新打开
        {
            using var db = new VectorLiteDB(path);
            var coll = db.GetOrCreateCollection("ephemeral", 4);
            coll.Count.Should().Be(1);
            var r = await coll.GetAsync(1);
            r!.Text.Should().Be("新数据");
        }
    }

    // ===== Upsert 持久化 =====

    [Fact]
    public async Task Upsert_ShouldPersistAcrossRestart()
    {
        var path = TempDb();
        {
            using var db = new VectorLiteDB(path);
            var coll = db.GetOrCreateCollection("upsert_persist", 4);
            await coll.UpsertAsync(new VectorRecord
            {
                Vector = new float[] { 1, 0, 0, 0 },
                Metadata = new() { ["name"] = "alice", ["ver"] = 1L }
            }, "name");

            await coll.UpsertAsync(new VectorRecord
            {
                Vector = new float[] { 0, 1, 0, 0 },
                Metadata = new() { ["name"] = "alice", ["ver"] = 2L }
            }, "name");
        }

        {
            using var db = new VectorLiteDB(path);
            var coll = db.GetOrCreateCollection("upsert_persist", 4);
            coll.Count.Should().Be(1);

            // 查找最新版本
            var results = await coll.Query(new float[] { 0, 1, 0, 0 }).TopK(1).ToListAsync();
            results.Should().HaveCount(1);
            results[0].Record.Metadata!["ver"].Should().Be(2L);
        }
    }

    // ===== 混合查询持久化 =====

    [Fact]
    public async Task HybridQuery_ShouldWorkAfterReopen()
    {
        var path = TempDb();
        {
            using var db = new VectorLiteDB(path);
            var coll = db.GetOrCreateCollection("hybrid", 4);
            await coll.InsertAsync(new VectorRecord
            {
                Vector = new float[] { 1, 0, 0, 0 },
                Metadata = new() { ["type"] = "doc" }
            });
            await coll.InsertAsync(new VectorRecord
            {
                Vector = new float[] { 0.9f, 0.1f, 0, 0 },
                Metadata = new() { ["type"] = "note" }
            });
            await coll.InsertAsync(new VectorRecord
            {
                Vector = new float[] { 0, 0, 0, 1 },
                Metadata = new() { ["type"] = "doc" }
            });
        }

        {
            using var db = new VectorLiteDB(path);
            var coll = db.GetOrCreateCollection("hybrid", 4);

            var results = await coll.Query(new float[] { 1, 0, 0, 0 })
                .TopK(10)
                .Where("type", "doc")
                .ToListAsync();

            results.Should().HaveCount(2);
            results.Should().OnlyContain(r => (string)r.Record.Metadata!["type"] == "doc");
        }
    }

    // ===== 空集合检查点 =====

    [Fact]
    public void EmptyDB_Checkpoint_ShouldNotThrow()
    {
        var path = TempDb();
        using var db = new VectorLiteDB(path);
        var act = () => db.Checkpoint();
        act.Should().NotThrow();
    }

    [Fact]
    public async Task EmptyCollection_Checkpoint_ShouldNotThrow()
    {
        var path = TempDb();
        using var db = new VectorLiteDB(path);
        db.GetOrCreateCollection("empty", 4);

        var act = () => db.Checkpoint();
        act.Should().NotThrow();
    }

    // ===== 并发检查点 =====

    [Fact]
    public async Task ConcurrentCheckpoints_ShouldNotCorrupt()
    {
        var path = TempDb();
        using var db = new VectorLiteDB(path);
        var coll = db.GetOrCreateCollection("concurrent_ckpt", 4);

        // 预填充数据
        for (int i = 0; i < 30; i++)
        {
            await coll.InsertAsync(new VectorRecord
            {
                Vector = new float[] { i, i * 0.1f, 0, 0 },
                Metadata = new() { ["i"] = (long)i }
            });
        }

        // 并发执行检查点
        var tasks = Enumerable.Range(0, 5).Select(_ => Task.Run(() =>
        {
            try { db.Checkpoint(); } catch { }
        }));
        await Task.WhenAll(tasks);

        // 数据应完整
        coll.Count.Should().Be(30);
    }

    // ===== 大量删除后检查点 =====

    [Fact]
    public async Task MassDeleteThenCheckpoint_ShouldWork()
    {
        var path = TempDb();
        using var db = new VectorLiteDB(path);
        var coll = db.GetOrCreateCollection("mass_delete", 4);

        // 插入 50 条
        for (int i = 0; i < 50; i++)
        {
            await coll.InsertAsync(new VectorRecord
            {
                Vector = new float[] { i, 0, 0, 0 }
            });
        }

        // 删除 40 条
        for (ulong i = 1; i <= 40; i++)
        {
            await coll.DeleteAsync(i);
        }

        coll.Count.Should().Be(10);
        db.Checkpoint();

        // 重新打开
        db.Dispose();
        using var db2 = new VectorLiteDB(path);
        var coll2 = db2.GetOrCreateCollection("mass_delete", 4);
        coll2.Count.Should().Be(10);
    }

    // ===== 不同维度集合 =====

    [Fact]
    public async Task DifferentDimensionCollections_ShouldCoexist()
    {
        var path = TempDb();
        {
            using var db = new VectorLiteDB(path);
            var c4 = db.GetOrCreateCollection("dim4", 4);
            var c8 = db.GetOrCreateCollection("dim8", 8);
            var c16 = db.GetOrCreateCollection("dim16", 16);

            await c4.InsertAsync(new VectorRecord { Vector = new float[4].Select((_, i) => (float)i).ToArray() });
            await c8.InsertAsync(new VectorRecord { Vector = new float[8].Select((_, i) => (float)i).ToArray() });
            await c16.InsertAsync(new VectorRecord { Vector = new float[16].Select((_, i) => (float)i).ToArray() });
        }

        {
            using var db = new VectorLiteDB(path);
            db.GetCollectionNames().Should().HaveCount(3);
            var c4 = db.GetOrCreateCollection("dim4", 4);
            var c16 = db.GetOrCreateCollection("dim16", 16);
            c4.Count.Should().Be(1);
            c16.Count.Should().Be(1);
        }
    }

    // ===== 元数据类型保留 =====

    [Fact]
    public async Task MetadataTypes_ShouldSurviveCheckpointAndReload()
    {
        var path = TempDb();
        {
            using var db = new VectorLiteDB(path);
            var coll = db.GetOrCreateCollection("meta_types", 4);
            await coll.InsertAsync(new VectorRecord
            {
                Vector = new float[] { 1, 0, 0, 0 },
                Metadata = new()
                {
                    ["str"] = "hello",
                    ["num"] = 42L,
                    ["dbl"] = 3.14,
                    ["flag"] = true
                }
            });
        }

        {
            using var db = new VectorLiteDB(path);
            var coll = db.GetOrCreateCollection("meta_types", 4);
            var r = await coll.GetAsync(1);
            r.Should().NotBeNull();
            r!.Metadata!["str"].Should().Be("hello");
            r.Metadata["num"].Should().Be(42L);
            ((double)r.Metadata["dbl"]).Should().BeApproximately(3.14, 0.001);
            r.Metadata["flag"].Should().Be(true);
        }
    }

    // ===== 多次打开关闭 =====

    [Fact]
    public async Task RepeatedOpenClose_ShouldNotCorruptData()
    {
        var path = TempDb();
        for (int round = 0; round < 5; round++)
        {
            using var db = new VectorLiteDB(path);
            var coll = db.GetOrCreateCollection("repeat", 4);
            await coll.InsertAsync(new VectorRecord
            {
                Vector = new float[] { round, 0, 0, 0 },
                Text = $"round_{round}"
            });
        }

        using var final = new VectorLiteDB(path);
        var c = final.GetOrCreateCollection("repeat", 4);
        c.Count.Should().Be(5);
    }

    public void Dispose()
    {
        foreach (var f in _tempFiles)
        {
            try { File.Delete(f); } catch { }
        }
    }
}
