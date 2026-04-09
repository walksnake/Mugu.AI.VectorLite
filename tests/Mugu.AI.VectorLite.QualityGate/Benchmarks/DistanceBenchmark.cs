using BenchmarkDotNet.Attributes;
using Mugu.AI.VectorLite.Engine.Distance;

namespace Mugu.AI.VectorLite.QualityGate.Benchmarks;

/// <summary>向量距离计算性能基准</summary>
[MemoryDiagnoser]
[ShortRunJob]
public class DistanceBenchmark
{
    private float[] _vectorA = null!;
    private float[] _vectorB = null!;
    private IDistanceFunction _cosine = null!;
    private IDistanceFunction _euclidean = null!;
    private IDistanceFunction _dotProduct = null!;

    [Params(128, 768, 1536)]
    public int Dimensions { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        var random = new Random(42);
        _vectorA = Enumerable.Range(0, Dimensions).Select(_ => (float)(random.NextDouble() * 2 - 1)).ToArray();
        _vectorB = Enumerable.Range(0, Dimensions).Select(_ => (float)(random.NextDouble() * 2 - 1)).ToArray();
        _cosine = DistanceFunctionFactory.Get(DistanceMetric.Cosine);
        _euclidean = DistanceFunctionFactory.Get(DistanceMetric.Euclidean);
        _dotProduct = DistanceFunctionFactory.Get(DistanceMetric.DotProduct);
    }

    [Benchmark(Baseline = true)]
    public float Cosine() => _cosine.Calculate(_vectorA, _vectorB);

    [Benchmark]
    public float Euclidean() => _euclidean.Calculate(_vectorA, _vectorB);

    [Benchmark]
    public float DotProduct() => _dotProduct.Calculate(_vectorA, _vectorB);
}
