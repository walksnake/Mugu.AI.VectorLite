using Mugu.AI.VectorLite.Engine;
using Mugu.AI.VectorLite.Engine.Distance;

namespace Mugu.AI.VectorLite;

/// <summary>
/// 查询构建器实现：通过 Fluent API 构建混合查询条件并执行。
/// </summary>
internal sealed class QueryBuilder : IQueryBuilder
{
    private readonly Collection _collection;
    private readonly float[] _queryVector;
    private readonly List<FilterExpression> _filters = new();
    private int _topK = 10;
    private float _minScore = 0f;
    private int? _efSearch;

    internal QueryBuilder(Collection collection, float[] queryVector)
    {
        _collection = collection;
        _queryVector = queryVector;
    }

    public IQueryBuilder Where(string field, object value)
    {
        _filters.Add(new EqualFilter(field, value));
        return this;
    }

    public IQueryBuilder Where(FilterExpression filter)
    {
        _filters.Add(filter);
        return this;
    }

    public IQueryBuilder TopK(int k)
    {
        if (k < 1) throw new ArgumentOutOfRangeException(nameof(k), "TopK 必须 >= 1");
        _topK = k;
        return this;
    }

    public IQueryBuilder WithMinScore(float minScore)
    {
        _minScore = minScore;
        return this;
    }

    public IQueryBuilder WithEfSearch(int efSearch)
    {
        if (efSearch < 1) throw new ArgumentOutOfRangeException(nameof(efSearch), "efSearch 必须 >= 1");
        _efSearch = efSearch;
        return this;
    }

    public Task<IReadOnlyList<SearchResult>> ToListAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        // 合并过滤条件
        FilterExpression? filter = _filters.Count switch
        {
            0 => null,
            1 => _filters[0],
            _ => new AndFilter(_filters.ToArray())
        };

        var results = _collection.ExecuteQuery(
            _queryVector, _topK, _efSearch, filter, _minScore);

        return Task.FromResult(results);
    }
}
