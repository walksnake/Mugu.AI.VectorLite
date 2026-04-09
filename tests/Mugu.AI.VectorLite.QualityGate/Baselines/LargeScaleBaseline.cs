using FluentAssertions;
using Mugu.AI.VectorLite.QualityGate.Infrastructure;

namespace Mugu.AI.VectorLite.QualityGate.Baselines;

/// <summary>
/// 大规模数据基线测试。
/// 验证在较大数据量下系统仍然正常工作。
/// </summary>
public class LargeScaleBaseline : IDisposable
{
    private readonly string _dbPath;

    public LargeScaleBaseline()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"vlite_scale_{Guid.NewGuid():N}.vldb");
    }

    [Fact]
    public async Task Should_Handle_5000_Records()
    {
        const int dimensions = 64;
        const int recordCount = 5000;

        using var db = new VectorLiteDB(_dbPath);
        var collection = db.GetOrCreateCollection("scale", dimensions);

        var records = TestDataGenerator.GenerateRecords(recordCount, dimensions);
        var ids = await collection.InsertBatchAsync(records);

        ids.Should().HaveCount(recordCount);
        collection.Count.Should().Be(recordCount);

        // 验证查询仍然有效
        var query = TestDataGenerator.GenerateRandomVector(dimensions);
        var results = await collection.Query(query).TopK(10).ToListAsync();

        results.Should().HaveCountGreaterThan(0);
        results.Should().HaveCountLessOrEqualTo(10);
        results.Should().BeInAscendingOrder(r => r.Distance);
    }

    [Fact]
    public async Task Multiple_Collections_Should_Be_Independent()
    {
        const int dimensions = 16;

        using var db = new VectorLiteDB(_dbPath + ".multi");

        var col1 = db.GetOrCreateCollection("collection_a", dimensions);
        var col2 = db.GetOrCreateCollection("collection_b", dimensions);

        await col1.InsertAsync(new VectorRecord
        {
            Vector = TestDataGenerator.GenerateRandomVector(dimensions),
            Text = "col1_record"
        });

        await col2.InsertAsync(new VectorRecord
        {
            Vector = TestDataGenerator.GenerateRandomVector(dimensions),
            Text = "col2_record"
        });

        col1.Count.Should().Be(1);
        col2.Count.Should().Be(1);

        db.GetCollectionNames().Should().HaveCount(2);
    }

    public void Dispose()
    {
        foreach (var suffix in new[] { "", ".multi" })
        {
            TryDeleteFile(_dbPath + suffix);
            TryDeleteFile(_dbPath + suffix + "-wal");
        }
    }

    private static void TryDeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}
