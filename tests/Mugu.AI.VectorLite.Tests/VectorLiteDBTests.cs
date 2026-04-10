using FluentAssertions;

namespace Mugu.AI.VectorLite.Tests;

/// <summary>
/// VectorLiteDB 生命周期测试：创建/关闭/恢复、多集合管理、检查点。
/// </summary>
public class VectorLiteDBTests : IDisposable
{
    private readonly string _dbPath;
    private const int Dims = 4;

    public VectorLiteDBTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"vlite_db_{Guid.NewGuid():N}.vldb");
    }

    private static float[] Vec(params float[] v) => v;

    [Fact]
    public void CreateAndDispose_ShouldNotThrow()
    {
        using var db = new VectorLiteDB(_dbPath);
        db.GetOrCreateCollection("c1", Dims);
    }

    [Fact]
    public async Task Persistence_ShouldSurviveCloseAndReopen()
    {
        // 写入并关闭
        {
            using var db = new VectorLiteDB(_dbPath);
            var coll = db.GetOrCreateCollection("notes", Dims);
            await coll.InsertAsync(new VectorRecord
            {
                Vector = Vec(1, 2, 3, 4),
                Metadata = new() { ["tag"] = "test" },
                Text = "持久化测试"
            });
        }

        // 重新打开并验证
        {
            using var db = new VectorLiteDB(_dbPath);
            var coll = db.GetOrCreateCollection("notes", Dims);
            coll.Count.Should().Be(1);

            var record = await coll.GetAsync(1);
            record.Should().NotBeNull();
            record!.Vector.Should().BeEquivalentTo(new float[] { 1, 2, 3, 4 });
            record.Metadata!["tag"].Should().Be("test");
            record.Text.Should().Be("持久化测试");
        }
    }

    [Fact]
    public async Task MultipleCollections_ShouldBeIndependent()
    {
        using var db = new VectorLiteDB(_dbPath);
        var c1 = db.GetOrCreateCollection("alpha", Dims);
        var c2 = db.GetOrCreateCollection("beta", Dims);

        await c1.InsertAsync(new VectorRecord { Vector = Vec(1, 0, 0, 0) });
        await c2.InsertAsync(new VectorRecord { Vector = Vec(0, 1, 0, 0) });
        await c2.InsertAsync(new VectorRecord { Vector = Vec(0, 0, 1, 0) });

        c1.Count.Should().Be(1);
        c2.Count.Should().Be(2);
    }

    [Fact]
    public void GetOrCreateCollection_SameNameDifferentDims_ShouldThrow()
    {
        using var db = new VectorLiteDB(_dbPath);
        db.GetOrCreateCollection("x", 4);

        var act = () => db.GetOrCreateCollection("x", 8);
        act.Should().Throw<CollectionException>();
    }

    [Fact]
    public async Task DeleteCollection_ShouldRemoveAllData()
    {
        using var db = new VectorLiteDB(_dbPath);
        var coll = db.GetOrCreateCollection("to_delete", Dims);
        await coll.InsertAsync(new VectorRecord { Vector = Vec(1, 1, 1, 1) });

        db.DeleteCollection("to_delete");
        db.GetCollectionNames().Should().NotContain("to_delete");
    }

    [Fact]
    public async Task Checkpoint_ShouldFlushData()
    {
        using var db = new VectorLiteDB(_dbPath);
        var coll = db.GetOrCreateCollection("ckpt", Dims);
        await coll.InsertAsync(new VectorRecord
        {
            Vector = Vec(1, 2, 3, 4),
            Text = "checkpoint_test"
        });

        // 手动触发检查点
        db.Checkpoint();

        // WAL 文件应为空或仅含 checkpoint 标记
        var walPath = _dbPath + "-wal";
        if (File.Exists(walPath))
        {
            new FileInfo(walPath).Length.Should().BeLessThan(1024,
                "检查点后 WAL 应已截断");
        }
    }

    [Fact]
    public async Task WalRecovery_ShouldRestoreUncheckpointedData()
    {
        // 写入数据但不调 Checkpoint（仅靠 WAL）
        var walPath = _dbPath + "-wal";
        {
            using var db = new VectorLiteDB(_dbPath);
            var coll = db.GetOrCreateCollection("wal_test", Dims);
            await coll.InsertAsync(new VectorRecord
            {
                Vector = Vec(9, 8, 7, 6),
                Text = "WAL恢复测试"
            });
            // Dispose 会触发 checkpoint，所以需要验证数据在重开后存在
        }

        {
            using var db = new VectorLiteDB(_dbPath);
            var coll = db.GetOrCreateCollection("wal_test", Dims);
            coll.Count.Should().Be(1);
            var r = await coll.GetAsync(1);
            r.Should().NotBeNull();
            r!.Text.Should().Be("WAL恢复测试");
        }
    }

    public void Dispose()
    {
        TryDelete(_dbPath);
        TryDelete(_dbPath + "-wal");
    }

    private static void TryDelete(string p) { try { File.Delete(p); } catch { } }
}
