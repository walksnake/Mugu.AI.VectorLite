using FluentAssertions;
using Mugu.AI.VectorLite.QualityGate.Infrastructure;

namespace Mugu.AI.VectorLite.QualityGate.Baselines;

/// <summary>
/// 数据完整性基线测试。
/// 验证插入的数据能被正确读取，删除后不再返回。
/// </summary>
public class DataIntegrityBaseline : IDisposable
{
    private readonly string _dbPath;

    public DataIntegrityBaseline()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"vlite_integrity_{Guid.NewGuid():N}.vldb");
    }

    [Fact]
    public async Task Insert_And_Get_Should_Return_Same_Data()
    {
        const int dimensions = 32;

        using var db = new VectorLiteDB(_dbPath);
        var collection = db.GetOrCreateCollection("integrity", dimensions);

        var vector = TestDataGenerator.GenerateRandomVector(dimensions);
        var record = new VectorRecord
        {
            Vector = vector,
            Metadata = new() { ["key1"] = "value1", ["key2"] = 42L },
            Text = "测试文本"
        };

        var id = await collection.InsertAsync(record);

        var retrieved = await collection.GetAsync(id);
        retrieved.Should().NotBeNull();
        retrieved!.Id.Should().Be(id);
        retrieved.Vector.Should().BeEquivalentTo(vector);
        retrieved.Text.Should().Be("测试文本");
        retrieved.Metadata!["key1"].Should().Be("value1");
        retrieved.Metadata!["key2"].Should().Be(42L);
    }

    [Fact]
    public async Task Delete_Should_Remove_Record()
    {
        const int dimensions = 32;

        using var db = new VectorLiteDB(_dbPath + ".del");
        var collection = db.GetOrCreateCollection("delete_test", dimensions);

        var record = new VectorRecord
        {
            Vector = TestDataGenerator.GenerateRandomVector(dimensions),
            Text = "to_delete"
        };

        var id = await collection.InsertAsync(record);
        collection.Count.Should().Be(1);

        var deleted = await collection.DeleteAsync(id);
        deleted.Should().BeTrue();

        var retrieved = await collection.GetAsync(id);
        retrieved.Should().BeNull();
    }

    [Fact]
    public async Task Batch_Insert_Should_Assign_Unique_Ids()
    {
        const int dimensions = 16;

        using var db = new VectorLiteDB(_dbPath + ".batch");
        var collection = db.GetOrCreateCollection("batch", dimensions);

        var records = TestDataGenerator.GenerateRecords(100, dimensions);
        var ids = await collection.InsertBatchAsync(records);

        ids.Should().HaveCount(100);
        ids.Distinct().Should().HaveCount(100); // 所有ID唯一
    }

    public void Dispose()
    {
        foreach (var suffix in new[] { "", ".del", ".batch" })
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
