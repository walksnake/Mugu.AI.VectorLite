using System.Numerics;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace Mugu.AI.VectorLite.Engine.Distance;

/// <summary>
/// 点积距离：返回 -dot_product，值越小表示越相似（因为点积越大越相似）。
/// 三级 SIMD 回退：AVX-512 → AVX2 → Vector&lt;float&gt;
/// </summary>
internal sealed class DotProductDistance : IDistanceFunction
{
    public DistanceMetric Metric => DistanceMetric.DotProduct;

    public float Calculate(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        if (a.Length != b.Length)
            throw new DimensionMismatchException(a.Length, b.Length);

        if (a.Length == 0) return 0f;

        float dot;

        if (Vector512.IsHardwareAccelerated && a.Length >= Vector512<float>.Count)
        {
            dot = ComputeAvx512(a, b);
        }
        else if (Vector256.IsHardwareAccelerated && a.Length >= Vector256<float>.Count)
        {
            dot = ComputeAvx2(a, b);
        }
        else
        {
            dot = ComputeVectorT(a, b);
        }

        // 取负值，统一为"值越小越好"语义
        return -dot;
    }

    private static float ComputeAvx512(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        var vDot = Vector512<float>.Zero;
        var count = Vector512<float>.Count;

        var spanA = MemoryMarshal.Cast<float, Vector512<float>>(a);
        var spanB = MemoryMarshal.Cast<float, Vector512<float>>(b);

        for (var j = 0; j < spanA.Length; j++)
        {
            vDot += spanA[j] * spanB[j];
        }

        var dot = Vector512.Sum(vDot);

        var i = spanA.Length * count;
        for (; i < a.Length; i++)
        {
            dot += a[i] * b[i];
        }

        return dot;
    }

    private static float ComputeAvx2(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        var vDot = Vector256<float>.Zero;
        var count = Vector256<float>.Count;

        var spanA = MemoryMarshal.Cast<float, Vector256<float>>(a);
        var spanB = MemoryMarshal.Cast<float, Vector256<float>>(b);

        for (var j = 0; j < spanA.Length; j++)
        {
            vDot += spanA[j] * spanB[j];
        }

        var dot = Vector256.Sum(vDot);

        var i = spanA.Length * count;
        for (; i < a.Length; i++)
        {
            dot += a[i] * b[i];
        }

        return dot;
    }

    private static float ComputeVectorT(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        var vDot = Vector<float>.Zero;
        var count = Vector<float>.Count;

        var spanA = MemoryMarshal.Cast<float, Vector<float>>(a);
        var spanB = MemoryMarshal.Cast<float, Vector<float>>(b);

        for (var j = 0; j < spanA.Length; j++)
        {
            vDot += spanA[j] * spanB[j];
        }

        var dot = Vector.Sum(vDot);

        var i = spanA.Length * count;
        for (; i < a.Length; i++)
        {
            dot += a[i] * b[i];
        }

        return dot;
    }
}
