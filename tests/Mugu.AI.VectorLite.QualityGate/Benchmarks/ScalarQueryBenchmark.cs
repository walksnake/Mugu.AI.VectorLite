using BenchmarkDotNet.Attributes;
using Mugu.AI.VectorLite.Engine;

namespace Mugu.AI.VectorLite.QualityGate.Benchmarks;

/// <summary>纯标量过滤、范围与 TopK 性能基准。</summary>
[MemoryDiagnoser]
[ShortRunJob]
public class ScalarQueryBenchmark
{
    private VectorLiteDB _db = null!;
    private ICollection _collection = null!;
    private string _dbPath = null!;

    /// <summary>基准数据规模。</summary>
    [Params(1_000, 10_000)]
    public int RecordCount { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _dbPath = Path.Combine(
            Path.GetTempPath(),
            $"vlite_bench_scalar_{Guid.NewGuid():N}.vldb");
        _db = new VectorLiteDB(
            _dbPath,
            new VectorLiteOptions { CheckpointInterval = Timeout.InfiniteTimeSpan });
        _collection = _db.GetOrCreateScalarCollection("records");
        var records = Enumerable.Range(0, RecordCount).Select(CreateRecord);
        _collection.InsertScalarBatchAsync(records).GetAwaiter().GetResult();
        _collection.CreateScalarIndexAsync(new ScalarIndexDefinition
        {
            Name = "ix_sequence",
            Type = ScalarIndexType.Ordered,
            Fields = ["sequence"]
        }).GetAwaiter().GetResult();
    }

    [Benchmark]
    public Task<IReadOnlyList<RecordView>> EqualTop20()
        => _collection.Filter()
            .Where("partition", "A")
            .OrderBy("priority", SortDirection.Descending)
            .TopK(20)
            .Select(RecordProjection.Metadata)
            .ToListAsync();

    [Benchmark]
    public Task<IReadOnlyList<RecordView>> OrderedRangeTop20()
        => _collection.Filter()
            .Where(new RangeFilter("sequence", RecordCount - 100L, RecordCount - 1L, true, true))
            .OrderBy("sequence", SortDirection.Descending)
            .TopK(20)
            .Select(RecordProjection.Metadata)
            .ToListAsync();

    [GlobalCleanup]
    public void Cleanup()
    {
        _db?.Dispose();
        TryDelete(_dbPath);
        TryDelete(_dbPath + "-wal");
    }

    private static ScalarRecord CreateRecord(int index)
        => new()
        {
            Text = $"记录{index}",
            Metadata = new()
            {
                ["partition"] = index % 5 == 0 ? "A" : "B",
                ["priority"] = (long)(index % 100),
                ["sequence"] = (long)index
            }
        };

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { }
    }
}
