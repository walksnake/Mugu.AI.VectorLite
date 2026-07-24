using BenchmarkDotNet.Attributes;

namespace Mugu.AI.VectorLite.QualityGate.Benchmarks;

/// <summary>纯标量投影和文本物化分配基准。</summary>
[MemoryDiagnoser]
[ShortRunJob]
public class ScalarMaterializationBenchmark
{
    private VectorLiteDB _db = null!;
    private ICollection _collection = null!;
    private string _dbPath = null!;
    private ulong[] _ids = null!;

    [GlobalSetup]
    public void Setup()
    {
        _dbPath = Path.Combine(
            Path.GetTempPath(),
            $"vlite_bench_materialize_{Guid.NewGuid():N}.vldb");
        using (var initial = new VectorLiteDB(_dbPath))
        {
            var collection = initial.GetOrCreateScalarCollection("records");
            _ids = collection.InsertScalarBatchAsync(
                Enumerable.Range(0, 1_000).Select(CreateRecord))
                .GetAwaiter().GetResult().ToArray();
        }
        _db = new VectorLiteDB(
            _dbPath,
            new VectorLiteOptions { CheckpointInterval = Timeout.InfiniteTimeSpan });
        _collection = _db.GetOrCreateScalarCollection("records");
    }

    [Benchmark(Baseline = true)]
    public Task<IReadOnlyList<RecordView>> MetadataOnly()
        => _collection.GetBatchAsync(_ids, RecordProjection.Metadata);

    [Benchmark]
    public Task<IReadOnlyList<RecordView>> MetadataAndText()
        => _collection.GetBatchAsync(
            _ids,
            RecordProjection.Metadata | RecordProjection.Text);

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
            Text = $"用于冷态读取验证的中文短文本 {index}",
            Metadata = new() { ["sequence"] = (long)index }
        };

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { }
    }
}
