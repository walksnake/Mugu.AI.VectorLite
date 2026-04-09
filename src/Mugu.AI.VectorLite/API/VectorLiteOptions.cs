using Mugu.AI.VectorLite.Engine.Distance;
using Microsoft.Extensions.Logging;

namespace Mugu.AI.VectorLite;

/// <summary>数据库全局配置，在 VectorLiteDB 构造时传入，运行时不可变。</summary>
public sealed class VectorLiteOptions
{
    /// <summary>页大小（字节），必须为 4096 的整数倍，默认 8192</summary>
    public uint PageSize { get; init; } = 8192;

    /// <summary>最大向量维度，默认 4096</summary>
    public uint MaxDimensions { get; init; } = 4096;

    // ── HNSW 参数 ──

    /// <summary>每节点最大邻居数（非零层），默认 16</summary>
    public int HnswM { get; init; } = 16;

    /// <summary>构建时动态候选列表大小，默认 200</summary>
    public int HnswEfConstruction { get; init; } = 200;

    /// <summary>搜索时动态候选列表大小（默认值，可被单次查询覆盖），默认 50</summary>
    public int HnswEfSearch { get; init; } = 50;

    /// <summary>默认距离度量方式，默认余弦</summary>
    public DistanceMetric DefaultDistanceMetric { get; init; } = DistanceMetric.Cosine;

    // ── 检查点 ──

    /// <summary>自动检查点间隔，默认 5 分钟。设为 Timeout.InfiniteTimeSpan 禁用自动检查点</summary>
    public TimeSpan CheckpointInterval { get; init; } = TimeSpan.FromMinutes(5);

    // ── 日志 ──

    /// <summary>日志工厂，为 null 时不输出日志</summary>
    public ILoggerFactory? LoggerFactory { get; init; }

    /// <summary>验证配置合法性</summary>
    internal void Validate()
    {
        if (PageSize < 4096 || PageSize % 4096 != 0)
            throw new ArgumentException($"PageSize 必须为 4096 的整数倍，当前值: {PageSize}");
        if (MaxDimensions == 0)
            throw new ArgumentException("MaxDimensions 不能为 0");
        if (HnswM < 2)
            throw new ArgumentException($"HnswM 必须 >= 2，当前值: {HnswM}");
        if (HnswEfConstruction < 1)
            throw new ArgumentException($"HnswEfConstruction 必须 >= 1，当前值: {HnswEfConstruction}");
        if (HnswEfSearch < 1)
            throw new ArgumentException($"HnswEfSearch 必须 >= 1，当前值: {HnswEfSearch}");
    }
}
