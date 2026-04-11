using System.Collections.Concurrent;

namespace Mugu.AI.VectorLite.Engine;

/// <summary>
/// HNSW 图结构，管理所有节点及入口点信息。
/// </summary>
internal sealed class HNSWGraph
{
    // 使用私有字段保证跨线程内存可见性
    private ulong _entryPointId;
    private volatile int _maxLayer = -1;

    /// <summary>入口点节点ID（最高层的节点），0 表示图为空。通过 Volatile.Read/Write 保证内存可见性。</summary>
    internal ulong EntryPointId
    {
        get => Volatile.Read(ref _entryPointId);
        set => Volatile.Write(ref _entryPointId, value);
    }

    /// <summary>当前图的最大层级，-1 表示图为空。volatile 字段保证跨线程可见性。</summary>
    internal int MaxLayer
    {
        get => _maxLayer;
        set => _maxLayer = value;
    }

    /// <summary>所有节点的快速查找表（并发安全）</summary>
    internal ConcurrentDictionary<ulong, HNSWNode> Nodes { get; } = new();

    /// <summary>节点总数（含已删除）</summary>
    internal int Count => Nodes.Count;

    /// <summary>已删除节点数</summary>
    internal int DeletedCount { get; set; }

    /// <summary>活跃节点数</summary>
    internal int ActiveCount => Count - DeletedCount;

    /// <summary>图是否为空</summary>
    internal bool IsEmpty => Nodes.IsEmpty;
}
