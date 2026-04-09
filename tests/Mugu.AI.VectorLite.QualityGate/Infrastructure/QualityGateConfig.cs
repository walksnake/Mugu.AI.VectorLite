using System.Text.Json;

namespace Mugu.AI.VectorLite.QualityGate.Infrastructure;

/// <summary>
/// 质量门禁阈值配置模型。
/// 从 quality-gate.json 加载量化标准。
/// </summary>
internal sealed class QualityGateConfig
{
    /// <summary>HNSW 检索 Recall@10 最低阈值（默认 0.90）</summary>
    public double MinRecallAt10 { get; set; } = 0.90;

    /// <summary>HNSW 检索 Recall@50 最低阈值（默认 0.95）</summary>
    public double MinRecallAt50 { get; set; } = 0.95;

    /// <summary>插入吞吐量最低阈值（条/秒）</summary>
    public double MinInsertThroughput { get; set; } = 5000;

    /// <summary>搜索 P99 延迟上限（毫秒）</summary>
    public double MaxSearchP99Ms { get; set; } = 10;

    /// <summary>单次距离计算最大耗时（纳秒）</summary>
    public double MaxDistanceCalcNs { get; set; } = 500;

    /// <summary>阈值容差比例（默认 10%）</summary>
    public double ToleranceRatio { get; set; } = 0.1;

    /// <summary>从 JSON 文件加载配置</summary>
    internal static QualityGateConfig Load(string path = "quality-gate.json")
    {
        if (!File.Exists(path))
            return new QualityGateConfig();

        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<QualityGateConfig>(json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new QualityGateConfig();
    }
}
