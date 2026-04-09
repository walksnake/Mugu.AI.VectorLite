using BenchmarkDotNet.Attributes;
using Mugu.AI.VectorLite.QualityGate.Infrastructure;

namespace Mugu.AI.VectorLite.QualityGate.Benchmarks;

/// <summary>向量插入吞吐量基准</summary>
[MemoryDiagnoser]
[ShortRunJob]
public class InsertBenchmark
{
    private VectorRecord[] _records = null!;
    private string _dbPath = null!;

    [Params(128, 768)]
    public int Dimensions { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _records = TestDataGenerator.GenerateRecords(1000, Dimensions);
        _dbPath = Path.Combine(Path.GetTempPath(), $"vlite_bench_insert_{Guid.NewGuid():N}.vldb");
    }

    [Benchmark]
    public async Task Insert_1000_Records()
    {
        using var db = new VectorLiteDB(_dbPath + $".{Dimensions}",
            new VectorLiteOptions { CheckpointInterval = Timeout.InfiniteTimeSpan });
        var collection = db.GetOrCreateCollection("bench", Dimensions);
        await collection.InsertBatchAsync(_records);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        foreach (var file in Directory.GetFiles(Path.GetTempPath(), "vlite_bench_insert_*"))
        {
            try { File.Delete(file); } catch { }
        }
    }
}
