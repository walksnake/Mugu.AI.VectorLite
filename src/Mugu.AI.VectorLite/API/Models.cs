namespace Mugu.AI.VectorLite;

/// <summary>向量记录，包含向量数据、元数据和可选文本</summary>
public sealed class VectorRecord
{
    /// <summary>记录ID（插入后由数据库分配，不可修改）</summary>
    public ulong Id { get; internal set; }

    /// <summary>向量数据</summary>
    public required float[] Vector { get; init; }

    /// <summary>元数据（键值对），值支持 string / long / double / bool</summary>
    public Dictionary<string, object>? Metadata { get; init; }

    /// <summary>可选的文本内容（原文存储）</summary>
    public string? Text { get; init; }
}

/// <summary>搜索结果项</summary>
public sealed class SearchResult
{
    /// <summary>匹配的向量记录</summary>
    public required VectorRecord Record { get; init; }

    /// <summary>与查询向量的距离（越小越相似）</summary>
    public float Distance { get; init; }

    /// <summary>相似度得分（1 - Distance，仅余弦距离有意义）</summary>
    public float Score => 1f - Distance;
}
