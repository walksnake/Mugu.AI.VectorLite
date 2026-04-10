using FluentAssertions;
using Mugu.AI.VectorLite.Engine;
using Mugu.AI.VectorLite.Engine.Distance;

namespace Mugu.AI.VectorLite.Tests;

/// <summary>
/// VectorLiteOptions 验证测试和 API 层边界条件测试。
/// </summary>
public class OptionsAndEdgeCaseTests : IDisposable
{
    private readonly List<string> _tempFiles = new();
    private const int Dims = 4;

    private string TempDb()
    {
        var path = Path.Combine(Path.GetTempPath(), $"vlite_opt_{Guid.NewGuid():N}.vldb");
        _tempFiles.Add(path);
        _tempFiles.Add(path + "-wal");
        return path;
    }

    // ===== VectorLiteOptions 验证 =====

    [Fact]
    public void Options_Default_ShouldBeValid()
    {
        var opts = new VectorLiteOptions();
        var act = () => opts.Validate();
        act.Should().NotThrow();
    }

    [Fact]
    public void Options_InvalidPageSize_ShouldThrow()
    {
        var act = () => new VectorLiteOptions { PageSize = 1000 }.Validate();
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Options_PageSizeNotMultipleOf4096_ShouldThrow()
    {
        var act = () => new VectorLiteOptions { PageSize = 5000 }.Validate();
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Options_ZeroMaxDimensions_ShouldThrow()
    {
        var act = () => new VectorLiteOptions { MaxDimensions = 0 }.Validate();
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Options_ExcessiveMaxDimensions_ShouldThrow()
    {
        var act = () => new VectorLiteOptions { MaxDimensions = 200_000 }.Validate();
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Options_HnswM_TooLow_ShouldThrow()
    {
        var act = () => new VectorLiteOptions { HnswM = 1 }.Validate();
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Options_HnswM_TooHigh_ShouldThrow()
    {
        var act = () => new VectorLiteOptions { HnswM = 256 }.Validate();
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Options_EfConstruction_TooHigh_ShouldThrow()
    {
        var act = () => new VectorLiteOptions { HnswEfConstruction = 5000 }.Validate();
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Options_EfSearch_TooLow_ShouldThrow()
    {
        var act = () => new VectorLiteOptions { HnswEfSearch = 0 }.Validate();
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Options_EfSearch_TooHigh_ShouldThrow()
    {
        var act = () => new VectorLiteOptions { HnswEfSearch = 3000 }.Validate();
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Options_HnswM_BoundaryValues_ShouldBeValid()
    {
        new VectorLiteOptions { HnswM = 2 }.Validate();   // 最小合法值
        new VectorLiteOptions { HnswM = 128 }.Validate();  // 最大合法值
    }

    // ===== 空集合查询 =====

    [Fact]
    public async Task EmptyCollection_Query_ShouldReturnEmpty()
    {
        using var db = new VectorLiteDB(TempDb());
        var coll = db.GetOrCreateCollection("empty", Dims);

        var results = await coll.Query(new float[] { 1, 0, 0, 0 }).TopK(10).ToListAsync();
        results.Should().BeEmpty();
    }

    [Fact]
    public async Task EmptyCollection_Count_ShouldBeZero()
    {
        using var db = new VectorLiteDB(TempDb());
        var coll = db.GetOrCreateCollection("empty", Dims);
        coll.Count.Should().Be(0);
    }

    [Fact]
    public async Task EmptyCollection_Get_ShouldReturnNull()
    {
        using var db = new VectorLiteDB(TempDb());
        var coll = db.GetOrCreateCollection("empty", Dims);
        var r = await coll.GetAsync(1);
        r.Should().BeNull();
    }

    // ===== NaN 向量 =====

    [Fact]
    public async Task InsertAndQuery_NaNVector_ShouldNotCorruptIndex()
    {
        using var db = new VectorLiteDB(TempDb());
        var coll = db.GetOrCreateCollection("nan_test", Dims);

        // 先插入正常向量
        await coll.InsertAsync(new VectorRecord { Vector = new float[] { 1, 0, 0, 0 }, Text = "normal" });

        // 使用 NaN 向量查询应返回结果但距离可能异常
        var results = await coll.Query(new float[] { float.NaN, 0, 0, 0 }).TopK(10).ToListAsync();
        // 应不抛异常，结果可能非空（距离为 2f 仍可排序）
    }

    // ===== 查询构建器 =====

    [Fact]
    public async Task QueryBuilder_TopK_Zero_ShouldThrow()
    {
        using var db = new VectorLiteDB(TempDb());
        var coll = db.GetOrCreateCollection("topk", Dims);
        await coll.InsertAsync(new VectorRecord { Vector = new float[] { 1, 0, 0, 0 } });

        var act = () => coll.Query(new float[] { 1, 0, 0, 0 }).TopK(0);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public async Task QueryBuilder_EfSearch_Zero_ShouldThrow()
    {
        using var db = new VectorLiteDB(TempDb());
        var coll = db.GetOrCreateCollection("ef", Dims);

        var act = () => coll.Query(new float[] { 1, 0, 0, 0 }).WithEfSearch(0);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public async Task QueryBuilder_WithMinScore_ShouldFilter()
    {
        using var db = new VectorLiteDB(TempDb());
        var coll = db.GetOrCreateCollection("minscore", Dims);

        await coll.InsertAsync(new VectorRecord
        {
            Vector = new float[] { 1, 0, 0, 0 },
            Text = "exact"
        });
        await coll.InsertAsync(new VectorRecord
        {
            Vector = new float[] { 0, 0, 0, 1 },
            Text = "distant"
        });

        var results = await coll.Query(new float[] { 1, 0, 0, 0 })
            .TopK(10)
            .WithMinScore(0.9f) // 仅返回 Score >= 0.9 的
            .ToListAsync();

        results.Should().HaveCount(1);
        results[0].Record.Text.Should().Be("exact");
    }

    // ===== 集合操作边界 =====

    [Fact]
    public void CollectionExists_NonExistent_ShouldReturnFalse()
    {
        using var db = new VectorLiteDB(TempDb());
        db.CollectionExists("nope").Should().BeFalse();
    }

    [Fact]
    public void GetCollectionNames_ShouldList()
    {
        using var db = new VectorLiteDB(TempDb());
        db.GetOrCreateCollection("a", 4);
        db.GetOrCreateCollection("b", 4);
        db.GetCollectionNames().Should().BeEquivalentTo(new[] { "a", "b" });
    }

    [Fact]
    public void DeleteCollection_NonExistent_ShouldNotThrow()
    {
        using var db = new VectorLiteDB(TempDb());
        var act = () => db.DeleteCollection("nope");
        act.Should().NotThrow();
    }

    // ===== 大文本读写 =====

    [Fact]
    public async Task LargeText_ShouldPersistCorrectly()
    {
        var largeText = new string('中', 50_000); // 50K 中文字符 ≈ 150KB
        var path = TempDb();

        {
            using var db = new VectorLiteDB(path);
            var coll = db.GetOrCreateCollection("large", Dims);
            await coll.InsertAsync(new VectorRecord
            {
                Vector = new float[] { 1, 0, 0, 0 },
                Text = largeText
            });
        }

        {
            using var db = new VectorLiteDB(path);
            var coll = db.GetOrCreateCollection("large", Dims);
            var r = await coll.GetAsync(1);
            r.Should().NotBeNull();
            r!.Text.Should().Be(largeText);
        }
    }

    // ===== 多次 Checkpoint =====

    [Fact]
    public async Task MultipleCheckpoints_ShouldNotLoseData()
    {
        using var db = new VectorLiteDB(TempDb());
        var coll = db.GetOrCreateCollection("ckpt_multi", Dims);

        await coll.InsertAsync(new VectorRecord { Vector = new float[] { 1, 0, 0, 0 }, Text = "first" });
        db.Checkpoint();

        await coll.InsertAsync(new VectorRecord { Vector = new float[] { 0, 1, 0, 0 }, Text = "second" });
        db.Checkpoint();

        await coll.InsertAsync(new VectorRecord { Vector = new float[] { 0, 0, 1, 0 }, Text = "third" });
        db.Checkpoint();

        coll.Count.Should().Be(3);
        var r = await coll.GetAsync(3);
        r!.Text.Should().Be("third");
    }

    // ===== HNSW 索引操作 =====

    [Fact]
    public void HNSWIndex_Delete_ShouldExcludeFromSearch()
    {
        var dist = DistanceFunctionFactory.Get(DistanceMetric.Cosine);
        var index = new HNSWIndex(dist, m: 8, efConstruction: 50);

        index.Insert(1, new float[] { 1, 0, 0, 0 });
        index.Insert(2, new float[] { 0.9f, 0.1f, 0, 0 });
        index.Insert(3, new float[] { 0, 1, 0, 0 });

        index.MarkDeleted(1);
        index.Count.Should().Be(2);

        var results = index.Search(new float[] { 1, 0, 0, 0 }, 3, efSearch: 50);
        results.Should().NotContain(r => r.RecordId == 1);
    }

    [Fact]
    public void HNSWIndex_NeedsCompaction_ShouldReturnTrue()
    {
        var dist = DistanceFunctionFactory.Get(DistanceMetric.Cosine);
        var index = new HNSWIndex(dist, m: 8, efConstruction: 50);

        // 插入 10 条，删除 3 条 → 30% > 20% 阈值
        for (var i = 1UL; i <= 10; i++)
            index.Insert(i, new float[] { i, 0, 0, 0 });
        for (var i = 1UL; i <= 3; i++)
            index.MarkDeleted(i);

        index.NeedsCompaction().Should().BeTrue();
    }

    [Fact]
    public void HNSWIndex_GetActiveNodeIds_ShouldExcludeDeleted()
    {
        var dist = DistanceFunctionFactory.Get(DistanceMetric.Cosine);
        var index = new HNSWIndex(dist, m: 8, efConstruction: 50);

        index.Insert(1, new float[] { 1, 0, 0, 0 });
        index.Insert(2, new float[] { 0, 1, 0, 0 });
        index.Insert(3, new float[] { 0, 0, 1, 0 });
        index.MarkDeleted(2);

        var activeIds = index.GetActiveNodeIds().ToList();
        activeIds.Should().BeEquivalentTo(new[] { 1UL, 3UL });
    }

    [Fact]
    public void HNSWIndex_EmptyIndex_ShouldReturnEmptySearch()
    {
        var dist = DistanceFunctionFactory.Get(DistanceMetric.Cosine);
        var index = new HNSWIndex(dist, m: 8, efConstruction: 50);

        var results = index.Search(new float[] { 1, 0, 0, 0 }, 5, efSearch: 50);
        results.Should().BeEmpty();
    }

    // ===== 不同距离度量创建集合 =====

    [Fact]
    public async Task DB_DotProductCollection_ShouldWork()
    {
        using var db = new VectorLiteDB(TempDb(), new VectorLiteOptions
        {
            DefaultDistanceMetric = DistanceMetric.DotProduct
        });
        var coll = db.GetOrCreateCollection("dotprod", Dims);

        await coll.InsertAsync(new VectorRecord { Vector = new float[] { 1, 1, 0, 0 } });
        await coll.InsertAsync(new VectorRecord { Vector = new float[] { 0, 0, 1, 1 } });

        var results = await coll.Query(new float[] { 1, 1, 0, 0 }).TopK(2).ToListAsync();
        results.Should().HaveCount(2);
    }

    // ===== SearchResult.Score =====

    [Fact]
    public void SearchResult_Score_ShouldBeOneMinusDistance()
    {
        var r = new SearchResult
        {
            Record = new VectorRecord { Id = 1, Vector = new float[] { 1 } },
            Distance = 0.3f
        };
        r.Score.Should().BeApproximately(0.7f, 0.001f);
    }

    // ===== FindIdsByMetadataAsync =====

    [Fact]
    public async Task FindIdsByMetadata_EmptyIndex_ShouldReturnEmpty()
    {
        using var db = new VectorLiteDB(TempDb());
        var coll = db.GetOrCreateCollection("find", Dims);
        var ids = await coll.FindIdsByMetadataAsync("key", "val");
        ids.Should().BeEmpty();
    }

    // ===== 异常类型层次结构 =====

    [Fact]
    public void ExceptionHierarchy_ShouldBeCorrect()
    {
        var vlEx = new VectorLiteException("test");
        vlEx.Should().BeAssignableTo<Exception>();

        var stEx = new StorageException("storage");
        stEx.Should().BeAssignableTo<VectorLiteException>();

        var cfEx = new CorruptedFileException("corrupted");
        cfEx.Should().BeAssignableTo<StorageException>();

        var wcEx = new WalCorruptedException("wal");
        wcEx.Should().BeAssignableTo<StorageException>();

        var pgEx = new PageException("page");
        pgEx.Should().BeAssignableTo<StorageException>();

        var dimEx = new DimensionMismatchException(3, 4);
        dimEx.Should().BeAssignableTo<VectorLiteException>();

        var collEx = new CollectionException("coll");
        collEx.Should().BeAssignableTo<VectorLiteException>();
    }

    public void Dispose()
    {
        foreach (var f in _tempFiles)
        {
            try { File.Delete(f); } catch { }
        }
    }
}
