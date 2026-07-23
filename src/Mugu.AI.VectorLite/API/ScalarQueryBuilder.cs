using Mugu.AI.VectorLite.Engine;

namespace Mugu.AI.VectorLite;

/// <summary>收集纯标量查询参数，并把执行委托给集合。</summary>
internal sealed class ScalarQueryBuilder : IScalarQueryBuilder
{
    private readonly Collection _collection;
    private readonly List<ScalarSort> _sorts = [];
    private FilterExpression? _filter;
    private int _topK = 10;
    private RecordProjection _projection = RecordProjection.All;
    private ScalarQueryCursor? _cursor;

    internal ScalarQueryBuilder(Collection collection) => _collection = collection;

    public IScalarQueryBuilder Where(FilterExpression filter)
    {
        ArgumentNullException.ThrowIfNull(filter);
        _filter = _filter is null ? filter : new AndFilter(_filter, filter);
        return this;
    }

    public IScalarQueryBuilder Where(string field, object value)
        => Where(new EqualFilter(field, value));

    public IScalarQueryBuilder OrderBy(string field, SortDirection direction)
    {
        _sorts.Clear();
        return AddSort(field, direction);
    }

    public IScalarQueryBuilder ThenBy(string field, SortDirection direction)
        => AddSort(field, direction);

    public IScalarQueryBuilder TopK(int count)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(count, 1);
        _topK = count;
        return this;
    }

    public IScalarQueryBuilder Select(RecordProjection projection)
    {
        if ((projection & ~RecordProjection.All) != 0)
            throw new ArgumentOutOfRangeException(nameof(projection));
        _projection = projection;
        return this;
    }

    public IScalarQueryBuilder After(ScalarQueryCursor cursor)
    {
        _cursor = cursor ?? throw new ArgumentNullException(nameof(cursor));
        return this;
    }

    public Task<IReadOnlyList<RecordView>> ToListAsync(CancellationToken ct = default)
        => _collection.ExecuteScalarQueryAsync(BuildRequest(false), ct)
            .ContinueWith<IReadOnlyList<RecordView>>(
                task => task.GetAwaiter().GetResult().Records,
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);

    public Task<ScalarQueryPage> ToPageAsync(CancellationToken ct = default)
        => _collection.ExecuteScalarQueryAsync(BuildRequest(true), ct);

    private IScalarQueryBuilder AddSort(string field, SortDirection direction)
    {
        if (string.IsNullOrWhiteSpace(field))
            throw new ArgumentException("排序字段不能为空", nameof(field));
        _sorts.Add(new ScalarSort(field, direction));
        return this;
    }

    private ScalarQueryRequest BuildRequest(bool includeNextCursor)
        => new(_filter, _sorts.ToArray(), _topK, _projection, _cursor, includeNextCursor);
}
