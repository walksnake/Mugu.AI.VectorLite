using FluentAssertions;
using Mugu.AI.VectorLite.Engine.Distance;

namespace Mugu.AI.VectorLite.Tests;

/// <summary>
/// 距离计算函数测试：覆盖所有度量类型、边界条件和 NaN/Inf 防护。
/// </summary>
public class DistanceTests
{
    private static readonly IDistanceFunction Cosine = DistanceFunctionFactory.Get(DistanceMetric.Cosine);
    private static readonly IDistanceFunction Euclidean = DistanceFunctionFactory.Get(DistanceMetric.Euclidean);
    private static readonly IDistanceFunction DotProduct = DistanceFunctionFactory.Get(DistanceMetric.DotProduct);

    // ===== 余弦距离 =====

    [Fact]
    public void Cosine_IdenticalVectors_ShouldReturnZero()
    {
        var v = new float[] { 1, 2, 3, 4 };
        Cosine.Calculate(v, v).Should().BeApproximately(0f, 0.001f);
    }

    [Fact]
    public void Cosine_OrthogonalVectors_ShouldReturnOne()
    {
        var a = new float[] { 1, 0, 0, 0 };
        var b = new float[] { 0, 1, 0, 0 };
        Cosine.Calculate(a, b).Should().BeApproximately(1f, 0.001f);
    }

    [Fact]
    public void Cosine_OppositeVectors_ShouldReturnTwo()
    {
        var a = new float[] { 1, 0, 0, 0 };
        var b = new float[] { -1, 0, 0, 0 };
        Cosine.Calculate(a, b).Should().BeApproximately(2f, 0.001f);
    }

    [Fact]
    public void Cosine_EmptyVectors_ShouldReturnZero()
    {
        Cosine.Calculate([], []).Should().Be(0f);
    }

    [Fact]
    public void Cosine_DimensionMismatch_ShouldThrow()
    {
        var a = new float[] { 1, 2 };
        var b = new float[] { 1, 2, 3 };
        var act = () => Cosine.Calculate(a, b);
        act.Should().Throw<DimensionMismatchException>();
    }

    [Fact]
    public void Cosine_NaNVector_ShouldReturnMaxDistance()
    {
        var a = new float[] { float.NaN, 1, 0, 0 };
        var b = new float[] { 1, 0, 0, 0 };
        Cosine.Calculate(a, b).Should().Be(2f);
    }

    [Fact]
    public void Cosine_InfinityVector_ShouldReturnMaxDistance()
    {
        var a = new float[] { float.PositiveInfinity, 1, 0, 0 };
        var b = new float[] { 1, 0, 0, 0 };
        Cosine.Calculate(a, b).Should().Be(2f);
    }

    [Fact]
    public void Cosine_ZeroVector_ShouldReturnOne()
    {
        var a = new float[] { 0, 0, 0, 0 };
        var b = new float[] { 1, 0, 0, 0 };
        // 零向量范数为 0，分母为 0，应返回 1.0
        Cosine.Calculate(a, b).Should().Be(1f);
    }

    [Fact]
    public void Cosine_LargeVector_ShouldCompute()
    {
        // 测试 SIMD 路径（维度 > 16 触发 AVX2/512）
        var dims = 128;
        var a = new float[dims];
        var b = new float[dims];
        for (var i = 0; i < dims; i++)
        {
            a[i] = i;
            b[i] = dims - i;
        }
        var dist = Cosine.Calculate(a, b);
        dist.Should().BeInRange(0f, 2f);
    }

    [Fact]
    public void Cosine_SmallVector_NonSimd_ShouldCompute()
    {
        // 2 维向量强制走标量路径
        var a = new float[] { 3, 4 };
        var b = new float[] { 4, 3 };
        var dist = Cosine.Calculate(a, b);
        dist.Should().BeInRange(0f, 2f);
        dist.Should().BeGreaterThan(0f); // 不完全平行
    }

