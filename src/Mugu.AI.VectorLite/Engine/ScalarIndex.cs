namespace Mugu.AI.VectorLite.Engine;

/// <summary>
/// 标量倒排索引：支持对元数据字段的快速过滤查询。
/// 数据结构：字段名 → 字段值 → 记录ID集合。
/// 非线程安全：调用方必须在 Collection._rwLock 保护下访问。
/// </summary>
internal sealed class ScalarIndex
{
    // 字段名 → (字段值 → 记录ID集合)
    private readonly Dictionary<string, Dictionary<object, HashSet<ulong>>> _index = new();

    // 记录ID → 其元数据（用于删除时反向查找）
    private readonly Dictionary<ulong, Dictionary<string, object>> _recordMetadata = new();
    private readonly Dictionary<string, ScalarIndexDefinition> _definitions = new();
    private readonly Dictionary<string, SortedDictionary<object, HashSet<ulong>>> _orderedIndexes = new();

    /// <summary>为一条记录的所有元数据字段建立索引</summary>
    internal void Add(ulong recordId, Dictionary<string, object>? metadata)
    {
        if (metadata == null || metadata.Count == 0) return;
        AddRecord(recordId, metadata);
    }

    /// <summary>登记记录并为现有元数据建索引，允许空元数据记录参与纯标量查询。</summary>
    internal void AddRecord(ulong recordId, Dictionary<string, object>? metadata)
    {
        _recordMetadata[recordId] = metadata is null
            ? []
            : new Dictionary<string, object>(metadata);
        if (metadata == null || metadata.Count == 0) return;

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
            AddToOrderedIndex(recordId, field, value);
        }
    }

    /// <summary>移除一条记录的所有索引条目</summary>
    internal void Remove(ulong recordId)
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

            if (_orderedIndexes.TryGetValue(field, out var ordered)
                && ordered.TryGetValue(value, out var orderedIds))
            {
                orderedIds.Remove(recordId);
                if (orderedIds.Count == 0)
                    ordered.Remove(value);
            }
        }

        _recordMetadata.Remove(recordId);
    }

    /// <summary>通过过滤表达式求值（调用方持有 Collection._rwLock 读锁）</summary>
    internal HashSet<ulong> Filter(FilterExpression expression)
    {
        return expression.Evaluate(this);
    }

    /// <summary>获取指定字段值的记录ID集合（由 FilterExpression.Evaluate 调用）</summary>
    internal HashSet<ulong> GetRecordIds(string field, object value)
    {
        if (_index.TryGetValue(field, out var fieldIndex) &&
            fieldIndex.TryGetValue(value, out var ids))
        {
            return new HashSet<ulong>(ids);
        }
        return [];
    }

    /// <summary>获取 Posting 只读视图，供只读 API 避免中间集合复制。</summary>
    internal IReadOnlyCollection<ulong> GetRecordIdsView(string field, object value)
    {
        if (_index.TryGetValue(field, out var fieldIndex)
            && fieldIndex.TryGetValue(value, out var ids))
        {
            return ids;
        }
        return Array.Empty<ulong>();
    }

    /// <summary>获取所有已索引的记录ID（由 FilterExpression.Evaluate 调用）</summary>
    internal HashSet<ulong> GetAllRecordIds()
        => new(_recordMetadata.Keys);

    /// <summary>范围查询：返回字段值在指定范围内的记录ID集合</summary>
    internal HashSet<ulong> GetRecordIdsByRange(
        string field,
        IComparable? lowerBound,
        IComparable? upperBound,
        bool lowerInclusive,
        bool upperInclusive)
    {
        if (_orderedIndexes.TryGetValue(field, out var orderedIndex))
            return ReadOrderedRange(
                orderedIndex, lowerBound, upperBound, lowerInclusive, upperInclusive);

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
                var cmp = SafeCompareTo(comparable, lowerBound);
                if (cmp == null) continue;
                inRange = lowerInclusive ? cmp >= 0 : cmp > 0;
            }

            if (inRange && upperBound != null)
            {
                var cmp = SafeCompareTo(comparable, upperBound);
                if (cmp == null) continue;
                inRange = upperInclusive ? cmp <= 0 : cmp < 0;
            }

            if (inRange)
                result.UnionWith(ids);
        }

        return result;
    }

    /// <summary>
    /// 类型安全的比较：将数值类型归一化为 double 后比较，避免跨类型 CompareTo 抛异常。
    /// 返回 null 表示不可比较。
    /// </summary>
    private static int? SafeCompareTo(IComparable a, IComparable b)
    {
        if (a.GetType() == b.GetType())
            return a.CompareTo(b);

        if (TryToDouble(a, out var da) && TryToDouble(b, out var db))
            return da.CompareTo(db);

        try
        {
            return a.CompareTo(b);
        }
        catch
        {
            return null;
        }
    }

    private static bool TryToDouble(object value, out double result)
    {
        switch (value)
        {
            case long l: result = l; return true;
            case int i: result = i; return true;
            case double d: result = d; return true;
            case float f: result = f; return true;
            case short s: result = s; return true;
            case byte b: result = b; return true;
            case decimal m: result = (double)m; return true;
            default: result = 0; return false;
        }
    }

    /// <summary>记录总数</summary>
    internal int Count => _recordMetadata.Count;

    /// <summary>获取所有记录的元数据副本（用于序列化，调用方持有读锁）</summary>
    internal IReadOnlyDictionary<ulong, Dictionary<string, object>> RecordMetadata
        => new Dictionary<ulong, Dictionary<string, object>>(_recordMetadata);

    /// <summary>获取指定记录的元数据副本（用于按需组装记录）</summary>
    internal Dictionary<string, object>? GetRecordMetadata(ulong recordId)
    {
        return _recordMetadata.TryGetValue(recordId, out var metadata)
            ? new Dictionary<string, object>(metadata)
            : null;
    }

    /// <summary>获取内部元数据只读视图，避免查询排序阶段复制字典。</summary>
    internal IReadOnlyDictionary<string, object>? GetRecordMetadataView(ulong recordId)
        => _recordMetadata.GetValueOrDefault(recordId);

    /// <summary>估算表达式结果数量，用于 AND 条件重排。</summary>
    internal int EstimateCount(FilterExpression expression)
    {
        return expression switch
        {
            EqualFilter equal => GetPostingCount(equal.Field, equal.Value),
            InFilter inFilter => inFilter.Values.Sum(value => GetPostingCount(inFilter.Field, value)),
            AndFilter andFilter => andFilter.Operands.Count == 0
                ? 0
                : andFilter.Operands.Min(EstimateCount),
            OrFilter orFilter => Math.Min(Count, orFilter.Operands.Sum(EstimateCount)),
            _ => Count
        };
    }

    /// <summary>创建显式索引并用已有元数据回填。</summary>
    internal void CreateIndex(ScalarIndexDefinition definition)
    {
        ValidateDefinition(definition);
        if (_definitions.ContainsKey(definition.Name))
            throw new ArgumentException($"标量索引 '{definition.Name}' 已存在", nameof(definition));

        _definitions[definition.Name] = definition;
        if (definition.Type == ScalarIndexType.Ordered)
            BuildOrderedIndex(definition.Fields[0]);
    }

    /// <summary>加载持久化索引定义。</summary>
    internal void LoadDefinitions(IEnumerable<ScalarIndexDefinition> definitions)
    {
        _definitions.Clear();
        _orderedIndexes.Clear();
        foreach (var definition in definitions)
        {
            _definitions[definition.Name] = definition;
            if (definition.Type == ScalarIndexType.Ordered)
                BuildOrderedIndex(definition.Fields[0]);
        }
    }

    /// <summary>返回显式索引定义快照。</summary>
    internal IReadOnlyList<ScalarIndexDefinition> Definitions
        => _definitions.Values.ToArray();

    /// <summary>批量加载元数据并重建倒排索引（反序列化时调用）</summary>
    internal void BulkLoad(Dictionary<ulong, Dictionary<string, object>> recordMetadata)
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

    private int GetPostingCount(string field, object value)
        => _index.TryGetValue(field, out var fields)
           && fields.TryGetValue(value, out var ids)
            ? ids.Count
            : 0;

    private void AddToOrderedIndex(ulong recordId, string field, object value)
    {
        if (!_orderedIndexes.TryGetValue(field, out var index))
            return;
        if (!index.TryGetValue(value, out var ids))
        {
            ids = [];
            index[value] = ids;
        }
        ids.Add(recordId);
    }

    private void BuildOrderedIndex(string field)
    {
        var ordered = new SortedDictionary<object, HashSet<ulong>>(ScalarValueComparer.Instance);
        _orderedIndexes[field] = ordered;
        if (!_index.TryGetValue(field, out var values))
            return;
        foreach (var (value, ids) in values)
        {
            if (!ordered.TryGetValue(value, out var orderedIds))
            {
                orderedIds = [];
                ordered[value] = orderedIds;
            }
            orderedIds.UnionWith(ids);
        }
    }

    private static HashSet<ulong> ReadOrderedRange(
        SortedDictionary<object, HashSet<ulong>> index,
        IComparable? lower,
        IComparable? upper,
        bool lowerInclusive,
        bool upperInclusive)
    {
        var result = new HashSet<ulong>();
        foreach (var (value, ids) in index)
        {
            if (!IsWithinLower(value, lower, lowerInclusive))
                continue;
            if (!IsWithinUpper(value, upper, upperInclusive))
                break;
            result.UnionWith(ids);
        }
        return result;
    }

    private static bool IsWithinLower(object value, IComparable? lower, bool inclusive)
    {
        if (lower is null)
            return true;
        var comparison = ScalarValueComparer.Instance.Compare(value, lower);
        return inclusive ? comparison >= 0 : comparison > 0;
    }

    private static bool IsWithinUpper(object value, IComparable? upper, bool inclusive)
    {
        if (upper is null)
            return true;
        var comparison = ScalarValueComparer.Instance.Compare(value, upper);
        return inclusive ? comparison <= 0 : comparison < 0;
    }

    private static void ValidateDefinition(ScalarIndexDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (string.IsNullOrWhiteSpace(definition.Name))
            throw new ArgumentException("索引名称不能为空", nameof(definition));
        if (definition.Fields.Count == 0 || definition.Fields.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("索引字段不能为空", nameof(definition));
        if (definition.Type == ScalarIndexType.Ordered && definition.Fields.Count != 1)
            throw new ArgumentException("有序索引只能包含一个字段", nameof(definition));
    }
}

/// <summary>统一标量值比较语义，数值类型按 double 归一化。</summary>
internal sealed class ScalarValueComparer : IComparer<object>
{
    internal static ScalarValueComparer Instance { get; } = new();

    public int Compare(object? x, object? y)
    {
        if (ReferenceEquals(x, y)) return 0;
        if (x is null) return -1;
        if (y is null) return 1;
        if (TryNumber(x, out var left) && TryNumber(y, out var right))
            return left.CompareTo(right);
        if (x.GetType() == y.GetType() && x is IComparable comparable)
            return comparable.CompareTo(y);
        return StringComparer.Ordinal.Compare(x.ToString(), y.ToString());
    }

    private static bool TryNumber(object value, out double number)
    {
        if (value is byte or short or int or long or float or double or decimal)
        {
            number = Convert.ToDouble(value, System.Globalization.CultureInfo.InvariantCulture);
            return true;
        }
        number = 0;
        return false;
    }
}
