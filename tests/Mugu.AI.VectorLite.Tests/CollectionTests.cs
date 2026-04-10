using FluentAssertions;

namespace Mugu.AI.VectorLite.Tests;

/// <summary>
/// Collection API 单元测试：覆盖 CRUD、查询、Upsert、批量操作等核心路径。
/// </summary>
public class CollectionTests : IDisposable
{
    private readonly string _dbPath;
    private VectorLiteDB _db;
    private ICollection _coll;
    private const int Dims = 8;

    public CollectionTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"vlite_test_{Guid.NewGuid():N}.vldb");
        _db = new VectorLiteDB(_dbPath);
        _coll = _db.GetOrCreateCollection("test", Dims);
    }

    private static float[] Vec(params float[] v)
    {
        if (v.Length == Dims) return v;
        var result = new float[Dims];
        Array.Copy(v, result, Math.Min(v.Length, Dims));
        return result;
    }

    [Fact]
    public async Task InsertAsync_ShouldReturnIncrementingIds()
    {
        var id1 = await _coll.InsertAsync(new VectorRecord { Vector = Vec(1, 0, 0, 0, 0, 0, 0, 0) });
        var id2 = await _coll.InsertAsync(new VectorRecord { Vector = Vec(0, 1, 0, 0, 0, 0, 0, 0) });

        id1.Should().Be(1);
        id2.Should().Be(2);
        _coll.Count.Should().Be(2);
    }

    [Fact]
    public async Task GetAsync_ShouldReturnInsertedRecord()
    {
        var record = new VectorRecord
        {
            Vector = Vec(1, 2, 3, 4, 5, 6, 7, 8),
            Metadata = new() { ["key"] = "value", ["num"] = 42L },
            Text = "测试文本"
        };
        var id = await _coll.InsertAsync(record);

        var fetched = await _coll.GetAsync(id);
        fetched.Should().NotBeNull();
        fetched!.Id.Should().Be(id);
        fetched.Vector.Should().BeEquivalentTo(record.Vector);
        fetched.Metadata!["key"].Should().Be("value");
        fetched.Text.Should().Be("测试文本");
    }

    [Fact]
    public async Task GetAsync_NonExistentId_ShouldReturnNull()
    {
        var result = await _coll.GetAsync(999);
        result.Should().BeNull();
    }

    [Fact]
    public async Task DeleteAsync_ShouldRemoveRecord()
    {
        var id = await _coll.InsertAsync(new VectorRecord { Vector = Vec(1, 1, 1, 1, 1, 1, 1, 1) });
        _coll.Count.Should().Be(1);

        var deleted = await _coll.DeleteAsync(id);
        deleted.Should().BeTrue();
        _coll.Count.Should().Be(0);

        var fetched = await _coll.GetAsync(id);
        fetched.Should().BeNull();
    }

    [Fact]
    public async Task DeleteAsync_NonExistentId_ShouldReturnFalse()
    {
        var deleted = await _coll.DeleteAsync(999);
        deleted.Should().BeFalse();
    }

    [Fact]
    public async Task InsertBatchAsync_ShouldInsertAll()
    {
        var records = Enumerable.Range(0, 10).Select(i => new VectorRecord
        {
            Vector = Vec(i, i, i, i, i, i, i, i),
            Metadata = new() { ["idx"] = (long)i }
        }).ToList();

        var ids = await _coll.InsertBatchAsync(records);
        ids.Should().HaveCount(10);
        _coll.Count.Should().Be(10);
    }

    [Fact]
    public async Task InsertAsync_WrongDimensions_ShouldThrow()
    {
        var badRecord = new VectorRecord { Vector = new float[3] };
        var act = () => _coll.InsertAsync(badRecord);
        await act.Should().ThrowAsync<DimensionMismatchException>();
    }

    [Fact]
    public async Task UpsertAsync_ShouldReplaceExisting()
    {
        var r1 = new VectorRecord
        {
            Vector = Vec(1, 0, 0, 0, 0, 0, 0, 0),
            Metadata = new() { ["name"] = "alice", ["age"] = 25L }
        };
        var id1 = await _coll.UpsertAsync(r1, "name");
        _coll.Count.Should().Be(1);

        var r2 = new VectorRecord
        {
            Vector = Vec(0, 1, 0, 0, 0, 0, 0, 0),
            Metadata = new() { ["name"] = "alice", ["age"] = 30L }
        };
        var id2 = await _coll.UpsertAsync(r2, "name");

        // 旧记录应被删除，新记录插入
        _coll.Count.Should().Be(1);
        id2.Should().NotBe(id1);

        var fetched = await _coll.GetAsync(id2);
        fetched!.Metadata!["age"].Should().Be(30L);
    }

    [Fact]
    public async Task FindIdsByMetadataAsync_ShouldFilterCorrectly()
    {
        await _coll.InsertAsync(new VectorRecord
        {
            Vector = Vec(1, 0, 0, 0, 0, 0, 0, 0),
            Metadata = new() { ["type"] = "doc" }
        });
        await _coll.InsertAsync(new VectorRecord
        {
            Vector = Vec(0, 1, 0, 0, 0, 0, 0, 0),
            Metadata = new() { ["type"] = "note" }
        });
        await _coll.InsertAsync(new VectorRecord
        {
            Vector = Vec(0, 0, 1, 0, 0, 0, 0, 0),
            Metadata = new() { ["type"] = "doc" }
        });

        var ids = await _coll.FindIdsByMetadataAsync("type", "doc");
        ids.Should().HaveCount(2);
    }

    [Fact]
    public async Task Query_TopK_ShouldReturnClosestVectors()
    {
        var target = Vec(1, 0, 0, 0, 0, 0, 0, 0);
        var close = Vec(0.9f, 0.1f, 0, 0, 0, 0, 0, 0);
        var far = Vec(0, 0, 0, 0, 0, 0, 0, 1);

        await _coll.InsertAsync(new VectorRecord { Vector = far, Text = "far" });
        await _coll.InsertAsync(new VectorRecord { Vector = close, Text = "close" });

        var results = await _coll.Query(target).TopK(1).ToListAsync();
        results.Should().HaveCount(1);
        results[0].Record.Text.Should().Be("close");
    }

    [Fact]
    public async Task Query_WithMetadataFilter_ShouldApply()
    {
        await _coll.InsertAsync(new VectorRecord
        {
            Vector = Vec(1, 0, 0, 0, 0, 0, 0, 0),
            Metadata = new() { ["cat"] = "A" }
        });
        await _coll.InsertAsync(new VectorRecord
        {
            Vector = Vec(0.9f, 0.1f, 0, 0, 0, 0, 0, 0),
            Metadata = new() { ["cat"] = "B" }
        });

        var results = await _coll.Query(Vec(1, 0, 0, 0, 0, 0, 0, 0))
            .TopK(10)
            .Where("cat", "A")
            .ToListAsync();

        results.Should().HaveCount(1);
        results[0].Record.Metadata!["cat"].Should().Be("A");
    }

    [Fact]
    public async Task ConcurrentReadWrite_ShouldNotThrow()
    {
        // 预填充数据
        for (int i = 0; i < 50; i++)
        {
            await _coll.InsertAsync(new VectorRecord
            {
                Vector = Vec(i, i, i, i, i, i, i, i),
                Metadata = new() { ["i"] = (long)i }
            });
        }

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));

        var writeTask = Task.Run(async () =>
        {
            var j = 100;
            while (!cts.IsCancellationRequested)
            {
                await _coll.InsertAsync(new VectorRecord
                {
                    Vector = Vec(j, j, j, j, j, j, j, j)
                });
                j++;
                await Task.Delay(5);
            }
        });

        var readTask = Task.Run(async () =>
        {
            while (!cts.IsCancellationRequested)
            {
                await _coll.Query(Vec(1, 1, 1, 1, 1, 1, 1, 1)).TopK(3).ToListAsync();
                await Task.Delay(5);
            }
        });

        var act = () => Task.WhenAll(writeTask, readTask);
        await act.Should().NotThrowAsync();
    }

    public void Dispose()
    {
        _db?.Dispose();
        TryDelete(_dbPath);
        TryDelete(_dbPath + "-wal");
    }

    private static void TryDelete(string p) { try { File.Delete(p); } catch { } }
}
