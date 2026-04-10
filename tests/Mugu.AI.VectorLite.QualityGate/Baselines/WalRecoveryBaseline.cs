using FluentAssertions;
using Mugu.AI.VectorLite.QualityGate.Infrastructure;

namespace Mugu.AI.VectorLite.QualityGate.Baselines;

/// <summary>
/// WAL 崩溃恢复和持久化基线测试。
/// 验证数据库关闭并重新打开后数据不丢失。
/// </summary>
public class WalRecoveryBaseline : IDisposable
{
    private readonly string _dbPath;
    private readonly List<string> _tempFiles = new();

    public WalRecoveryBaseline()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"vlite_wal_{Guid.NewGuid():N}.vldb");
    }

    [Fact]
    public async Task Data_Should_Survive_Close_And_Reopen()
    {
        const int dimensions = 32;
        var dbFile = GetTempDb("survive");

        // 第一次打开：写入数据
        ulong insertedId;
        {
            using var db = new VectorLiteDB(dbFile);
            var collection = db.GetOrCreateCollection("recovery", dimensions);

            var record = new VectorRecord
            {
                Vector = TestDataGenerator.GenerateRandomVector(dimensions, new Random(42)),
                Metadata = new Dictionary<string, object> { ["tag"] = "test" },
                Text = "persistence_test"
            };
            insertedId = await collection.InsertAsync(record);
            insertedId.Should().BeGreaterThan(0);
        }

        // 第二次打开：验证数据完整恢复
        {
            using var db = new VectorLiteDB(dbFile);
            db.CollectionExists("recovery").Should().BeTrue("集合应当被持久化");

            var collection = db.GetOrCreateCollection("recovery", dimensions);
            collection.Count.Should().Be(1, "记录数量应恢复");

            var record = await collection.GetAsync(insertedId);
            record.Should().NotBeNull("记录应当被恢复");
            record!.Vector.Length.Should().Be(dimensions);
            record.Text.Should().Be("persistence_test");
            record.Metadata.Should().ContainKey("tag");
        }
    }

    [Fact]
    public async Task Checkpoint_Should_Persist_Data()
    {
        const int dimensions = 16;
        var dbFile = GetTempDb("ckpt");

        // 第一次打开：写入数据并执行检查点
        {
            using var db = new VectorLiteDB(dbFile);
            var collection = db.GetOrCreateCollection("ckpt_test", dimensions);

            for (var i = 0; i < 10; i++)
            {
                await collection.InsertAsync(new VectorRecord
                {
                    Vector = TestDataGenerator.GenerateRandomVector(dimensions),
                    Text = $"record_{i}"
                });
            }

            // 显式检查点
            db.Checkpoint();
        }

        // 第二次打开：验证数据仍在
        {
            using var db = new VectorLiteDB(dbFile);
            var collection = db.GetOrCreateCollection("ckpt_test", dimensions);
            collection.Count.Should().Be(10, "检查点后所有记录应当持久化");
        }
    }

    [Fact]
    public async Task Delete_Should_Persist_After_Reopen()
    {
        const int dimensions = 8;
        var dbFile = GetTempDb("delete");

        ulong id1, id2;
        {
            using var db = new VectorLiteDB(dbFile);
            var collection = db.GetOrCreateCollection("del_test", dimensions);

            id1 = await collection.InsertAsync(new VectorRecord
            {
                Vector = TestDataGenerator.GenerateRandomVector(dimensions, new Random(1)),
                Text = "keep"
            });
            id2 = await collection.InsertAsync(new VectorRecord
            {
                Vector = TestDataGenerator.GenerateRandomVector(dimensions, new Random(2)),
                Text = "delete_me"
            });

            (await collection.DeleteAsync(id2)).Should().BeTrue();
        }

        {
            using var db = new VectorLiteDB(dbFile);
            var collection = db.GetOrCreateCollection("del_test", dimensions);
            collection.Count.Should().Be(1);

            var kept = await collection.GetAsync(id1);
            kept.Should().NotBeNull();
            kept!.Text.Should().Be("keep");

            var deleted = await collection.GetAsync(id2);
            deleted.Should().BeNull("已删除记录不应恢复");
        }
    }

    [Fact]
    public async Task Multiple_Collections_Should_Persist()
    {
        const int dim1 = 16, dim2 = 32;
        var dbFile = GetTempDb("multi");

        {
            using var db = new VectorLiteDB(dbFile);
            var c1 = db.GetOrCreateCollection("alpha", dim1);
            var c2 = db.GetOrCreateCollection("beta", dim2);

            await c1.InsertAsync(new VectorRecord
            {
                Vector = TestDataGenerator.GenerateRandomVector(dim1),
                Text = "alpha_record"
            });

            for (var i = 0; i < 5; i++)
            {
                await c2.InsertAsync(new VectorRecord
                {
                    Vector = TestDataGenerator.GenerateRandomVector(dim2),
                    Text = $"beta_{i}"
                });
            }
        }

        {
            using var db = new VectorLiteDB(dbFile);
            db.GetCollectionNames().Should().BeEquivalentTo(new[] { "alpha", "beta" });

            var c1 = db.GetOrCreateCollection("alpha", dim1);
            c1.Count.Should().Be(1);

            var c2 = db.GetOrCreateCollection("beta", dim2);
            c2.Count.Should().Be(5);
        }
    }

    [Fact]
    public async Task Search_Should_Work_After_Reopen()
    {
        const int dimensions = 16;
        var dbFile = GetTempDb("search");
        var rng = new Random(123);

        var targetVector = TestDataGenerator.GenerateRandomVector(dimensions, new Random(999));

        {
            using var db = new VectorLiteDB(dbFile);
            var collection = db.GetOrCreateCollection("search_test", dimensions);

            // 插入目标向量
            await collection.InsertAsync(new VectorRecord
            {
                Vector = targetVector,
                Metadata = new Dictionary<string, object> { ["type"] = "target" },
                Text = "target"
            });

            // 插入噪声向量
            for (var i = 0; i < 20; i++)
            {
                await collection.InsertAsync(new VectorRecord
                {
                    Vector = TestDataGenerator.GenerateRandomVector(dimensions, rng),
                    Metadata = new Dictionary<string, object> { ["type"] = "noise" },
                    Text = $"noise_{i}"
                });
            }
        }

        {
            using var db = new VectorLiteDB(dbFile);
            var collection = db.GetOrCreateCollection("search_test", dimensions);
            collection.Count.Should().Be(21);

            // 搜索目标向量本身应当返回完全匹配
            var results = await collection.Query(targetVector).TopK(1).ToListAsync();
            results.Should().NotBeEmpty("恢复后搜索应当返回结果");
            results[0].Record.Text.Should().Be("target");
        }
    }

    public void Dispose()
    {
        foreach (var file in _tempFiles)
        {
            TryDeleteFile(file);
            TryDeleteFile(file + "-wal");
        }
        TryDeleteFile(_dbPath);
        TryDeleteFile(_dbPath + "-wal");
    }

    private string GetTempDb(string suffix)
    {
        var path = Path.Combine(Path.GetTempPath(), $"vlite_{suffix}_{Guid.NewGuid():N}.vldb");
        _tempFiles.Add(path);
        return path;
    }

    private static void TryDeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}
