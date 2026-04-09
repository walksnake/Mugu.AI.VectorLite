using Mugu.AI.VectorLite.Engine.Distance;

namespace Mugu.AI.VectorLite.Engine;

/// <summary>
/// 查询引擎：协调标量索引和 HNSW 索引执行混合查询。
/// </summary>
internal sealed class QueryEngine
{
    private readonly HNSWIndex _hnswIndex;
    private readonly ScalarIndex _scalarIndex;
    private readonly IDistanceFunction _distFunc;

    internal QueryEngine(HNSWIndex hnswIndex, ScalarIndex scalarIndex, IDistanceFunction distFunc)
    {
        _hnswIndex = hnswIndex;
        _scalarIndex = scalarIndex;
        _distFunc = distFunc;
    }

    /// <summary>
    /// 执行混合查询：先标量过滤，再向量搜索。
    /// </summary>
    internal IReadOnlyList<(ulong RecordId, float Distance)> Search(
        ReadOnlySpan<float> queryVector,
        int topK,
        int efSearch,
        FilterExpression? filter = null)
    {
        HashSet<ulong>? candidateIds = null;

        // 如有过滤条件，先用标量索引缩小候选集
        if (filter != null)
        {
            candidateIds = _scalarIndex.Filter(filter);
            if (candidateIds.Count == 0)
                return [];
        }

        return _hnswIndex.Search(queryVector, topK, efSearch, candidateIds);
    }
}
