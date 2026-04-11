using FluentAssertions;

namespace Mugu.AI.VectorLite.Tests;

/// <summary>
/// WAL 恢复与持久化健壮性测试：覆盖崩溃恢复、多集合持久化、插入后删除恢复等场景。
/// </summary>
public class PersistenceAndRecoveryTests : IDisposable
{
    private readonly List<string> _tempFiles = new();
    private const int Dims = 4;

    private string TempDb()
    {
        var path = Path.Combine(Path.GetTempPath(), $"vlite_pers_{Guid.NewGuid():N}.vldb");
        _tempFiles.Add(path);
        _tempFiles.Add(path + "-wal");
        _tempFiles.Add(path + "-wal.tmp");
        return path;
    }

    private static float[] Vec(params float[] v) => v;

    [Fact]
    public async Task WalRecovery_InsertOnly_ShouldRestore()
    {
        var path = TempDb();

        // 阶段1：写入数据并正常关闭
        {
            using var db = new VectorLiteDB(path);
            var coll = db.GetOrCreateCollection("wal_r", Dims);
            await coll.InsertAsync(new VectorRecord { Vector = Vec(1, 2, 3, 4), Text = "恢复测试1" });
            await coll.InsertAsync(new VectorRecord { Vector = Vec(5, 6, 7, 8), Text = "恢复测试2" });
        }

        // 阶段2：重新打开验证数据
        {
            using var db = new VectorLiteDB(path);
            var coll = db.GetOrCreateCollection("wal_r", Dims);
            coll.Count.Should().Be(2);
            var r1 = await coll.GetAsync(1);
            r1!.Text.Should().Be("恢复测试1");
            var r2 = await coll.GetAsync(2);
            r2!.Text.Should().Be("恢复测试2");
        }
    }

    [Fact]
    public async Task WalRecovery_InsertThenDelete_ShouldRestore()
    {
        var path = TempDb();

        {
            using var db = new VectorLiteDB(path);
            var coll = db.GetOrCreateCollection("wal_del", Dims);
            await coll.InsertAsync(new VectorRecord { Vector = Vec(1, 0, 0, 0), Text = "keep" });
            var delId = await coll.InsertAsync(new VectorRecord { Vector = Vec(0, 1, 0, 0), Text = "remove" });
            await coll.DeleteAsync(delId);
        }

        {
            using var db = new VectorLiteDB(path);
            var coll = db.GetOrCreateCollection("wal_del", Dims);
            coll.Count.Should().Be(1);
            var r = await coll.GetAsync(1);
            r!.Text.Should().Be("keep");
        }
    }

    [Fact]
    public async Task Persistence_MultipleCollections_ShouldBeIndependent()
    {
        var path = TempDb();

        {
            using var db = new VectorLiteDB(path);
            var c1 = db.GetOrCreateCollection("alpha", 3);
            var c2 = db.GetOrCreateCollection("beta", 3);
            await c1.InsertAsync(new VectorRecord { Vector = new float[] { 1, 0, 0 }, Text = "a1" });
            await c2.InsertAsync(new VectorRecord { Vector = new float[] { 0, 1, 0 }, Text = "b1" });
            await c2.InsertAsync(new VectorRecord { Vector = new float[] { 0, 0, 1 }, Text = "b2" });
        }

        {
            using var db = new VectorLiteDB(path);
            var c1 = db.GetOrCreateCollection("alpha", 3);
            var c2 = db.GetOrCreateCollection("beta", 3);
            c1.Count.Should().Be(1);
            c2.Count.Should().Be(2);
            (await c1.GetAsync(1))!.Text.Should().Be("a1");
            (await c2.GetAsync(1))!.Text.Should().Be("b1");
        }
    }

    [Fact]
    public async Task Persistence_Metadata_ShouldSurvive()
    {
        var path = TempDb();

        {
            using var db = new VectorLiteDB(path);
            var coll = db.GetOrCreateCollection("meta", Dims);
            await coll.InsertAsync(new VectorRecord
            {
                Vector = Vec(1, 0, 0, 0),
                Metadata = new()
                {
                    ["string_val"] = "hello",
                    ["long_val"] = 42L,
                    ["double_val"] = 3.14,
                    ["bool_val"] = true
                }
            });
        }

        {
            using var db = new VectorLiteDB(path);
            var coll = db.GetOrCreateCollection("meta", Dims);
            var r = await coll.GetAsync(1);
            r!.Metadata!["string_val"].Should().Be("hello");
            r.Metadata["long_val"].Should().Be(42L);
            r.Metadata["double_val"].Should().Be(3.14);
            r.Metadata["bool_val"].Should().Be(true);
        }
    }

    [Fact]
    public async Task Persistence_MetadataFilter_ShouldWorkAfterReopen()
    {
        var path = TempDb();

        {
            using var db = new VectorLiteDB(path);
            var coll = db.GetOrCreateCollection("filter_persist", Dims);
            await coll.InsertAsync(new VectorRecord
            {
                Vector = Vec(1, 0, 0, 0),
                Metadata = new() { ["cat"] = "A" }
            });
            await coll.InsertAsync(new VectorRecord
            {
                Vector = Vec(0, 1, 0, 0),
                Metadata = new() { ["cat"] = "B" }
            });
        }

        {
            using var db = new VectorLiteDB(path);
            var coll = db.GetOrCreateCollection("filter_persist", Dims);
            var results = await coll.Query(Vec(1, 0, 0, 0))
                .TopK(10)
                .Where("cat", "A")
                .ToListAsync();
            results.Should().HaveCount(1);
            results[0].Record.Metadata!["cat"].Should().Be("A");
        }
    }

