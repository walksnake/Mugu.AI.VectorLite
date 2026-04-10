using FluentAssertions;
using Mugu.AI.VectorLite.Engine;
using Mugu.AI.VectorLite.Engine.Distance;

namespace Mugu.AI.VectorLite.Tests;

/// <summary>
/// 引擎层测试：距离计算、HNSW 索引准确性。
/// </summary>
public class EngineTests : IDisposable
{
    private readonly string _dbPath;
    private const int Dims = 4;

    public EngineTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"vlite_engine_{Guid.NewGuid():N}.vldb");
    }

    [Fact]
    public async Task CosineSearch_ShouldRankByCosineSimilarity()
    {
        using var db = new VectorLiteDB(_dbPath, new VectorLiteOptions
        {
            DefaultDistanceMetric = DistanceMetric.Cosine
        });
        var coll = db.GetOrCreateCollection("cosine", Dims);

        // 插入平行和正交向量
        await coll.InsertAsync(new VectorRecord { Vector = new float[] { 1, 0, 0, 0 }, Text = "exact" });
        await coll.InsertAsync(new VectorRecord { Vector = new float[] { 0.9f, 0.1f, 0, 0 }, Text = "close" });
        await coll.InsertAsync(new VectorRecord { Vector = new float[] { 0, 1, 0, 0 }, Text = "orthogonal" });
        await coll.InsertAsync(new VectorRecord { Vector = new float[] { -1, 0, 0, 0 }, Text = "opposite" });

        var results = await coll.Query(new float[] { 1, 0, 0, 0 }).TopK(4).ToListAsync();

        results.Should().HaveCount(4);
        results[0].Record.Text.Should().Be("exact");
        results[0].Score.Should().BeApproximately(1.0f, 0.01f);
    }

    [Fact]
    public async Task EuclideanSearch_ShouldRankByEuclideanDistance()
    {
        using var db = new VectorLiteDB(_dbPath + ".euc", new VectorLiteOptions
        {
            DefaultDistanceMetric = DistanceMetric.Euclidean
        });
        var coll = db.GetOrCreateCollection("euclidean", Dims);

        await coll.InsertAsync(new VectorRecord { Vector = new float[] { 0, 0, 0, 0 }, Text = "origin" });
        await coll.InsertAsync(new VectorRecord { Vector = new float[] { 1, 0, 0, 0 }, Text = "near" });
        await coll.InsertAsync(new VectorRecord { Vector = new float[] { 10, 10, 10, 10 }, Text = "far" });

        var results = await coll.Query(new float[] { 0.5f, 0, 0, 0 }).TopK(3).ToListAsync();
        results[0].Record.Text.Should().BeOneOf("origin", "near");
        results[2].Record.Text.Should().Be("far");
    }

    [Fact]
    public async Task ScalarFilter_ExactMatch_ShouldWork()
    {
        using var db = new VectorLiteDB(_dbPath + ".sf");
        var coll = db.GetOrCreateCollection("filter", Dims);

        for (int i = 0; i < 20; i++)
        {
            await coll.InsertAsync(new VectorRecord
            {
                Vector = new float[] { i, 0, 0, 0 },
                Metadata = new() { ["group"] = i < 10 ? "A" : "B" }
            });
        }

        var results = await coll.Query(new float[] { 5, 0, 0, 0 })
            .TopK(100)
            .Where("group", "A")
            .ToListAsync();

        results.Should().HaveCount(10);
        results.Should().OnlyContain(r => r.Record.Metadata!["group"].ToString() == "A");
    }

    [Fact]
    public async Task ScalarFilter_RangeQuery_ShouldWork()
    {
        using var db = new VectorLiteDB(_dbPath + ".rg");
        var coll = db.GetOrCreateCollection("range", Dims);

        for (int i = 0; i < 20; i++)
        {
            await coll.InsertAsync(new VectorRecord
            {
                Vector = new float[] { i, 0, 0, 0 },
                Metadata = new() { ["score"] = (long)i }
            });
        }

        var results = await coll.Query(new float[] { 10, 0, 0, 0 })
            .TopK(100)
            .Where(new RangeFilter("score", 5L, 15L, lowerInclusive: true, upperInclusive: true))
            .ToListAsync();

        results.Should().HaveCount(11); // 5..15 inclusive
        results.Should().OnlyContain(r => (long)r.Record.Metadata!["score"] >= 5 && (long)r.Record.Metadata!["score"] <= 15);
    }

    [Fact]
    public async Task DeletedRecords_ShouldNotAppearInSearch()
    {
        using var db = new VectorLiteDB(_dbPath + ".del");
        var coll = db.GetOrCreateCollection("del_test", Dims);

        var id = await coll.InsertAsync(new VectorRecord
        {
            Vector = new float[] { 1, 0, 0, 0 },
            Text = "to_delete"
        });
        await coll.InsertAsync(new VectorRecord
        {
            Vector = new float[] { 0.9f, 0, 0, 0 },
            Text = "keep"
        });

        await coll.DeleteAsync(id);

        var results = await coll.Query(new float[] { 1, 0, 0, 0 }).TopK(10).ToListAsync();
        results.Should().HaveCount(1);
        results[0].Record.Text.Should().Be("keep");
    }

    public void Dispose()
    {
        foreach (var suffix in new[] { "", ".euc", ".sf", ".rg", ".del" })
        {
            TryDelete(_dbPath + suffix);
            TryDelete(_dbPath + suffix + "-wal");
        }
    }

    private static void TryDelete(string p) { try { File.Delete(p); } catch { } }
}
