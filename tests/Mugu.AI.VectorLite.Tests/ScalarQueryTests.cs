using FluentAssertions;
using Mugu.AI.VectorLite.Engine;

namespace Mugu.AI.VectorLite.Tests;

/// <summary>纯标量查询、投影、批量读取和稳定分页测试。</summary>
public sealed class ScalarQueryTests : IDisposable
{
    private readonly string _path =
        Path.Combine(Path.GetTempPath(), $"vlite_scalar_query_{Guid.NewGuid():N}.vldb");

    [Fact]
    public async Task Filter_CompositeExpressions_ShouldReturnExpectedRecords()
    {
        using var db = CreateDatabase();
        var collection = db.GetOrCreateCollection("items", 2);
        await InsertVectorRecords(collection);

        var filter = new AndFilter(
            new InFilter("status", ["active", "pending"]),
            new RangeFilter("priority", 2L, 5L, true, true),
            new NotFilter(new EqualFilter("category", "ignored")));

        var records = await collection.Filter()
            .Where(filter)
            .OrderBy("priority", SortDirection.Ascending)
            .Select(RecordProjection.Metadata)
            .TopK(10)
            .ToListAsync();

        records.Select(ReadPriority).Should().Equal(2L, 3L, 5L);
        records.Should().OnlyContain(record => record.Vector == null && record.Text == null);
    }

    [Fact]
    public async Task Filter_OrderAndTopK_ShouldUseStableMultiFieldOrder()
    {
        using var db = CreateDatabase();
        var collection = db.GetOrCreateCollection("items", 2);
        await InsertVectorRecords(collection);

        var records = await collection.Filter()
            .Where("status", "active")
            .OrderBy("priority", SortDirection.Descending)
            .ThenBy("updatedAt", SortDirection.Descending)
            .TopK(2)
            .Select(RecordProjection.Id)
            .ToListAsync();

        records.Select(record => record.Id).Should().Equal(4UL, 5UL);
        records.Should().OnlyContain(
            record => record.Vector == null && record.Metadata == null && record.Text == null);
    }

    [Fact]
    public async Task Filter_PaginationWithDuplicateKeys_ShouldHaveNoDuplicatesOrGaps()
    {
        using var db = CreateDatabase();
        var collection = db.GetOrCreateCollection("items", 2);
        await InsertVectorRecords(collection);

        var first = await collection.Filter()
            .OrderBy("status", SortDirection.Ascending)
            .ThenBy("priority", SortDirection.Descending)
            .TopK(2)
            .Select(RecordProjection.Id)
            .ToPageAsync();
        var second = await collection.Filter()
            .OrderBy("status", SortDirection.Ascending)
            .ThenBy("priority", SortDirection.Descending)
            .After(first.NextCursor!)
            .TopK(2)
            .Select(RecordProjection.Id)
            .ToPageAsync();
        var third = await collection.Filter()
            .OrderBy("status", SortDirection.Ascending)
            .ThenBy("priority", SortDirection.Descending)
            .After(second.NextCursor!)
            .TopK(2)
            .Select(RecordProjection.Id)
            .ToPageAsync();

        first.Records.Concat(second.Records).Concat(third.Records)
            .Select(record => record.Id)
            .Should().OnlyHaveUniqueItems().And.HaveCount(5);
        third.NextCursor.Should().BeNull();
    }

    [Fact]
    public async Task GetBatch_AfterReopen_ShouldRespectProjectionAndOrder()
    {
        ulong[] ids;
        using (var db = CreateDatabase())
        {
            var collection = db.GetOrCreateCollection("items", 2);
            ids = (await collection.InsertBatchAsync(
            [
                Vector("甲", 1L),
                Vector("乙", 2L)
            ])).ToArray();
        }

        using var reopened = CreateDatabase();
        var loaded = reopened.GetOrCreateCollection("items", 2);
        var records = await loaded.GetBatchAsync(
            ids.Reverse(),
            RecordProjection.Metadata | RecordProjection.Text);

        records.Select(record => record.Id).Should().Equal(ids.Reverse());
        records.Select(record => record.Text).Should().Equal("乙", "甲");
        records.Should().OnlyContain(record => record.Vector == null);
    }

    [Fact]
    public async Task Filter_CancelledToken_ShouldThrow()
    {
        using var db = CreateDatabase();
        var collection = db.GetOrCreateCollection("items", 2);
        using var source = new CancellationTokenSource();
        source.Cancel();

        var action = () => collection.Filter().ToListAsync(source.Token);

        await action.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task Filter_VectorRecordWithoutMetadata_ShouldStillReturnId()
    {
        using var db = CreateDatabase();
        var collection = db.GetOrCreateCollection("items", 2);
        var id = await collection.InsertAsync(new VectorRecord { Vector = [1f, 2f] });

        var records = await collection.Filter()
            .Select(RecordProjection.Id)
            .TopK(10)
            .ToListAsync();

        records.Should().ContainSingle(record => record.Id == id);
    }

    private VectorLiteDB CreateDatabase()
        => new(_path, new VectorLiteOptions { CheckpointInterval = Timeout.InfiniteTimeSpan });

    private static async Task InsertVectorRecords(ICollection collection)
    {
        await collection.InsertBatchAsync(
        [
            Vector("一", 1L, "inactive", "normal", 10L),
            Vector("二", 3L, "active", "normal", 20L),
            Vector("三", 2L, "pending", "normal", 30L),
            Vector("四", 5L, "active", "normal", 40L),
            Vector("五", 4L, "active", "ignored", 50L)
        ]);
    }

    private static VectorRecord Vector(
        string text,
        long priority,
        string status = "active",
        string category = "normal",
        long updatedAt = 0)
        => new()
        {
            Vector = [priority, updatedAt],
            Text = text,
            Metadata = new()
            {
                ["priority"] = priority,
                ["status"] = status,
                ["category"] = category,
                ["updatedAt"] = updatedAt
            }
        };

    private static long ReadPriority(RecordView record)
        => (long)record.Metadata!["priority"];

    public void Dispose()
    {
        TryDelete(_path);
        TryDelete(_path + "-wal");
        TryDelete(_path + "-wal.tmp");
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { }
    }
}
