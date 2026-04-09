using BenchmarkDotNet.Attributes;
using Mugu.AI.VectorLite.Engine;
using Mugu.AI.VectorLite.QualityGate.Infrastructure;

namespace Mugu.AI.VectorLite.QualityGate.Benchmarks;

/// <summary>混合查询（标量过滤 + 向量搜索）性能基准</summary>
[MemoryDiagnoser]
[ShortRunJob]
public class HybridQueryBenchmark
{
    private VectorLiteDB _db = null!;
    private ICollection _collection = null!;
    private float[][] _queries = null!;
    private string _dbPath = null!;
    private int _queryIndex;

    [GlobalSetup]
    public void Setup()
    {
        const int dimensions = 128;
        const int corpusSize = 5000;
        _dbPath = Path.Combine(Path.GetTempPath(), $"vlite_bench_hybrid_{Guid.NewGuid():N}.vldb");

        _db = new VectorLiteDB(_dbPath,
            new VectorLiteOptions { CheckpointInterval = Timeout.InfiniteTimeSpan });
        _collection = _db.GetOrCreateCollection("bench", dimensions);

        var records = TestDataGenerator.GenerateRecords(corpusSize, dimensions);
        _collection.InsertBatchAsync(records).Wait();

        _queries = TestDataGenerator.GenerateRandomVectors(100, dimensions, seed: 99);
        _queryIndex = 0;
    }

    [Benchmark]
    public async Task<int> HybridQuery_EqualFilter()
    {
        var query = _queries[_queryIndex % _queries.Length];
        _queryIndex++;
        var results = await _collection.Query(query)
            .Where("category", "note")
            .TopK(10)
            .ToListAsync();
        return results.Count;
    }

    [Benchmark]
    public async Task<int> HybridQuery_RangeFilter()
    {
        var query = _queries[_queryIndex % _queries.Length];
        _queryIndex++;
        var results = await _collection.Query(query)
            .Where(new RangeFilter("importance", lowerBound: 3L, upperBound: 8L))
            .TopK(10)
            .ToListAsync();
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
