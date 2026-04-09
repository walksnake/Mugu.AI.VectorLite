namespace Mugu.AI.VectorLite.Engine;

/// <summary>
/// HNSW 图中的单个节点。
/// 存储向量数据副本（常驻内存）以及各层的邻居连接。
/// </summary>
internal sealed class HNSWNode
{
    /// <summary>对应的向量记录ID</summary>
    internal ulong RecordId { get; }

    /// <summary>该节点所在的最大层级</summary>
    internal int MaxLayer { get; }

    /// <summary>
    /// 各层的邻居列表。Neighbors[i] 为第 i 层的邻居 RecordId 列表。
    /// </summary>
    internal List<ulong>[] Neighbors { get; }

    /// <summary>节点向量的缓存副本（常驻内存以避免重复IO）</summary>
    internal ReadOnlyMemory<float> Vector { get; }

    /// <summary>是否已标记删除</summary>
    internal bool IsDeleted { get; set; }

    internal HNSWNode(ulong recordId, int maxLayer, ReadOnlyMemory<float> vector)
    {
        RecordId = recordId;
        MaxLayer = maxLayer;
        Vector = vector;
        IsDeleted = false;
        Neighbors = new List<ulong>[maxLayer + 1];
        for (var i = 0; i <= maxLayer; i++)
        {
            Neighbors[i] = new List<ulong>();
        }
    }
}
