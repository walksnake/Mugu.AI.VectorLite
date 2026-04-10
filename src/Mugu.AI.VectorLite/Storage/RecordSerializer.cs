using System.Text;
using System.Text.Json;

namespace Mugu.AI.VectorLite.Storage;

/// <summary>
/// 向量记录的二进制序列化/反序列化。
/// 同时服务于 WAL 逻辑记录和检查点快照。
/// </summary>
internal static class RecordSerializer
{
    /// <summary>将一条记录序列化到 BinaryWriter</summary>
    internal static void Write(BinaryWriter writer, VectorRecord record)
    {
        // RecordId
        writer.Write(record.Id);
        // Dimensions
        writer.Write((uint)record.Vector.Length);
        // Vector
        foreach (var v in record.Vector)
            writer.Write(v);
        // Metadata
        if (record.Metadata is { Count: > 0 })
        {
            var json = JsonSerializer.SerializeToUtf8Bytes(record.Metadata);
            writer.Write((uint)json.Length);
            writer.Write(json);
        }
        else
        {
            writer.Write(0u);
        }
        // Text
        if (record.Text != null)
        {
            var textBytes = Encoding.UTF8.GetBytes(record.Text);
            writer.Write((uint)textBytes.Length);
            writer.Write(textBytes);
        }
        else
        {
            writer.Write(0u);
        }
    }

    // 反序列化安全上限
    private const uint MaxDimensions = 100_000;
    private const uint MaxFieldSize = 100 * 1024 * 1024; // 100MB
    private const uint MaxNameLength = 4096;

    /// <summary>从 BinaryReader 反序列化一条记录</summary>
    internal static VectorRecord Read(BinaryReader reader)
    {
        var id = reader.ReadUInt64();
        var dimensions = reader.ReadUInt32();
        if (dimensions == 0 || dimensions > MaxDimensions)
            throw new StorageException($"记录 {id} 维度异常: {dimensions}");
        var vector = new float[dimensions];
        for (var i = 0; i < dimensions; i++)
            vector[i] = reader.ReadSingle();

        // Metadata
        Dictionary<string, object>? metadata = null;
        var metadataLen = reader.ReadUInt32();
        if (metadataLen > MaxFieldSize)
            throw new StorageException($"记录 {id} 元数据长度异常: {metadataLen}");
        if (metadataLen > 0)
        {
            var metadataBytes = reader.ReadBytes((int)metadataLen);
            metadata = DeserializeMetadata(metadataBytes);
        }

        // Text
        string? text = null;
        var textLen = reader.ReadUInt32();
        if (textLen > MaxFieldSize)
            throw new StorageException($"记录 {id} 文本长度异常: {textLen}");
        if (textLen > 0)
        {
            var textBytes = reader.ReadBytes((int)textLen);
            text = Encoding.UTF8.GetString(textBytes);
        }

        return new VectorRecord
        {
            Id = id,
            Vector = vector,
            Metadata = metadata,
            Text = text
        };
    }

    /// <summary>
    /// 序列化 WAL RecordInsert 数据：CollectionName + Record
    /// </summary>
    internal static byte[] SerializeInsert(string collectionName, VectorRecord record)
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);

        // 集合名
        var nameBytes = Encoding.UTF8.GetBytes(collectionName);
        bw.Write((uint)nameBytes.Length);
        bw.Write(nameBytes);

        // 记录
        Write(bw, record);
        bw.Flush();

        return ms.ToArray();
    }

    /// <summary>
    /// 序列化 WAL RecordDelete 数据：CollectionName + RecordId
    /// </summary>
    internal static byte[] SerializeDelete(string collectionName, ulong recordId)
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);

        var nameBytes = Encoding.UTF8.GetBytes(collectionName);
        bw.Write((uint)nameBytes.Length);
        bw.Write(nameBytes);
        bw.Write(recordId);
        bw.Flush();

        return ms.ToArray();
    }

    /// <summary>反序列化 WAL RecordInsert 数据</summary>
    internal static (string CollectionName, VectorRecord Record) DeserializeInsert(ReadOnlySpan<byte> data)
    {
        using var ms = new MemoryStream(data.ToArray());
        using var br = new BinaryReader(ms, Encoding.UTF8, leaveOpen: true);

        var nameLen = br.ReadUInt32();
        if (nameLen == 0 || nameLen > MaxNameLength)
            throw new StorageException($"集合名长度异常: {nameLen}");
        var nameBytes = br.ReadBytes((int)nameLen);
        var collectionName = Encoding.UTF8.GetString(nameBytes);

        var record = Read(br);
        return (collectionName, record);
    }

    /// <summary>反序列化 WAL RecordDelete 数据</summary>
    internal static (string CollectionName, ulong RecordId) DeserializeDelete(ReadOnlySpan<byte> data)
    {
        using var ms = new MemoryStream(data.ToArray());
        using var br = new BinaryReader(ms, Encoding.UTF8, leaveOpen: true);

        var nameLen = br.ReadUInt32();
        if (nameLen == 0 || nameLen > MaxNameLength)
            throw new StorageException($"集合名长度异常: {nameLen}");
        var nameBytes = br.ReadBytes((int)nameLen);
        var collectionName = Encoding.UTF8.GetString(nameBytes);

        var recordId = br.ReadUInt64();
        return (collectionName, recordId);
    }

    /// <summary>反序列化元数据 JSON，按类型还原值</summary>
    private static Dictionary<string, object> DeserializeMetadata(byte[] jsonBytes)
    {
        var result = new Dictionary<string, object>();
        using var doc = JsonDocument.Parse(jsonBytes);

        foreach (var prop in doc.RootElement.EnumerateObject())
        {
            result[prop.Name] = prop.Value.ValueKind switch
            {
                JsonValueKind.String => prop.Value.GetString()!,
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.Number when prop.Value.TryGetInt64(out var l) => l,
                JsonValueKind.Number => prop.Value.GetDouble(),
                _ => prop.Value.ToString()
            };
        }

        return result;
    }
}
