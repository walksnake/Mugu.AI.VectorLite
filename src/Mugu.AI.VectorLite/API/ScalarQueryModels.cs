namespace Mugu.AI.VectorLite;

/// <summary>记录投影字段。</summary>
[Flags]
public enum RecordProjection
{
    /// <summary>仅返回记录 ID。</summary>
    Id = 0,
    /// <summary>返回元数据。</summary>
    Metadata = 1,
    /// <summary>返回文本。</summary>
    Text = 2,
    /// <summary>返回向量。</summary>
    Vector = 4,
    /// <summary>返回全部字段。</summary>
    All = Metadata | Text | Vector
}

/// <summary>排序方向。</summary>
public enum SortDirection
{
    /// <summary>升序。</summary>
    Ascending,
    /// <summary>降序。</summary>
    Descending
}

/// <summary>集合存储模式。</summary>
public enum CollectionMode : byte
{
    /// <summary>向量集合。</summary>
    Vector = 0,
    /// <summary>不维护 HNSW 的纯标量集合。</summary>
    ScalarOnly = 1
}

/// <summary>投影后的记录视图。</summary>
public sealed class RecordView
{
    /// <summary>记录 ID。</summary>
    public ulong Id { get; init; }
    /// <summary>按需返回的向量。</summary>
    public float[]? Vector { get; init; }
    /// <summary>按需返回的元数据。</summary>
    public IReadOnlyDictionary<string, object>? Metadata { get; init; }
    /// <summary>按需返回的文本。</summary>
    public string? Text { get; init; }
}

/// <summary>纯标量写入模型。</summary>
public sealed class ScalarRecord
{
    /// <summary>记录 ID，插入时由数据库分配。</summary>
    public ulong Id { get; internal set; }
    /// <summary>用于过滤和排序的元数据。</summary>
    public Dictionary<string, object>? Metadata { get; init; }
    /// <summary>可选文本。</summary>
    public string? Text { get; init; }
}

/// <summary>稳定分页游标。</summary>
public sealed class ScalarQueryCursor
{
    /// <summary>排序字段。</summary>
    public required IReadOnlyList<string> Fields { get; init; }
    /// <summary>排序方向。</summary>
    public required IReadOnlyList<SortDirection> Directions { get; init; }
    /// <summary>上一页末项的排序键。</summary>
    public required IReadOnlyList<object?> Values { get; init; }
    /// <summary>上一页末项的记录 ID。</summary>
    public ulong RecordId { get; init; }
}

/// <summary>纯标量查询分页结果。</summary>
public sealed class ScalarQueryPage
{
    /// <summary>当前页记录。</summary>
    public required IReadOnlyList<RecordView> Records { get; init; }
    /// <summary>存在后续记录时返回的游标。</summary>
    public ScalarQueryCursor? NextCursor { get; init; }
}

/// <summary>标量索引类型。</summary>
public enum ScalarIndexType : byte
{
    /// <summary>单字段有序索引。</summary>
    Ordered = 1,
    /// <summary>复合有序索引。</summary>
    CompositeOrdered = 2
}

/// <summary>显式标量索引定义。</summary>
public sealed record ScalarIndexDefinition
{
    /// <summary>索引名称。</summary>
    public required string Name { get; init; }
    /// <summary>索引类型。</summary>
    public ScalarIndexType Type { get; init; } = ScalarIndexType.Ordered;
    /// <summary>索引字段，顺序对复合索引有意义。</summary>
    public required IReadOnlyList<string> Fields { get; init; }
}
