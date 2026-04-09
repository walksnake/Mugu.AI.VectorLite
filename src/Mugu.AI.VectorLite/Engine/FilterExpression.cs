namespace Mugu.AI.VectorLite.Engine;

/// <summary>过滤表达式基类</summary>
public abstract class FilterExpression
{
    /// <summary>对标量索引求值，返回符合条件的记录ID集合</summary>
    internal abstract HashSet<ulong> Evaluate(ScalarIndex index);
}

/// <summary>精确匹配过滤</summary>
public sealed class EqualFilter : FilterExpression
{
    public string Field { get; }
    public object Value { get; }

    public EqualFilter(string field, object value)
    {
        Field = field ?? throw new ArgumentNullException(nameof(field));
        Value = value ?? throw new ArgumentNullException(nameof(value));
    }

    internal override HashSet<ulong> Evaluate(ScalarIndex index)
        => index.GetRecordIds(Field, Value);
}

/// <summary>不等于过滤</summary>
public sealed class NotEqualFilter : FilterExpression
{
    public string Field { get; }
    public object Value { get; }

    public NotEqualFilter(string field, object value)
    {
        Field = field ?? throw new ArgumentNullException(nameof(field));
        Value = value ?? throw new ArgumentNullException(nameof(value));
    }

    internal override HashSet<ulong> Evaluate(ScalarIndex index)
    {
        var all = index.GetAllRecordIds();
        var matched = index.GetRecordIds(Field, Value);
        all.ExceptWith(matched);
        return all;
    }
}

/// <summary>集合包含过滤</summary>
public sealed class InFilter : FilterExpression
{
    public string Field { get; }
    public IReadOnlyList<object> Values { get; }

    public InFilter(string field, IReadOnlyList<object> values)
    {
        Field = field ?? throw new ArgumentNullException(nameof(field));
        Values = values ?? throw new ArgumentNullException(nameof(values));
    }

    internal override HashSet<ulong> Evaluate(ScalarIndex index)
    {
        var result = new HashSet<ulong>();
        foreach (var value in Values)
        {
            result.UnionWith(index.GetRecordIds(Field, value));
        }
        return result;
    }
}

/// <summary>范围过滤（仅数值类型）</summary>
public sealed class RangeFilter : FilterExpression
{
    public string Field { get; }
    public IComparable? LowerBound { get; }
    public IComparable? UpperBound { get; }
    public bool LowerInclusive { get; }
    public bool UpperInclusive { get; }

    public RangeFilter(string field,
        IComparable? lowerBound = null,
        IComparable? upperBound = null,
        bool lowerInclusive = true,
        bool upperInclusive = false)
    {
        Field = field ?? throw new ArgumentNullException(nameof(field));
        LowerBound = lowerBound;
        UpperBound = upperBound;
        LowerInclusive = lowerInclusive;
        UpperInclusive = upperInclusive;
    }

    internal override HashSet<ulong> Evaluate(ScalarIndex index)
        => index.GetRecordIdsByRange(Field, LowerBound, UpperBound, LowerInclusive, UpperInclusive);
}

/// <summary>逻辑与</summary>
public sealed class AndFilter : FilterExpression
{
    public IReadOnlyList<FilterExpression> Operands { get; }

    public AndFilter(params FilterExpression[] operands)
    {
        Operands = operands;
    }

    internal override HashSet<ulong> Evaluate(ScalarIndex index)
    {
        HashSet<ulong>? result = null;
        foreach (var operand in Operands)
        {
            var ids = operand.Evaluate(index);
            if (result == null)
                result = ids;
            else
                result.IntersectWith(ids);

            if (result.Count == 0) break;
        }
        return result ?? [];
    }
}

/// <summary>逻辑或</summary>
public sealed class OrFilter : FilterExpression
{
    public IReadOnlyList<FilterExpression> Operands { get; }

    public OrFilter(params FilterExpression[] operands)
    {
        Operands = operands;
    }

    internal override HashSet<ulong> Evaluate(ScalarIndex index)
    {
        var result = new HashSet<ulong>();
        foreach (var operand in Operands)
        {
            result.UnionWith(operand.Evaluate(index));
        }
        return result;
    }
}

/// <summary>逻辑非</summary>
public sealed class NotFilter : FilterExpression
{
    public FilterExpression Operand { get; }

    public NotFilter(FilterExpression operand)
    {
        Operand = operand ?? throw new ArgumentNullException(nameof(operand));
    }

    internal override HashSet<ulong> Evaluate(ScalarIndex index)
    {
        var all = index.GetAllRecordIds();
        var matched = Operand.Evaluate(index);
        all.ExceptWith(matched);
        return all;
    }
}
