namespace Mugu.AI.VectorLite.Engine.Distance;

/// <summary>
/// 向量距离计算接口。所有实现必须保证线程安全（无状态纯函数）。
/// 返回值语义统一为：值越小表示越相似。
/// </summary>
internal interface IDistanceFunction
{
    /// <summary>距离度量类型</summary>
    DistanceMetric Metric { get; }

    /// <summary>计算两个等长向量之间的距离</summary>
    float Calculate(ReadOnlySpan<float> a, ReadOnlySpan<float> b);
}
