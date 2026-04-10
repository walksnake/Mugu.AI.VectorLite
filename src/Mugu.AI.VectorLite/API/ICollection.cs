using Mugu.AI.VectorLite.Engine;

namespace Mugu.AI.VectorLite;

/// <summary>集合接口：对特定集合的向量数据进行增删改查</summary>
public interface ICollection
{
    /// <summary>集合名称</summary>
    string Name { get; }

    /// <summary>向量维度</summary>
    int Dimensions { get; }

    /// <summary>集合中的记录数</summary>
    int Count { get; }

    /// <summary>插入一条向量记录</summary>
    Task<ulong> InsertAsync(VectorRecord record, CancellationToken ct = default);

    /// <summary>批量插入向量记录</summary>
    Task<IReadOnlyList<ulong>> InsertBatchAsync(IEnumerable<VectorRecord> records, CancellationToken ct = default);

    /// <summary>根据ID获取记录</summary>
    Task<VectorRecord?> GetAsync(ulong id, CancellationToken ct = default);

    /// <summary>根据ID删除记录</summary>
    Task<bool> DeleteAsync(ulong id, CancellationToken ct = default);

    /// <summary>根据元数据字段查找记录ID</summary>
    Task<IReadOnlyList<ulong>> FindIdsByMetadataAsync(string field, object value, CancellationToken ct = default);

    /// <summary>
    /// 插入或更新记录：根据指定元数据字段查找已有记录，若存在则替换，否则插入。
    /// keyField 对应 record.Metadata 中的一个键。
    /// </summary>
    Task<ulong> UpsertAsync(VectorRecord record, string keyField, CancellationToken ct = default);

    /// <summary>创建查询构建器</summary>
    IQueryBuilder Query(float[] queryVector);
}
