using Mugu.AI.VectorLite.Engine;
using Mugu.AI.VectorLite.Engine.Distance;
using Microsoft.Extensions.Logging;

namespace Mugu.AI.VectorLite;

/// <summary>
/// 集合实现：管理一个命名集合中的所有向量记录。
/// 内部持有 HNSW 索引、标量索引和记录存储。
/// </summary>
internal sealed class Collection : ICollection
{
    private readonly HNSWIndex _hnswIndex;
    private readonly ScalarIndex _scalarIndex;
    private readonly QueryEngine _queryEngine;
    private readonly IDistanceFunction _distFunc;
    private readonly int _efSearch;
    private readonly ILogger? _logger;
    private readonly object _writeLock = new();

    // 内存中的记录存储（后续会持久化到 FileStorage）
    private readonly Dictionary<ulong, VectorRecord> _records = new();
    private ulong _nextRecordId = 1;

    public string Name { get; }
    public int Dimensions { get; }
    public int Count => _records.Count;

    internal Collection(string name, int dimensions, VectorLiteOptions options, ILogger? logger = null)
    {
        Name = name;
        Dimensions = dimensions;
        _efSearch = options.HnswEfSearch;
        _logger = logger;

        _distFunc = DistanceFunctionFactory.Get(options.DefaultDistanceMetric);
        _hnswIndex = new HNSWIndex(_distFunc, options.HnswM, options.HnswEfConstruction);
        _scalarIndex = new ScalarIndex();
        _queryEngine = new QueryEngine(_hnswIndex, _scalarIndex, _distFunc);
    }

    public Task<ulong> InsertAsync(VectorRecord record, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        if (record.Vector.Length != Dimensions)
            throw new DimensionMismatchException(Dimensions, record.Vector.Length);

        lock (_writeLock)
        {
            var id = _nextRecordId++;
            record.Id = id;

            _records[id] = record;
            _hnswIndex.Insert(id, record.Vector);
            _scalarIndex.Add(id, record.Metadata);

            _logger?.LogDebug("集合 '{Name}' 插入记录 {Id}", Name, id);
        }

        return Task.FromResult(record.Id);
    }

    public Task<IReadOnlyList<ulong>> InsertBatchAsync(IEnumerable<VectorRecord> records,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var ids = new List<ulong>();

        lock (_writeLock)
        {
            foreach (var record in records)
            {
                ct.ThrowIfCancellationRequested();

                if (record.Vector.Length != Dimensions)
                    throw new DimensionMismatchException(Dimensions, record.Vector.Length);

                var id = _nextRecordId++;
                record.Id = id;

                _records[id] = record;
                _hnswIndex.Insert(id, record.Vector);
                _scalarIndex.Add(id, record.Metadata);
                ids.Add(id);
            }
        }

        return Task.FromResult<IReadOnlyList<ulong>>(ids);
    }

    public Task<VectorRecord?> GetAsync(ulong id, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        _records.TryGetValue(id, out var record);
        return Task.FromResult(record);
    }

    public Task<bool> DeleteAsync(ulong id, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        lock (_writeLock)
        {
            if (!_records.TryGetValue(id, out var record))
                return Task.FromResult(false);

            _records.Remove(id);
            _hnswIndex.MarkDeleted(id);
            _scalarIndex.Remove(id);
            return Task.FromResult(true);
        }
    }

    public IQueryBuilder Query(float[] queryVector)
    {
        if (queryVector.Length != Dimensions)
            throw new DimensionMismatchException(Dimensions, queryVector.Length);

        return new QueryBuilder(this, queryVector);
    }

    /// <summary>由 QueryBuilder 调用，执行实际的混合查询</summary>
    internal IReadOnlyList<SearchResult> ExecuteQuery(
        float[] queryVector, int topK, int? efSearch,
        FilterExpression? filter, float minScore)
    {
        var ef = efSearch ?? _efSearch;
        var rawResults = _queryEngine.Search(queryVector, topK, ef, filter);

        var results = new List<SearchResult>();
        foreach (var (recordId, distance) in rawResults)
        {
            if (!_records.TryGetValue(recordId, out var record))
                continue;

            var result = new SearchResult
            {
                Record = record,
                Distance = distance
            };

            if (minScore > 0f && result.Score < minScore)
                continue;

            results.Add(result);
        }

        return results;
    }

    /// <summary>获取 HNSW 索引（用于序列化等）</summary>
    internal HNSWIndex HnswIndex => _hnswIndex;

    /// <summary>获取标量索引（用于序列化等）</summary>
    internal ScalarIndex ScalarIndexInstance => _scalarIndex;

    /// <summary>获取所有记录（用于序列化等）</summary>
    internal IReadOnlyDictionary<ulong, VectorRecord> Records => _records;
}
