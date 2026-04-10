using System.Numerics;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace Mugu.AI.VectorLite.Engine.Distance;

/// <summary>
/// 余弦距离：返回 1 - cosine_similarity，值域 [0, 2]，值越小越相似。
/// 三级 SIMD 回退：AVX-512 → AVX2 → Vector&lt;float&gt;
/// </summary>
internal sealed class CosineDistance : IDistanceFunction
{
    public DistanceMetric Metric => DistanceMetric.Cosine;

    public float Calculate(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        if (a.Length != b.Length)
            throw new DimensionMismatchException(a.Length, b.Length);

        if (a.Length == 0) return 0f;

        float dot, normA, normB;

        if (Vector512.IsHardwareAccelerated && a.Length >= Vector512<float>.Count)
        {
            ComputeAvx512(a, b, out dot, out normA, out normB);
        }
        else if (Vector256.IsHardwareAccelerated && a.Length >= Vector256<float>.Count)
        {
            ComputeAvx2(a, b, out dot, out normA, out normB);
        }
        else
        {
            ComputeVectorT(a, b, out dot, out normA, out normB);
        }

        // NaN/Inf 防护：输入含异常值时返回最大距离
        if (float.IsNaN(dot) || float.IsNaN(normA) || float.IsNaN(normB) ||
            float.IsInfinity(dot) || float.IsInfinity(normA) || float.IsInfinity(normB))
            return 2f;

        var denominator = MathF.Sqrt(normA * normB);
        if (denominator < float.Epsilon) return 1f;

        var cosine = dot / denominator;
        return 1f - Math.Clamp(cosine, -1f, 1f);
    }

    private static void ComputeAvx512(ReadOnlySpan<float> a, ReadOnlySpan<float> b,
        out float dot, out float normA, out float normB)
    {
        var vDot = Vector512<float>.Zero;
        var vNormA = Vector512<float>.Zero;
        var vNormB = Vector512<float>.Zero;

        var count = Vector512<float>.Count;
        var i = 0;

        var spanA = MemoryMarshal.Cast<float, Vector512<float>>(a);
        var spanB = MemoryMarshal.Cast<float, Vector512<float>>(b);

        for (var j = 0; j < spanA.Length; j++)
        {
            vDot += spanA[j] * spanB[j];
            vNormA += spanA[j] * spanA[j];
            vNormB += spanB[j] * spanB[j];
        }

        dot = Vector512.Sum(vDot);
        normA = Vector512.Sum(vNormA);
        normB = Vector512.Sum(vNormB);

        // 处理剩余元素
        i = spanA.Length * count;
        for (; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            normA += a[i] * a[i];
            normB += b[i] * b[i];
        }
    }

    private static void ComputeAvx2(ReadOnlySpan<float> a, ReadOnlySpan<float> b,
        out float dot, out float normA, out float normB)
    {
        var vDot = Vector256<float>.Zero;
        var vNormA = Vector256<float>.Zero;
        var vNormB = Vector256<float>.Zero;

        var count = Vector256<float>.Count;

        var spanA = MemoryMarshal.Cast<float, Vector256<float>>(a);
        var spanB = MemoryMarshal.Cast<float, Vector256<float>>(b);

        for (var j = 0; j < spanA.Length; j++)
        {
            vDot += spanA[j] * spanB[j];
            vNormA += spanA[j] * spanA[j];
            vNormB += spanB[j] * spanB[j];
        }

        dot = Vector256.Sum(vDot);
        normA = Vector256.Sum(vNormA);
        normB = Vector256.Sum(vNormB);

        var i = spanA.Length * count;
        for (; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            normA += a[i] * a[i];
            normB += b[i] * b[i];
        }
    }

    private static void ComputeVectorT(ReadOnlySpan<float> a, ReadOnlySpan<float> b,
        out float dot, out float normA, out float normB)
    {
        var vDot = Vector<float>.Zero;
        var vNormA = Vector<float>.Zero;
        var vNormB = Vector<float>.Zero;

        var count = Vector<float>.Count;

        var spanA = MemoryMarshal.Cast<float, Vector<float>>(a);
        var spanB = MemoryMarshal.Cast<float, Vector<float>>(b);

        for (var j = 0; j < spanA.Length; j++)
        {
            vDot += spanA[j] * spanB[j];
            vNormA += spanA[j] * spanA[j];
            vNormB += spanB[j] * spanB[j];
        }

        dot = Vector.Sum(vDot);
        normA = Vector.Sum(vNormA);
        normB = Vector.Sum(vNormB);

        var i = spanA.Length * count;
        for (; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            normA += a[i] * a[i];
            normB += b[i] * b[i];
        }
    }
}
