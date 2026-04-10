namespace Mugu.AI.VectorLite.Engine;

/// <summary>
/// 标量倒排索引：支持对元数据字段的快速过滤查询。
/// 数据结构：字段名 → 字段值 → 记录ID集合。
/// 内部使用 ReaderWriterLockSlim 保证并发安全。
/// </summary>
internal sealed class ScalarIndex
{
    // 字段名 → (字段值 → 记录ID集合)
    private readonly Dictionary<string, Dictionary<object, HashSet<ulong>>> _index = new();

    // 记录ID → 其元数据（用于删除时反向查找）
    private readonly Dictionary<ulong, Dictionary<string, object>> _recordMetadata = new();

    private readonly ReaderWriterLockSlim _rwLock = new();

    /// <summary>为一条记录的所有元数据字段建立索引</summary>
    internal void Add(ulong recordId, Dictionary<string, object>? metadata)
    {
        if (metadata == null || metadata.Count == 0) return;

        _rwLock.EnterWriteLock();
        try
        {
            _recordMetadata[recordId] = new Dictionary<string, object>(metadata);

            foreach (var (field, value) in metadata)
            {
                if (!_index.TryGetValue(field, out var fieldIndex))
                {
                    fieldIndex = new Dictionary<object, HashSet<ulong>>();
                    _index[field] = fieldIndex;
                }

                if (!fieldIndex.TryGetValue(value, out var ids))
                {
                    ids = new HashSet<ulong>();
                    fieldIndex[value] = ids;
                }

                ids.Add(recordId);
            }
        }
        finally
        {
            _rwLock.ExitWriteLock();
        }
    }

    /// <summary>移除一条记录的所有索引条目</summary>
    internal void Remove(ulong recordId)
    {
        _rwLock.EnterWriteLock();
        try
        {
            if (!_recordMetadata.TryGetValue(recordId, out var metadata))
                return;

            foreach (var (field, value) in metadata)
            {
                if (_index.TryGetValue(field, out var fieldIndex) &&
                    fieldIndex.TryGetValue(value, out var ids))
                {
                    ids.Remove(recordId);
                    if (ids.Count == 0)
                        fieldIndex.Remove(value);
                    if (fieldIndex.Count == 0)
                        _index.Remove(field);
                }
            }

            _recordMetadata.Remove(recordId);
        }
        finally
        {
            _rwLock.ExitWriteLock();
        }
    }

    /// <summary>通过过滤表达式求值（内部自动获取读锁）</summary>
    internal HashSet<ulong> Filter(FilterExpression expression)
    {
        _rwLock.EnterReadLock();
        try
        {
            return expression.Evaluate(this);
        }
        finally
        {
            _rwLock.ExitReadLock();
        }
    }

    /// <summary>获取指定字段值的记录ID集合（由 FilterExpression.Evaluate 在读锁内调用）</summary>
    internal HashSet<ulong> GetRecordIds(string field, object value)
    {
        if (_index.TryGetValue(field, out var fieldIndex) &&
            fieldIndex.TryGetValue(value, out var ids))
        {
            return new HashSet<ulong>(ids);
        }
        return [];
    }

    /// <summary>获取所有已索引的记录ID（由 FilterExpression.Evaluate 在读锁内调用）</summary>
    internal HashSet<ulong> GetAllRecordIds()
        => new(_recordMetadata.Keys);

    /// <summary>范围查询：返回字段值在指定范围内的记录ID集合（由 FilterExpression.Evaluate 在读锁内调用）</summary>
    internal HashSet<ulong> GetRecordIdsByRange(
        string field,
        IComparable? lowerBound,
        IComparable? upperBound,
        bool lowerInclusive,
        bool upperInclusive)
    {
        if (!_index.TryGetValue(field, out var fieldIndex))
            return [];

        var result = new HashSet<ulong>();

        foreach (var (value, ids) in fieldIndex)
        {
            if (value is not IComparable comparable)
                continue;

            var inRange = true;

            if (lowerBound != null)
            {
                var cmp = comparable.CompareTo(lowerBound);
                inRange = lowerInclusive ? cmp >= 0 : cmp > 0;
            }

            if (inRange && upperBound != null)
            {
                var cmp = comparable.CompareTo(upperBound);
                inRange = upperInclusive ? cmp <= 0 : cmp < 0;
            }

            if (inRange)
                result.UnionWith(ids);
        }

        return result;
    }

    /// <summary>记录总数</summary>
    internal int Count
    {
        get
        {
            _rwLock.EnterReadLock();
            try
            {
                return _recordMetadata.Count;
            }
            finally
            {
                _rwLock.ExitReadLock();
            }
        }
    }

    /// <summary>获取所有记录的元数据（用于序列化）</summary>
    internal IReadOnlyDictionary<ulong, Dictionary<string, object>> RecordMetadata
    {
        get
        {
            _rwLock.EnterReadLock();
            try
            {
                return new Dictionary<ulong, Dictionary<string, object>>(_recordMetadata);
            }
            finally
            {
                _rwLock.ExitReadLock();
            }
        }
    }

    /// <summary>获取指定记录的元数据副本（用于按需组装记录）</summary>
    internal Dictionary<string, object>? GetRecordMetadata(ulong recordId)
    {
        _rwLock.EnterReadLock();
        try
        {
            return _recordMetadata.TryGetValue(recordId, out var metadata)
                ? new Dictionary<string, object>(metadata)
                : null;
        }
        finally
        {
            _rwLock.ExitReadLock();
        }
    }

    /// <summary>批量加载元数据并重建倒排索引（反序列化时调用）</summary>
    internal void BulkLoad(Dictionary<ulong, Dictionary<string, object>> recordMetadata)
    {
        _rwLock.EnterWriteLock();
        try
        {
            _recordMetadata.Clear();
            _index.Clear();

            foreach (var (recordId, metadata) in recordMetadata)
            {
                _recordMetadata[recordId] = new Dictionary<string, object>(metadata);

                foreach (var (field, value) in metadata)
                {
                    if (!_index.TryGetValue(field, out var fieldIndex))
                    {
                        fieldIndex = new Dictionary<object, HashSet<ulong>>();
                        _index[field] = fieldIndex;
                    }

                    if (!fieldIndex.TryGetValue(value, out var ids))
                    {
                        ids = new HashSet<ulong>();
                        fieldIndex[value] = ids;
                    }

                    ids.Add(recordId);
                }
            }
        }
        finally
        {
            _rwLock.ExitWriteLock();
        }
    }
}
