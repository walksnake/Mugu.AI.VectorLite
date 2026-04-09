using System.Buffers.Binary;
using Mugu.AI.VectorLite.Engine.Distance;

namespace Mugu.AI.VectorLite.Engine;

/// <summary>
/// HNSW 索引：支持高效的近邻向量检索。
/// 实现了分层可导航小世界图的插入、搜索、惰性删除和序列化。
/// </summary>
internal sealed class HNSWIndex
{
    private readonly HNSWGraph _graph = new();
    private readonly IDistanceFunction _distFunc;
    private readonly int _m;
    private readonly int _mmax0;
    private readonly int _efConstruction;
    private readonly double _mL;
    private readonly Random _random;
    private readonly object _writeLock = new();

    internal int Count => _graph.ActiveCount;
    internal int TotalCount => _graph.Count;

    internal HNSWIndex(IDistanceFunction distFunc, int m = 16, int efConstruction = 200)
    {
        _distFunc = distFunc;
        _m = m;
        _mmax0 = 2 * m;
        _efConstruction = efConstruction;
        _mL = 1.0 / Math.Log(m);
        _random = new Random();
    }

    /// <summary>插入一个向量节点到索引中</summary>
    internal void Insert(ulong recordId, ReadOnlyMemory<float> vector)
    {
        lock (_writeLock)
        {
            var level = RandomLevel();
            var node = new HNSWNode(recordId, level, vector);
            _graph.Nodes[recordId] = node;

            if (_graph.IsEmpty || _graph.EntryPointId == 0 || !_graph.Nodes.ContainsKey(_graph.EntryPointId))
            {
                _graph.EntryPointId = recordId;
                _graph.MaxLayer = level;
                return;
            }

            var ep = _graph.EntryPointId;
            var L = _graph.MaxLayer;

            // 阶段1：从顶层贪心下降到 level+1 层
            for (var lc = L; lc > level; lc--)
            {
                var w = SearchLayer(vector.Span, ep, 1, lc, null);
                if (w.Count > 0)
                    ep = w[0].RecordId;
            }

            // 阶段2：在 level 层到第 0 层执行插入
            for (var lc = Math.Min(level, L); lc >= 0; lc--)
            {
                var w = SearchLayer(vector.Span, ep, _efConstruction, lc, null);
                var maxConn = lc == 0 ? _mmax0 : _m;
                var neighbors = SelectNeighborsHeuristic(vector.Span, w, maxConn);

                // 建立双向连接
                foreach (var (nId, _) in neighbors)
                {
                    if (lc <= node.MaxLayer)
                    {
                        lock (node.Neighbors[lc])
                        {
                            node.Neighbors[lc].Add(nId);
                        }
                    }

                    var nNode = _graph.Nodes[nId];
                    if (lc <= nNode.MaxLayer)
                    {
                        lock (nNode.Neighbors[lc])
                        {
                            nNode.Neighbors[lc].Add(recordId);
                            // 裁剪超出最大连接数的邻居
                            if (nNode.Neighbors[lc].Count > maxConn)
                            {
                                TrimNeighbors(nNode, lc, maxConn);
                            }
                        }
                    }
                }

                if (w.Count > 0)
                    ep = w[0].RecordId;
            }

            // 更新入口点
            if (level > L)
            {
                _graph.EntryPointId = recordId;
                _graph.MaxLayer = level;
            }
        }
    }

    /// <summary>标记删除（惰性）</summary>
    internal void MarkDeleted(ulong recordId)
    {
        lock (_writeLock)
        {
            if (_graph.Nodes.TryGetValue(recordId, out var node) && !node.IsDeleted)
            {
                node.IsDeleted = true;
                _graph.DeletedCount++;
            }
        }
    }

    /// <summary>
    /// 搜索最近邻。candidateIds 非空时启用前置过滤。
    /// </summary>
    internal IReadOnlyList<(ulong RecordId, float Distance)> Search(
        ReadOnlySpan<float> queryVector,
        int topK,
        int efSearch,
        HashSet<ulong>? candidateIds = null)
    {
        if (_graph.IsEmpty) return [];

        var ep = _graph.EntryPointId;
        var L = _graph.MaxLayer;

        // 阶段1：贪心下降到第 1 层
        for (var lc = L; lc >= 1; lc--)
        {
            var w = SearchLayer(queryVector, ep, 1, lc, null);
            if (w.Count > 0)
                ep = w[0].RecordId;
        }

        // 阶段2：在第 0 层搜索
        var efActual = Math.Max(efSearch, topK);
        var results = SearchLayer(queryVector, ep, efActual, 0, candidateIds);

        // 过滤已删除节点和不符合过滤条件的节点，取 top-K
        return results
            .Where(r => _graph.Nodes.TryGetValue(r.RecordId, out var n) && !n.IsDeleted)
            .Where(r => candidateIds == null || candidateIds.Contains(r.RecordId))
            .OrderBy(r => r.Distance)
            .Take(topK)
            .ToList();
    }