    [Fact]
    public async Task Persistence_SearchResultsConsistent()
    {
        var path = TempDb();

        {
            using var db = new VectorLiteDB(path);
            var coll = db.GetOrCreateCollection("search_persist", Dims);
            await coll.InsertAsync(new VectorRecord { Vector = Vec(1, 0, 0, 0), Text = "closest" });
            await coll.InsertAsync(new VectorRecord { Vector = Vec(0, 0, 0, 1), Text = "farthest" });
        }

        {
            using var db = new VectorLiteDB(path);
            var coll = db.GetOrCreateCollection("search_persist", Dims);
            var results = await coll.Query(Vec(1, 0, 0, 0)).TopK(2).ToListAsync();
            results.Should().HaveCount(2);
            results[0].Record.Text.Should().Be("closest");
        }
    }

    [Fact]
    public async Task CheckpointThenMore_ShouldPersistAll()
    {
        var path = TempDb();

        {
            using var db = new VectorLiteDB(path);
            var coll = db.GetOrCreateCollection("ckpt_mix", Dims);
            await coll.InsertAsync(new VectorRecord { Vector = Vec(1, 0, 0, 0), Text = "before_ckpt" });
            db.Checkpoint();
            await coll.InsertAsync(new VectorRecord { Vector = Vec(0, 1, 0, 0), Text = "after_ckpt" });
            // 关闭时再 checkpoint
        }

        {
            using var db = new VectorLiteDB(path);
            var coll = db.GetOrCreateCollection("ckpt_mix", Dims);
            coll.Count.Should().Be(2);
            (await coll.GetAsync(1))!.Text.Should().Be("before_ckpt");
            (await coll.GetAsync(2))!.Text.Should().Be("after_ckpt");
        }
    }

    [Fact]
    public async Task Upsert_ShouldPersistCorrectly()
    {
        var path = TempDb();

        {
            using var db = new VectorLiteDB(path);
            var coll = db.GetOrCreateCollection("upsert_persist", Dims);
            await coll.UpsertAsync(new VectorRecord
            {
                Vector = Vec(1, 0, 0, 0),
                Metadata = new() { ["name"] = "alice", ["version"] = 1L }
            }, "name");

            await coll.UpsertAsync(new VectorRecord
            {
                Vector = Vec(0, 1, 0, 0),
                Metadata = new() { ["name"] = "alice", ["version"] = 2L }
            }, "name");

            coll.Count.Should().Be(1);
        }

        {
            using var db = new VectorLiteDB(path);
            var coll = db.GetOrCreateCollection("upsert_persist", Dims);
            coll.Count.Should().Be(1);
            // 应是更新后的版本
            var ids = await coll.FindIdsByMetadataAsync("name", "alice");
            ids.Should().HaveCount(1);
            var r = await coll.GetAsync(ids[0]);
            r!.Metadata!["version"].Should().Be(2L);
        }
    }

    [Fact]
    public async Task BatchInsert_ShouldPersist()
    {
        var path = TempDb();

        {
            using var db = new VectorLiteDB(path);
            var coll = db.GetOrCreateCollection("batch", Dims);
            var records = Enumerable.Range(0, 20).Select(i => new VectorRecord
            {
                Vector = Vec(i, i, i, i),
                Metadata = new() { ["idx"] = (long)i }
            }).ToList();
            await coll.InsertBatchAsync(records);
        }

        {
            using var db = new VectorLiteDB(path);
            var coll = db.GetOrCreateCollection("batch", Dims);
            coll.Count.Should().Be(20);
        }
    }

    [Fact]
    public async Task DeleteCollection_ShouldNotPersist()
    {
        var path = TempDb();

        {
            using var db = new VectorLiteDB(path);
            var coll = db.GetOrCreateCollection("to_remove", Dims);
            await coll.InsertAsync(new VectorRecord { Vector = Vec(1, 0, 0, 0) });
            db.DeleteCollection("to_remove");
        }

        {
            using var db = new VectorLiteDB(path);
            db.CollectionExists("to_remove").Should().BeFalse();
            db.GetCollectionNames().Should().BeEmpty();
        }
    }

    // ===== P2-4：DeleteCollection 写 WAL 防止复活 =====

    [Fact]
    public async Task DeleteCollection_ShouldNotResurrectAfterWalRecovery()
    {
        // 不执行Checkpoint，通过WAL日志保留删除记录，验证重新打开后集合不复活
        var path = TempDb();

        {
            using var db = new VectorLiteDB(path, new VectorLiteOptions
                { CheckpointInterval = Timeout.InfiniteTimeSpan });
            var coll = db.GetOrCreateCollection("to_delete", Dims);
            await coll.InsertAsync(new VectorRecord { Vector = Vec(1, 2, 3, 4) });
            db.DeleteCollection("to_delete");
            // 不调用 Checkpoint，WAL 保留了 CollectionDelete 记录
        }

        // 重新打开，集合应不存在（WAL 恢复了删除操作）
        {
            using var db = new VectorLiteDB(path);
            db.CollectionExists("to_delete").Should().BeFalse();
        }
    }

    public void Dispose()
    {
        foreach (var f in _tempFiles)
        {
            try { File.Delete(f); } catch { }
        }
    }
}
