namespace Mugu.AI.VectorLite.QualityGate.Infrastructure;

/// <summary>
/// 标准测试数据集生成器：提供可重复的随机向量和元数据。
/// </summary>
internal static class TestDataGenerator
{
    /// <summary>生成指定数量的随机归一化向量</summary>
    internal static float[][] GenerateRandomVectors(int count, int dimensions, int seed = 42)
    {
        var random = new Random(seed);
        var vectors = new float[count][];

        for (var i = 0; i < count; i++)
        {
            vectors[i] = GenerateRandomVector(dimensions, random);
        }

        return vectors;
    }

    /// <summary>生成单个随机归一化向量</summary>
    internal static float[] GenerateRandomVector(int dimensions, Random? random = null)
    {
        random ??= Random.Shared;
        var vector = new float[dimensions];
        var sumSq = 0f;

        for (var d = 0; d < dimensions; d++)
        {
            vector[d] = (float)(random.NextDouble() * 2 - 1);
            sumSq += vector[d] * vector[d];
        }

        // 归一化
        var norm = MathF.Sqrt(sumSq);
        if (norm > float.Epsilon)
        {
            for (var d = 0; d < dimensions; d++)
                vector[d] /= norm;
        }

        return vector;
    }

    /// <summary>生成带元数据的测试记录</summary>
    internal static VectorRecord[] GenerateRecords(int count, int dimensions, int seed = 42)
    {
        var random = new Random(seed);
        var categories = new[] { "note", "email", "document", "chat" };
        var records = new VectorRecord[count];

        for (var i = 0; i < count; i++)
        {
            records[i] = new VectorRecord
            {
                Vector = GenerateRandomVector(dimensions, random),
                Metadata = new Dictionary<string, object>
                {
                    ["category"] = categories[i % categories.Length],
                    ["importance"] = (long)(i % 10),
                    ["source"] = $"source_{i % 5}"
                },
                Text = $"测试文本 #{i}"
            };
        }

        return records;
    }

    /// <summary>通过暴力搜索计算精确 K 近邻（作为准确率基准）</summary>
    internal static List<(int Index, float Distance)> BruteForceKnn(
        float[] query, float[][] corpus, int topK)
    {
        var distances = new List<(int Index, float Distance)>();

        for (var i = 0; i < corpus.Length; i++)
        {
            var dist = CosineDistanceBrute(query, corpus[i]);
            distances.Add((i, dist));
        }

        return distances.OrderBy(d => d.Distance).Take(topK).ToList();
    }

    /// <summary>暴力余弦距离计算</summary>
    private static float CosineDistanceBrute(float[] a, float[] b)
    {
        float dot = 0, normA = 0, normB = 0;
        for (var i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            normA += a[i] * a[i];
            normB += b[i] * b[i];
        }

        var denom = MathF.Sqrt(normA * normB);
        if (denom < float.Epsilon) return 1f;
        return 1f - dot / denom;
    }
}
