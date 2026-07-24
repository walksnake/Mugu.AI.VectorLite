namespace Mugu.AI.VectorLite.Engine;

/// <summary>单个标量排序定义。</summary>
internal sealed record ScalarSort(string Field, SortDirection Direction);

/// <summary>纯标量查询的不可变请求。</summary>
internal sealed record ScalarQueryRequest(
    FilterExpression? Filter,
    IReadOnlyList<ScalarSort> Sorts,
    int TopK,
    RecordProjection Projection,
    ScalarQueryCursor? Cursor,
    bool IncludeNextCursor);

/// <summary>纯标量查询执行结果。</summary>
internal sealed record ScalarQueryResult(
    IReadOnlyList<ulong> RecordIds,
    ScalarQueryCursor? NextCursor);

/// <summary>执行过滤、稳定排序与有界 TopK，不访问 HNSW。</summary>
internal sealed class ScalarQueryEngine
{
    private readonly ScalarIndex _index;

    internal ScalarQueryEngine(ScalarIndex index) => _index = index;

    internal ScalarQueryResult Execute(ScalarQueryRequest request, CancellationToken ct)
    {
        ValidateCursor(request);
        var candidates = request.Filter is null
            ? _index.GetAllRecordIds()
            : _index.Filter(request.Filter);
        var take = request.IncludeNextCursor ? request.TopK + 1 : request.TopK;
        var ids = SelectTop(candidates, request, take, ct);
        var hasMore = request.IncludeNextCursor && ids.Count > request.TopK;
        if (hasMore)
            ids.RemoveAt(ids.Count - 1);
        return new ScalarQueryResult(ids, hasMore ? CreateCursor(ids[^1], request.Sorts) : null);
    }

    private List<ulong> SelectTop(
        IEnumerable<ulong> candidates,
        ScalarQueryRequest request,
        int take,
        CancellationToken ct)
    {
        var comparer = new RecordComparer(_index, request.Sorts);
        var heapComparer = Comparer<ulong>.Create((left, right) => -comparer.Compare(left, right));
        var heap = new PriorityQueue<ulong, ulong>(heapComparer);
        foreach (var id in candidates)
        {
            ct.ThrowIfCancellationRequested();
            if (!IsAfterCursor(id, request, comparer))
                continue;
            heap.Enqueue(id, id);
            if (heap.Count > take)
                heap.Dequeue();
        }
        var result = heap.UnorderedItems.Select(item => item.Element).ToList();
        result.Sort(comparer);
        return result;
    }

    private bool IsAfterCursor(
        ulong id,
        ScalarQueryRequest request,
        RecordComparer comparer)
    {
        if (request.Cursor is null)
            return true;
        return comparer.CompareToCursor(id, request.Cursor) > 0;
    }

    private void ValidateCursor(ScalarQueryRequest request)
    {
        if (request.Cursor is null)
            return;
        var fields = request.Sorts.Select(sort => sort.Field);
        var directions = request.Sorts.Select(sort => sort.Direction);
        if (!fields.SequenceEqual(request.Cursor.Fields)
            || !directions.SequenceEqual(request.Cursor.Directions)
            || request.Cursor.Values.Count != request.Sorts.Count)
        {
            throw new ArgumentException("分页游标与当前排序定义不匹配", nameof(request));
        }
    }

    private ScalarQueryCursor CreateCursor(ulong id, IReadOnlyList<ScalarSort> sorts)
    {
        var metadata = _index.GetRecordMetadataView(id);
        return new ScalarQueryCursor
        {
            Fields = sorts.Select(sort => sort.Field).ToArray(),
            Directions = sorts.Select(sort => sort.Direction).ToArray(),
            Values = sorts.Select(sort => ReadValue(metadata, sort.Field)).ToArray(),
            RecordId = id
        };
    }

    private static object? ReadValue(IReadOnlyDictionary<string, object>? metadata, string field)
        => metadata?.GetValueOrDefault(field);

    /// <summary>按全部排序键比较，并始终以记录 ID 作为最终稳定键。</summary>
    private sealed class RecordComparer : IComparer<ulong>
    {
        private readonly ScalarIndex _index;
        private readonly IReadOnlyList<ScalarSort> _sorts;

        internal RecordComparer(ScalarIndex index, IReadOnlyList<ScalarSort> sorts)
        {
            _index = index;
            _sorts = sorts;
        }

        public int Compare(ulong left, ulong right)
        {
            var leftMetadata = _index.GetRecordMetadataView(left);
            var rightMetadata = _index.GetRecordMetadataView(right);
            for (var i = 0; i < _sorts.Count; i++)
            {
                var sort = _sorts[i];
                var comparison = CompareValues(
                    ReadValue(leftMetadata, sort.Field),
                    ReadValue(rightMetadata, sort.Field));
                if (comparison != 0)
                    return ApplyDirection(comparison, sort.Direction);
            }
            return left.CompareTo(right);
        }

        internal int CompareToCursor(ulong id, ScalarQueryCursor cursor)
        {
            var metadata = _index.GetRecordMetadataView(id);
            for (var i = 0; i < _sorts.Count; i++)
            {
                var comparison = CompareValues(
                    ReadValue(metadata, _sorts[i].Field),
                    cursor.Values[i]);
                if (comparison != 0)
                    return ApplyDirection(comparison, _sorts[i].Direction);
            }
            return id.CompareTo(cursor.RecordId);
        }

        private static int CompareValues(object? left, object? right)
            => ScalarValueComparer.Instance.Compare(left, right);

        private static int ApplyDirection(int value, SortDirection direction)
            => direction == SortDirection.Ascending ? value : -value;
    }
}
