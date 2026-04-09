namespace Mugu.AI.VectorLite.Engine;

/// <summary>
/// HNSW 图结构，管理所有节点及入口点信息。
/// </summary>
internal sealed class HNSWGraph
{
    /// <summary>入口点节点ID（最高层的节点），0 表示图为空</summary>
    internal ulong EntryPointId { get; set; }

    /// <summary>当前图的最大层级，-1 表示图为空</summary>
    internal int MaxLayer { get; set; } = -1;

    /// <summary>所有节点的快速查找表</summary>
    internal Dictionary<ulong, HNSWNode> Nodes { get; } = new();

    /// <summary>节点总数（含已删除）</summary>
    internal int Count => Nodes.Count;

    /// <summary>已删除节点数</summary>
    internal int DeletedCount { get; set; }

    /// <summary>活跃节点数</summary>
    internal int ActiveCount => Count - DeletedCount;

    /// <summary>图是否为空</summary>
    internal bool IsEmpty => Nodes.Count == 0;
}
