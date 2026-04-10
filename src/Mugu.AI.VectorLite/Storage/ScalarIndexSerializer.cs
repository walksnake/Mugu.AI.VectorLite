using System.Text;
using Mugu.AI.VectorLite.Engine;

namespace Mugu.AI.VectorLite.Storage;

/// <summary>
/// 标量索引的二进制序列化/反序列化。
/// 仅序列化 recordMetadata，加载时重建倒排索引。
/// </summary>
internal static class ScalarIndexSerializer
{
    private const byte TypeString = 0;
    private const byte TypeLong = 1;
    private const byte TypeDouble = 2;
    private const byte TypeBool = 3;

    /// <summary>序列化标量索引为字节数组</summary>
    internal static byte[] Serialize(ScalarIndex index)
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);

        var metadata = index.RecordMetadata;
        bw.Write((uint)metadata.Count);

        foreach (var (recordId, fields) in metadata)
        {
            bw.Write(recordId);
            bw.Write((uint)fields.Count);

            foreach (var (fieldName, value) in fields)
            {
                var nameBytes = Encoding.UTF8.GetBytes(fieldName);
                bw.Write((uint)nameBytes.Length);
                bw.Write(nameBytes);
                WriteValue(bw, value);
            }
        }

        bw.Flush();
        return ms.ToArray();
    }

    /// <summary>从字节数组反序列化，返回重建好的 ScalarIndex</summary>
    internal static ScalarIndex Deserialize(ReadOnlySpan<byte> data)
    {
        using var ms = new MemoryStream(data.ToArray());
        using var br = new BinaryReader(ms, Encoding.UTF8, leaveOpen: true);

        var recordCount = br.ReadUInt32();
        var recordMetadata = new Dictionary<ulong, Dictionary<string, object>>((int)recordCount);

        for (var i = 0u; i < recordCount; i++)
        {
            var recordId = br.ReadUInt64();
            var fieldCount = br.ReadUInt32();
            var fields = new Dictionary<string, object>((int)fieldCount);

            for (var j = 0u; j < fieldCount; j++)
            {
                var nameLen = br.ReadUInt32();
                var nameBytes = br.ReadBytes((int)nameLen);
                var fieldName = Encoding.UTF8.GetString(nameBytes);
                var value = ReadValue(br);
                fields[fieldName] = value;
            }

            recordMetadata[recordId] = fields;
        }

        var index = new ScalarIndex();
        index.BulkLoad(recordMetadata);
        return index;
    }

    private static void WriteValue(BinaryWriter writer, object value)
    {
        switch (value)
        {
            case string s:
                writer.Write(TypeString);
                var strBytes = Encoding.UTF8.GetBytes(s);
                writer.Write((uint)strBytes.Length);
                writer.Write(strBytes);
                break;
            case long l:
                writer.Write(TypeLong);
                writer.Write(l);
                break;
            case int intVal:
                writer.Write(TypeLong);
                writer.Write((long)intVal);
                break;
            case double d:
                writer.Write(TypeDouble);
                writer.Write(d);
                break;
            case float f:
                writer.Write(TypeDouble);
                writer.Write((double)f);
                break;
            case bool b:
                writer.Write(TypeBool);
                writer.Write(b);
                break;
            default:
                // 降级为字符串
                writer.Write(TypeString);
                var fallback = Encoding.UTF8.GetBytes(value.ToString() ?? "");
                writer.Write((uint)fallback.Length);
                writer.Write(fallback);
                break;
        }
    }

    private static object ReadValue(BinaryReader reader)
    {
        var type = reader.ReadByte();
        return type switch
        {
            TypeString => Encoding.UTF8.GetString(reader.ReadBytes((int)reader.ReadUInt32())),
            TypeLong => reader.ReadInt64(),
            TypeDouble => reader.ReadDouble(),
            TypeBool => reader.ReadBoolean(),
            _ => throw new CorruptedFileException($"未知的元数据值类型: {type}")
        };
    }
}
