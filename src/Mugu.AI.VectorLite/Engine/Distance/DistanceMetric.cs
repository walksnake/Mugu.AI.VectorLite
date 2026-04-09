namespace Mugu.AI.VectorLite.Engine.Distance;

/// <summary>向量距离度量类型</summary>
public enum DistanceMetric : byte
{
    /// <summary>余弦相似度（返回 1 - cosine，值越小越相似）</summary>
    Cosine = 0,

    /// <summary>欧几里得距离</summary>
    Euclidean = 1,

    /// <summary>点积（值越大越相似，内部取负以统一为"越小越好"语义）</summary>
    DotProduct = 2
}
