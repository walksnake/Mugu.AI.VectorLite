using FluentAssertions;
using Mugu.AI.VectorLite.Engine;

namespace Mugu.AI.VectorLite.Tests;

/// <summary>纯标量集合、有序索引及持久化测试。</summary>
public sealed class ScalarOnlyCollectionTests : IDisposable
{
    private readonly string _path =
        Path.Combine(Path.GetTempPath(), $"vlite_scalar_only_{Guid.NewGuid():N}.vldb");

    [Fact]
    public async Task ScalarOnly_InsertQueryDeleteAndReopen_ShouldRemainConsistent()
    {
        using (var db = CreateDatabase())
        {
            var collection = db.GetOrCreateScalarCollection("events");
            await collection.InsertScalarBatchAsync(
            [
                Scalar("甲", 1L),
                Scalar("乙", 2L),
                Scalar("丙", 3L)
            ]);
            (await collection.DeleteAsync(2)).Should().BeTrue();
            collection.Count.Should().Be(2);
        }

        using var reopened = CreateDatabase();
        var loaded = reopened.GetOrCreateScalarCollection("events");
        loaded.Count.Should().Be(2);
        var records = await loaded.Filter()
            .OrderBy("sequence", SortDirection.Ascending)
            .Select(RecordProjection.Metadata | RecordProjection.Text)
            .TopK(10)
            .ToListAsync();

        records.Select(record => record.Text).Should().Equal("甲", "丙");
        records.Should().OnlyContain(record => record.Vector == null);
    }

    [Fact]
    public async Task ScalarOnly_WrongVectorApis_ShouldThrowCollectionException()
    {
        using var db = CreateDatabase();
        var collection = db.GetOrCreateScalarCollection("events");

        var insert = () => collection.InsertAsync(
            new VectorRecord { Vector = [1f] });
        var query = () => collection.Query([1f]);

        await insert.Should().ThrowAsync<CollectionException>();
        query.Should().Throw<CollectionException>();
    }

    [Fact]
    public async Task OrderedIndex_RangeAndDefinition_ShouldPersist()
    {
        using (var db = CreateDatabase())
        {
            var collection = db.GetOrCreateScalarCollection("events");
            await collection.InsertScalarBatchAsync(
                Enumerable.Range(0, 100).Select(i => Scalar($"事件{i}", i)));
            await collection.CreateScalarIndexAsync(new ScalarIndexDefinition
            {
                Name = "ix_sequence",
                Type = ScalarIndexType.Ordered,
                Fields = ["sequence"]
            });
        }

        using var reopened = CreateDatabase();
        var loaded = reopened.GetOrCreateScalarCollection("events");
        var definitions = await loaded.ListScalarIndexesAsync();
        var records = await loaded.Filter()
            .Where(new RangeFilter("sequence", 90.0, 95L, true, true))
            .OrderBy("sequence", SortDirection.Ascending)
            .Select(RecordProjection.Metadata)
            .TopK(10)
            .ToListAsync();

        definitions.Should().ContainSingle(definition => definition.Name == "ix_sequence");
        records.Select(record => Convert.ToInt64(record.Metadata!["sequence"]))
            .Should().Equal(90L, 91L, 92L, 93L, 94L, 95L);
    }

    [Fact]
    public async Task Cursor_WithDifferentSortDefinition_ShouldBeRejected()
    {
        using var db = CreateDatabase();
        var collection = db.GetOrCreateScalarCollection("events");
        await collection.InsertScalarBatchAsync([Scalar("甲", 1L), Scalar("乙", 2L)]);
        var first = await collection.Filter()
            .OrderBy("sequence", SortDirection.Ascending)
            .TopK(1)
            .ToPageAsync();

        var action = () => collection.Filter()
            .OrderBy("sequence", SortDirection.Descending)
            .After(first.NextCursor!)
            .TopK(1)
            .ToPageAsync();

        await action.Should().ThrowAsync<ArgumentException>();
    }

    private VectorLiteDB CreateDatabase()
        => new(_path, new VectorLiteOptions { CheckpointInterval = Timeout.InfiniteTimeSpan });

    private static ScalarRecord Scalar(string text, long sequence)
        => new()
        {
            Text = text,
            Metadata = new() { ["sequence"] = sequence, ["kind"] = "event" }
        };

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
