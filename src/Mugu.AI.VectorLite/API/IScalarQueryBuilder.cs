using Mugu.AI.VectorLite.Engine;

namespace Mugu.AI.VectorLite;

/// <summary>纯标量查询构建器。</summary>
public interface IScalarQueryBuilder
{
    /// <summary>添加过滤条件。</summary>
    IScalarQueryBuilder Where(FilterExpression filter);
    /// <summary>添加精确匹配条件。</summary>
    IScalarQueryBuilder Where(string field, object value);
    /// <summary>添加首个排序字段。</summary>
    IScalarQueryBuilder OrderBy(string field, SortDirection direction);
    /// <summary>添加后续排序字段。</summary>
    IScalarQueryBuilder ThenBy(string field, SortDirection direction);
    /// <summary>限制返回数量。</summary>
    IScalarQueryBuilder TopK(int count);
    /// <summary>设置返回投影。</summary>
    IScalarQueryBuilder Select(RecordProjection projection);
    /// <summary>从稳定游标之后继续查询。</summary>
    IScalarQueryBuilder After(ScalarQueryCursor cursor);
    /// <summary>执行查询。</summary>
    Task<IReadOnlyList<RecordView>> ToListAsync(CancellationToken ct = default);
    /// <summary>执行分页查询。</summary>
    Task<ScalarQueryPage> ToPageAsync(CancellationToken ct = default);
}
