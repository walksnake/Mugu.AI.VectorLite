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
    /// <summary>过滤字段名</summary>
    public string Field { get; }
    /// <summary>过滤值</summary>
    public object Value { get; }

    /// <summary>创建精确匹配过滤条件</summary>
    /// <param name="field">字段名</param>
    /// <param name="value">匹配值</param>
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
    /// <summary>过滤字段名</summary>
    public string Field { get; }
    /// <summary>过滤值</summary>
    public object Value { get; }

    /// <summary>创建不等于过滤条件</summary>
    /// <param name="field">字段名</param>
    /// <param name="value">排除值</param>
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
    /// <summary>过滤字段名</summary>
    public string Field { get; }
    /// <summary>候选值集合</summary>
    public IReadOnlyList<object> Values { get; }

    /// <summary>创建集合包含过滤条件</summary>
    /// <param name="field">字段名</param>
    /// <param name="values">候选值列表</param>
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
    /// <summary>过滤字段名</summary>
    public string Field { get; }
    /// <summary>下界（null 表示无下界）</summary>
    public IComparable? LowerBound { get; }
    /// <summary>上界（null 表示无上界）</summary>
    public IComparable? UpperBound { get; }
    /// <summary>是否包含下界</summary>
    public bool LowerInclusive { get; }
    /// <summary>是否包含上界</summary>
    public bool UpperInclusive { get; }

    /// <summary>创建范围过滤条件</summary>
    /// <param name="field">字段名</param>
    /// <param name="lowerBound">下界（null 表示无下界）</param>
    /// <param name="upperBound">上界（null 表示无上界）</param>
    /// <param name="lowerInclusive">是否包含下界，默认 true</param>
    /// <param name="upperInclusive">是否包含上界，默认 false</param>
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
    /// <summary>子表达式列表</summary>
    public IReadOnlyList<FilterExpression> Operands { get; }

    /// <summary>创建逻辑与过滤条件</summary>
    /// <param name="operands">子表达式</param>
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
    /// <summary>子表达式列表</summary>
    public IReadOnlyList<FilterExpression> Operands { get; }

    /// <summary>创建逻辑或过滤条件</summary>
    /// <param name="operands">子表达式</param>
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
    /// <summary>被取反的子表达式</summary>
    public FilterExpression Operand { get; }

    /// <summary>创建逻辑非过滤条件</summary>
    /// <param name="operand">被取反的表达式</param>
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
