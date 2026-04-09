using Mugu.AI.VectorLite.Engine;

namespace Mugu.AI.VectorLite;

/// <summary>查询构建器接口（Fluent API）</summary>
public interface IQueryBuilder
{
    /// <summary>添加精确匹配过滤条件</summary>
    IQueryBuilder Where(string field, object value);

    /// <summary>添加自定义过滤表达式</summary>
    IQueryBuilder Where(FilterExpression filter);

    /// <summary>设置返回结果数量</summary>
    IQueryBuilder TopK(int k);

    /// <summary>设置最小相似度得分（仅对余弦距离有意义）</summary>
    IQueryBuilder WithMinScore(float minScore);

    /// <summary>设置搜索时的 ef 参数（覆盖默认值）</summary>
    IQueryBuilder WithEfSearch(int efSearch);

    /// <summary>执行查询并返回结果列表</summary>
    Task<IReadOnlyList<SearchResult>> ToListAsync(CancellationToken ct = default);
}
