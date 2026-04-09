namespace Mugu.AI.VectorLite.QualityGate.Infrastructure;

/// <summary>
/// 基准结果校验器：比较实际指标与阈值配置。
/// </summary>
internal static class ThresholdValidator
{
    /// <summary>验证指标是否达标（值越大越好型，如 Recall、吞吐量）</summary>
    internal static bool ValidateHigherIsBetter(double actual, double threshold, double tolerance)
    {
        var adjustedThreshold = threshold * (1 - tolerance);
        return actual >= adjustedThreshold;
    }

    /// <summary>验证指标是否达标（值越小越好型，如延迟）</summary>
    internal static bool ValidateLowerIsBetter(double actual, double threshold, double tolerance)
    {
        var adjustedThreshold = threshold * (1 + tolerance);
        return actual <= adjustedThreshold;
    }
}