    // ===== 欧几里得距离 =====

    [Fact]
    public void Euclidean_IdenticalVectors_ShouldReturnZero()
    {
        var v = new float[] { 1, 2, 3, 4 };
        Euclidean.Calculate(v, v).Should().BeApproximately(0f, 0.001f);
    }

    [Fact]
    public void Euclidean_KnownDistance_ShouldBeCorrect()
    {
        var a = new float[] { 0, 0 };
        var b = new float[] { 3, 4 };
        // sqrt(9+16) = 5
        Euclidean.Calculate(a, b).Should().BeApproximately(5f, 0.001f);
    }

    [Fact]
    public void Euclidean_EmptyVectors_ShouldReturnZero()
    {
        Euclidean.Calculate([], []).Should().Be(0f);
    }

    [Fact]
    public void Euclidean_DimensionMismatch_ShouldThrow()
    {
        var act = () => Euclidean.Calculate(new float[] { 1 }, new float[] { 1, 2 });
        act.Should().Throw<DimensionMismatchException>();
    }

    [Fact]
    public void Euclidean_NaN_ShouldReturnMaxValue()
    {
        var a = new float[] { float.NaN, 0 };
        var b = new float[] { 0, 0 };
        Euclidean.Calculate(a, b).Should().Be(float.MaxValue);
    }

    [Fact]
    public void Euclidean_LargeVector_ShouldCompute()
    {
        var dims = 256;
        var a = new float[dims];
        var b = new float[dims];
        for (var i = 0; i < dims; i++) { a[i] = 1f; b[i] = 2f; }
        var dist = Euclidean.Calculate(a, b);
        // sqrt(256 * 1) = 16
        dist.Should().BeApproximately(16f, 0.01f);
    }

    // ===== 点积距离 =====

    [Fact]
    public void DotProduct_OrthogonalVectors_ShouldReturnZero()
    {
        var a = new float[] { 1, 0 };
        var b = new float[] { 0, 1 };
        // dot=0, distance = -0 = 0
        DotProduct.Calculate(a, b).Should().BeApproximately(0f, 0.001f);
    }

    [Fact]
    public void DotProduct_ParallelVectors_ShouldReturnNegativeDot()
    {
        var a = new float[] { 2, 3 };
        var b = new float[] { 4, 5 };
        // dot = 8+15 = 23, distance = -23
        DotProduct.Calculate(a, b).Should().BeApproximately(-23f, 0.001f);
    }

    [Fact]
    public void DotProduct_EmptyVectors_ShouldReturnZero()
    {
        DotProduct.Calculate([], []).Should().Be(0f);
    }

    [Fact]
    public void DotProduct_NaN_ShouldReturnMaxValue()
    {
        var a = new float[] { float.NaN, 1 };
        var b = new float[] { 1, 1 };
        DotProduct.Calculate(a, b).Should().Be(float.MaxValue);
    }

    [Fact]
    public void DotProduct_LargeVector_ShouldCompute()
    {
        var dims = 64;
        var a = new float[dims];
        var b = new float[dims];
        for (var i = 0; i < dims; i++) { a[i] = 1f; b[i] = 1f; }
        // dot = 64, distance = -64
        DotProduct.Calculate(a, b).Should().BeApproximately(-64f, 0.01f);
    }

    // ===== Infinity 边界测试（补充 SIMD 分支） =====

    [Fact]
    public void Euclidean_Infinity_ShouldReturnMaxValue()
    {
        var a = new float[] { float.PositiveInfinity, 0 };
        var b = new float[] { 0, 0 };
        Euclidean.Calculate(a, b).Should().Be(float.MaxValue);
    }

    [Fact]
    public void Euclidean_NegativeInfinity_ShouldReturnMaxValue()
    {
        var a = new float[] { float.NegativeInfinity, 0 };
        var b = new float[] { 0, 0 };
        Euclidean.Calculate(a, b).Should().Be(float.MaxValue);
    }

