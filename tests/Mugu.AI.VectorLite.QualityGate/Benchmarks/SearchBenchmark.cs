using BenchmarkDotNet.Attributes;
using Mugu.AI.VectorLite.QualityGate.Infrastructure;

namespace Mugu.AI.VectorLite.QualityGate.Benchmarks;

/// <summary>向量搜索延迟基准</summary>
[MemoryDiagnoser]
[ShortRunJob]
public class SearchBenchmark
{
    private VectorLiteDB _db = null!;
    private ICollection _collection = null!;
    private float[][] _queries = null!;
    private string _dbPath = null!;
    private int _queryIndex;

    [Params(1000, 5000)]
    public int CorpusSize { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        const int dimensions = 128;
        _dbPath = Path.Combine(Path.GetTempPath(), $"vlite_bench_search_{Guid.NewGuid():N}.vldb");

        _db = new VectorLiteDB(_dbPath,
            new VectorLiteOptions { CheckpointInterval = Timeout.InfiniteTimeSpan });
        _collection = _db.GetOrCreateCollection("bench", dimensions);

        var records = TestDataGenerator.GenerateRecords(CorpusSize, dimensions);
        _collection.InsertBatchAsync(records).Wait();

        _queries = TestDataGenerator.GenerateRandomVectors(100, dimensions, seed: 99);
        _queryIndex = 0;
    }

    [Benchmark]
    public async Task<int> Search_Top10()
    {
        var query = _queries[_queryIndex % _queries.Length];
        _queryIndex++;
        var results = await _collection.Query(query).TopK(10).ToListAsync();
        return results.Count;
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _db?.Dispose();
        try { File.Delete(_dbPath); } catch { }
        try { File.Delete(_dbPath + "-wal"); } catch { }
    }
}