    /// <summary>检查是否需要压缩（已删除节点超过20%）</summary>
    internal bool NeedsCompaction()
    {
        if (_graph.Count == 0) return false;
        return (double)_graph.DeletedCount / _graph.Count > 0.2;
    }

    /// <summary>获取所有活跃节点（用于重建）</summary>
    internal IEnumerable<(ulong RecordId, ReadOnlyMemory<float> Vector)> GetActiveNodes()
    {
        return _graph.Nodes.Values
            .Where(n => !n.IsDeleted)
            .Select(n => (n.RecordId, n.Vector));
    }

    /// <summary>序列化到字节数组</summary>
    internal byte[] Serialize()
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);

        // 图元信息
        bw.Write(_graph.EntryPointId);
        bw.Write(_graph.MaxLayer);
        bw.Write((uint)_graph.Nodes.Count);

        foreach (var node in _graph.Nodes.Values)
        {
            bw.Write(node.RecordId);
            bw.Write(node.MaxLayer);
            bw.Write(node.IsDeleted);

            // 向量
            bw.Write(node.Vector.Length);
            foreach (var v in node.Vector.Span)
                bw.Write(v);

            // 各层邻居（获取快照避免并发问题）
            for (var layer = 0; layer <= node.MaxLayer; layer++)
            {
                ulong[] neighborSnapshot;
                lock (node.Neighbors[layer])
                {
                    neighborSnapshot = node.Neighbors[layer].ToArray();
                }
                bw.Write((uint)neighborSnapshot.Length);
                foreach (var nId in neighborSnapshot)
                    bw.Write(nId);
            }
        }

        return ms.ToArray();
    }

    /// <summary>从字节数据反序列化重建索引</summary>
    internal static HNSWIndex Deserialize(
        ReadOnlySpan<byte> data,
        IDistanceFunction distFunc,
        int m = 16,
        int efConstruction = 200)
    {
        var index = new HNSWIndex(distFunc, m, efConstruction);

        var offset = 0;
        index._graph.EntryPointId = BinaryPrimitives.ReadUInt64LittleEndian(data[offset..]);
        offset += 8;
        index._graph.MaxLayer = BinaryPrimitives.ReadInt32LittleEndian(data[offset..]);
        offset += 4;
        var nodeCount = BinaryPrimitives.ReadUInt32LittleEndian(data[offset..]);
        offset += 4;

        for (uint i = 0; i < nodeCount; i++)
        {
            var recordId = BinaryPrimitives.ReadUInt64LittleEndian(data[offset..]);
            offset += 8;
            var maxLayer = BinaryPrimitives.ReadInt32LittleEndian(data[offset..]);
            offset += 4;
            var isDeleted = data[offset] != 0;
            offset += 1;

            var dimensions = BinaryPrimitives.ReadInt32LittleEndian(data[offset..]);
            offset += 4;
            var vector = new float[dimensions];
            for (var d = 0; d < dimensions; d++)
            {
                vector[d] = BinaryPrimitives.ReadSingleLittleEndian(data[offset..]);
                offset += 4;
            }

            var node = new HNSWNode(recordId, maxLayer, vector)
            {
                IsDeleted = isDeleted
            };

            for (var layer = 0; layer <= maxLayer; layer++)
            {
                var neighborCount = BinaryPrimitives.ReadUInt32LittleEndian(data[offset..]);
                offset += 4;
                for (uint n = 0; n < neighborCount; n++)
                {
                    var nId = BinaryPrimitives.ReadUInt64LittleEndian(data[offset..]);
                    offset += 8;
                    node.Neighbors[layer].Add(nId);
                }
            }

            index._graph.Nodes[recordId] = node;
            if (isDeleted) index._graph.DeletedCount++;
        }

        return index;
    }

    /// <summary>在指定层搜索</summary>
    private List<(ulong RecordId, float Distance)> SearchLayer(
        ReadOnlySpan<float> queryVector,
        ulong entryId,
        int ef,
        int layer,
        HashSet<ulong>? candidateIds)
    {
        if (!_graph.Nodes.TryGetValue(entryId, out var entryNode))
            return [];

        var visited = new HashSet<ulong> { entryId };
        var entryDist = _distFunc.Calculate(queryVector, entryNode.Vector.Span);

        // 最小堆（候选集）— 入口节点始终加入候选集用于导航
        var candidates = new PriorityQueue<ulong, float>();
        candidates.Enqueue(entryId, entryDist);

        // 结果集 — 入口节点仅在通过过滤时才加入
        var results = new List<(ulong RecordId, float Distance)>();
        if (candidateIds == null || candidateIds.Contains(entryId))
        {
            results.Add((entryId, entryDist));
        }
        var farthestDist = entryDist;

        while (candidates.Count > 0)
        {
            candidates.TryDequeue(out var cId, out var cDist);

            if (cDist > farthestDist && results.Count >= ef)
                break;

            if (!_graph.Nodes.TryGetValue(cId, out var cNode))
                continue;

            if (layer > cNode.MaxLayer)
                continue;

            // 获取邻居快照，避免并发修改异常
            ulong[] neighborSnapshot;
            lock (cNode.Neighbors[layer])
            {
                neighborSnapshot = cNode.Neighbors[layer].ToArray();
            }

            foreach (var neighborId in neighborSnapshot)
            {
                if (!visited.Add(neighborId)) continue;
                if (!_graph.Nodes.TryGetValue(neighborId, out var nNode)) continue;

                // 前置过滤
                if (candidateIds != null && !candidateIds.Contains(neighborId))
                    continue;

                var nDist = _distFunc.Calculate(queryVector, nNode.Vector.Span);

                if (results.Count < ef || nDist < farthestDist)
                {
                    candidates.Enqueue(neighborId, nDist);
                    results.Add((neighborId, nDist));

                    // 保持结果集大小不超过 ef
                    if (results.Count > ef)
                    {
                        results.Sort((x, y) => x.Distance.CompareTo(y.Distance));
                        results.RemoveAt(results.Count - 1);
                    }

                    farthestDist = results.Max(r => r.Distance);
                }
            }
        }

        results.Sort((x, y) => x.Distance.CompareTo(y.Distance));
        return results;
    }

    /// <summary>启发式邻居选择</summary>
    private List<(ulong RecordId, float Distance)> SelectNeighborsHeuristic(
        ReadOnlySpan<float> vector,
        List<(ulong RecordId, float Distance)> candidates,
        int maxConnections)
    {
        if (candidates.Count <= maxConnections)
            return candidates.ToList();

        // 按距离排序，选择最近的
        var sorted = candidates.OrderBy(c => c.Distance).ToList();
        var selected = new List<(ulong RecordId, float Distance)>();

        foreach (var candidate in sorted)
        {
            if (selected.Count >= maxConnections) break;

            // 启发式：检查候选是否比已选的更好
            var shouldAdd = true;
            foreach (var s in selected)
            {
                if (!_graph.Nodes.TryGetValue(s.RecordId, out var sNode) ||
                    !_graph.Nodes.TryGetValue(candidate.RecordId, out var cNode))
                    continue;

                var distBetween = _distFunc.Calculate(sNode.Vector.Span, cNode.Vector.Span);
                if (distBetween < candidate.Distance)
                {
                    shouldAdd = false;
                    break;
                }
            }

            if (shouldAdd)
                selected.Add(candidate);
        }

        // 如果启发式选择不够，补充最近的
        if (selected.Count < maxConnections)
        {
            foreach (var candidate in sorted)
            {
                if (selected.Count >= maxConnections) break;
                if (!selected.Any(s => s.RecordId == candidate.RecordId))
                    selected.Add(candidate);
            }
        }

        return selected;
    }

    /// <summary>裁剪节点的邻居列表</summary>
    private void TrimNeighbors(HNSWNode node, int layer, int maxConnections)
    {
        var neighbors = node.Neighbors[layer];
        var withDist = new List<(ulong RecordId, float Distance)>();

        foreach (var nId in neighbors)
        {
            if (_graph.Nodes.TryGetValue(nId, out var nNode))
            {
                var dist = _distFunc.Calculate(node.Vector.Span, nNode.Vector.Span);
                withDist.Add((nId, dist));
            }
        }

        var selected = SelectNeighborsHeuristic(node.Vector.Span, withDist, maxConnections);
        neighbors.Clear();
        neighbors.AddRange(selected.Select(s => s.RecordId));
    }

    /// <summary>生成随机层级</summary>
    private int RandomLevel()
    {
        return (int)Math.Floor(-Math.Log(_random.NextDouble()) * _mL);
    }
}
