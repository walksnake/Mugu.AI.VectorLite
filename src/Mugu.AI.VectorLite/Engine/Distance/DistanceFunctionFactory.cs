namespace Mugu.AI.VectorLite.Engine.Distance;

/// <summary>
/// 距离函数工厂：根据 DistanceMetric 枚举返回对应的距离计算实现。
/// 实例为无状态单例，线程安全。
/// </summary>
internal static class DistanceFunctionFactory
{
    private static readonly CosineDistance s_cosine = new();
    private static readonly EuclideanDistance s_euclidean = new();
    private static readonly DotProductDistance s_dotProduct = new();

    /// <summary>获取指定度量类型的距离函数实例</summary>
    internal static IDistanceFunction Get(DistanceMetric metric) => metric switch
    {
        DistanceMetric.Cosine => s_cosine,
        DistanceMetric.Euclidean => s_euclidean,
        DistanceMetric.DotProduct => s_dotProduct,
        _ => throw new ArgumentOutOfRangeException(nameof(metric), metric, $"不支持的距离度量: {metric}")
    };
}