    [Fact]
    public void DotProduct_Infinity_ShouldReturnMaxValue()
    {
        var a = new float[] { float.PositiveInfinity, 1 };
        var b = new float[] { 1, 1 };
        DotProduct.Calculate(a, b).Should().Be(float.MaxValue);
    }

    [Fact]
    public void DotProduct_NegativeInfinity_ShouldReturnMaxValue()
    {
        var a = new float[] { float.NegativeInfinity, 1 };
        var b = new float[] { 1, 1 };
        DotProduct.Calculate(a, b).Should().Be(float.MaxValue);
    }

    [Fact]
    public void Cosine_NegativeInfinity_ShouldReturnMaxDistance()
    {
        var a = new float[] { float.NegativeInfinity, 0, 0, 0 };
        var b = new float[] { 1, 0, 0, 0 };
        Cosine.Calculate(a, b).Should().Be(2f);
    }

    [Fact]
    public void DotProduct_NaN_LargeVector_ShouldReturnMaxValue()
    {
        // 触发 AVX2 路径中的 NaN 检测
        var dims = 64;
        var a = new float[dims];
        var b = new float[dims];
        for (var i = 0; i < dims; i++) { a[i] = 1f; b[i] = 1f; }
        a[0] = float.NaN;
        DotProduct.Calculate(a, b).Should().Be(float.MaxValue);
    }

    [Fact]
    public void Euclidean_NaN_LargeVector_ShouldReturnMaxValue()
    {
        var dims = 128;
        var a = new float[dims];
        var b = new float[dims];
        a[0] = float.NaN;
        Euclidean.Calculate(a, b).Should().Be(float.MaxValue);
    }

    [Fact]
    public void Cosine_NaN_LargeVector_ShouldReturnTwo()
    {
        var dims = 128;
        var a = new float[dims];
        var b = new float[dims];
        for (var i = 0; i < dims; i++) { a[i] = 1f; b[i] = 1f; }
        a[10] = float.NaN;
        Cosine.Calculate(a, b).Should().Be(2f);
    }

    [Fact]
    public void Cosine_SingleElement_ShouldCompute()
    {
        var a = new float[] { 3f };
        var b = new float[] { 5f };
        // 两个正数同方向，余弦距离应接近 0
        Cosine.Calculate(a, b).Should().BeApproximately(0f, 0.001f);
    }

    [Fact]
    public void Euclidean_SingleElement_ShouldCompute()
    {
        var a = new float[] { 3f };
        var b = new float[] { 7f };
        Euclidean.Calculate(a, b).Should().BeApproximately(4f, 0.001f);
    }

    [Fact]
    public void DotProduct_SingleElement_ShouldCompute()
    {
        var a = new float[] { 3f };
        var b = new float[] { 5f };
        // dot = 15, distance = -15
        DotProduct.Calculate(a, b).Should().BeApproximately(-15f, 0.001f);
    }

    // ===== 工厂 =====

    [Fact]
    public void Factory_AllMetrics_ShouldReturn()
    {
        DistanceFunctionFactory.Get(DistanceMetric.Cosine).Should().NotBeNull();
        DistanceFunctionFactory.Get(DistanceMetric.Euclidean).Should().NotBeNull();
        DistanceFunctionFactory.Get(DistanceMetric.DotProduct).Should().NotBeNull();
    }

    [Fact]
    public void Factory_InvalidMetric_ShouldThrow()
    {
        var act = () => DistanceFunctionFactory.Get((DistanceMetric)99);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Factory_Singleton_ShouldReturnSameInstance()
    {
        var a = DistanceFunctionFactory.Get(DistanceMetric.Cosine);
        var b = DistanceFunctionFactory.Get(DistanceMetric.Cosine);
        a.Should().BeSameAs(b);
    }
}
