using Microsoft.SemanticKernel.Memory;

namespace Mugu.AI.VectorLite.SemanticKernel;

/// <summary>
/// VectorRecord 与 SK MemoryRecord 之间的映射工具。
/// SK 使用 string key 标识记录，VectorLite 使用 ulong Id。
/// 通过在元数据中保存 _sk_key 字段建立映射。
/// </summary>
internal static class MemoryRecordMapper
{
    internal const string SkKeyField = "_sk_key";
    private const int DefaultDimensions = 1536;

    /// <summary>将 SK MemoryRecord 转换为 VectorLite VectorRecord</summary>
    internal static VectorRecord ToVectorRecord(MemoryRecord record)
    {
        var metadata = new Dictionary<string, object>
        {
            [SkKeyField] = record.Metadata.Id
        };

        // 保留 SK 的其他元数据
        if (!string.IsNullOrEmpty(record.Metadata.AdditionalMetadata))
            metadata["_sk_additional"] = record.Metadata.AdditionalMetadata;

        return new VectorRecord
        {
            Vector = record.Embedding.ToArray(),
            Metadata = metadata,
            Text = record.Metadata.Text
        };
    }

    /// <summary>将 VectorLite VectorRecord 转换为 SK MemoryRecord</summary>
    internal static MemoryRecord ToMemoryRecord(VectorRecord record, bool withEmbedding)
    {
        var skKey = record.Metadata?.GetValueOrDefault(SkKeyField)?.ToString() ?? record.Id.ToString();
        var additionalMeta = record.Metadata?.GetValueOrDefault("_sk_additional")?.ToString() ?? string.Empty;

        return MemoryRecord.LocalRecord(
            id: skKey,
            text: record.Text ?? string.Empty,
            description: string.Empty,
            embedding: withEmbedding ? record.Vector : ReadOnlyMemory<float>.Empty,
            additionalMetadata: additionalMeta);
    }

    /// <summary>获取向量维度（从记录推断，默认1536）</summary>
    internal static int GetDimensions(MemoryRecord record)
    {
        return record.Embedding.Length > 0 ? record.Embedding.Length : DefaultDimensions;
    }
}
