using System.Numerics;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace Mugu.AI.VectorLite.Engine.Distance;

/// <summary>
/// 欧几里得距离：返回 L2 距离，值越小越相似。
/// 三级 SIMD 回退：AVX-512 → AVX2 → Vector&lt;float&gt;
/// </summary>
internal sealed class EuclideanDistance : IDistanceFunction
{
    public DistanceMetric Metric => DistanceMetric.Euclidean;

    public float Calculate(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        if (a.Length != b.Length)
            throw new DimensionMismatchException(a.Length, b.Length);

        if (a.Length == 0) return 0f;

        float sumSq;

        if (Vector512.IsHardwareAccelerated && a.Length >= Vector512<float>.Count)
        {
            sumSq = ComputeAvx512(a, b);
        }
        else if (Vector256.IsHardwareAccelerated && a.Length >= Vector256<float>.Count)
        {
            sumSq = ComputeAvx2(a, b);
        }
        else
        {
            sumSq = ComputeVectorT(a, b);
        }

        // NaN/Inf 防护
        if (float.IsNaN(sumSq) || float.IsInfinity(sumSq))
            return float.MaxValue;

        return MathF.Sqrt(sumSq);
    }

    private static float ComputeAvx512(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        var vSum = Vector512<float>.Zero;
        var count = Vector512<float>.Count;

        var spanA = MemoryMarshal.Cast<float, Vector512<float>>(a);
        var spanB = MemoryMarshal.Cast<float, Vector512<float>>(b);

        for (var j = 0; j < spanA.Length; j++)
        {
            var diff = spanA[j] - spanB[j];
            vSum += diff * diff;
        }

        var sum = Vector512.Sum(vSum);

        var i = spanA.Length * count;
        for (; i < a.Length; i++)
        {
            var diff = a[i] - b[i];
            sum += diff * diff;
        }

        return sum;
    }

    private static float ComputeAvx2(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        var vSum = Vector256<float>.Zero;
        var count = Vector256<float>.Count;

        var spanA = MemoryMarshal.Cast<float, Vector256<float>>(a);
        var spanB = MemoryMarshal.Cast<float, Vector256<float>>(b);

        for (var j = 0; j < spanA.Length; j++)
        {
            var diff = spanA[j] - spanB[j];
            vSum += diff * diff;
        }

        var sum = Vector256.Sum(vSum);

        var i = spanA.Length * count;
        for (; i < a.Length; i++)
        {
            var diff = a[i] - b[i];
            sum += diff * diff;
        }

        return sum;
    }

    private static float ComputeVectorT(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        var vSum = Vector<float>.Zero;
        var count = Vector<float>.Count;

        var spanA = MemoryMarshal.Cast<float, Vector<float>>(a);
        var spanB = MemoryMarshal.Cast<float, Vector<float>>(b);

        for (var j = 0; j < spanA.Length; j++)
        {
            var diff = spanA[j] - spanB[j];
            vSum += diff * diff;
        }

        var sum = Vector.Sum(vSum);

        var i = spanA.Length * count;
        for (; i < a.Length; i++)
        {
            var diff = a[i] - b[i];
            sum += diff * diff;
        }

        return sum;
    }
}
