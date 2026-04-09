using FluentAssertions;
using Mugu.AI.VectorLite.Engine;
using Mugu.AI.VectorLite.QualityGate.Infrastructure;

namespace Mugu.AI.VectorLite.QualityGate.Baselines;

/// <summary>
/// 标量过滤基线测试。
/// 验证元数据过滤（Equal、In、Range、组合）的正确性。
/// </summary>
public class ScalarFilterBaseline : IDisposable
{
    private readonly string _dbPath;

    public ScalarFilterBaseline()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"vlite_filter_{Guid.NewGuid():N}.vldb");
    }

    [Fact]
    public async Task Equal_Filter_Should_Return_Matching_Records()
    {
        const int dimensions = 16;

        using var db = new VectorLiteDB(_dbPath);
        var collection = db.GetOrCreateCollection("filter", dimensions);

        var records = TestDataGenerator.GenerateRecords(50, dimensions);
        await collection.InsertBatchAsync(records);

        // 查询 category == "note"
        var query = TestDataGenerator.GenerateRandomVector(dimensions);
        var results = await collection.Query(query)
            .Where("category", "note")
            .TopK(50)
            .ToListAsync();

        results.Should().AllSatisfy(r =>
            r.Record.Metadata!["category"].Should().Be("note"));
    }

    [Fact]
    public async Task Range_Filter_Should_Return_Records_In_Range()
    {
        const int dimensions = 16;

        using var db = new VectorLiteDB(_dbPath + ".range");
        var collection = db.GetOrCreateCollection("range", dimensions);

        var records = TestDataGenerator.GenerateRecords(50, dimensions);
        await collection.InsertBatchAsync(records);

        var query = TestDataGenerator.GenerateRandomVector(dimensions);
        var results = await collection.Query(query)
            .Where(new RangeFilter("importance", lowerBound: 3L, upperBound: 7L,
                lowerInclusive: true, upperInclusive: false))
            .TopK(50)
            .ToListAsync();

        results.Should().AllSatisfy(r =>
        {
            var importance = (long)r.Record.Metadata!["importance"];
            importance.Should().BeGreaterOrEqualTo(3).And.BeLessThan(7);
        });
    }

    [Fact]
    public async Task Combined_Filter_Should_Intersect_Conditions()
    {
        const int dimensions = 16;

        using var db = new VectorLiteDB(_dbPath + ".combined");
        var collection = db.GetOrCreateCollection("combined", dimensions);

        var records = TestDataGenerator.GenerateRecords(100, dimensions);
        await collection.InsertBatchAsync(records);

        var query = TestDataGenerator.GenerateRandomVector(dimensions);
        var results = await collection.Query(query)
            .Where("category", "email")
            .Where(new RangeFilter("importance", lowerBound: 5L))
            .TopK(100)
            .ToListAsync();

        results.Should().AllSatisfy(r =>
        {
            r.Record.Metadata!["category"].Should().Be("email");
            ((long)r.Record.Metadata!["importance"]).Should().BeGreaterOrEqualTo(5);
        });
    }

    public void Dispose()
    {
        foreach (var suffix in new[] { "", ".range", ".combined" })
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
