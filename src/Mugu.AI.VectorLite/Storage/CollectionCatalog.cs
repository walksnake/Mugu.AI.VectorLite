using System.Text;
using Mugu.AI.VectorLite.Engine.Distance;

namespace Mugu.AI.VectorLite.Storage;

/// <summary>集合目录条目</summary>
internal struct CollectionCatalogEntry
{
    internal string Name;
    internal int Dimensions;
    internal DistanceMetric DistanceMetric;
    internal int HnswM;
    internal int HnswEfConstruction;
    internal ulong NextRecordId;
    internal ulong HNSWRootPage;
    internal ulong ScalarIndexRootPage;
    internal ulong TextStoreRootPage;
}

/// <summary>
/// 集合目录的序列化/反序列化。
/// 目录记录所有集合的元信息及其数据/索引页链根页 ID。
/// </summary>
internal static class CollectionCatalog
{
    /// <summary>序列化集合目录为字节数组</summary>
    internal static byte[] Serialize(IReadOnlyList<CollectionCatalogEntry> entries)
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);

        // 版本号
        bw.Write((byte)1);

        bw.Write((uint)entries.Count);

        foreach (var entry in entries)
        {
            var nameBytes = Encoding.UTF8.GetBytes(entry.Name);
            bw.Write((uint)nameBytes.Length);
            bw.Write(nameBytes);
            bw.Write((uint)entry.Dimensions);
            bw.Write((byte)entry.DistanceMetric);
            bw.Write(entry.HnswM);
            bw.Write(entry.HnswEfConstruction);
            bw.Write(entry.NextRecordId);
            bw.Write(entry.HNSWRootPage);
            bw.Write(entry.ScalarIndexRootPage);
            bw.Write(entry.TextStoreRootPage);
        }

        bw.Flush();
        return ms.ToArray();
    }

    /// <summary>反序列化集合目录</summary>
    internal static List<CollectionCatalogEntry> Deserialize(ReadOnlySpan<byte> data)
    {
        using var ms = new MemoryStream(data.ToArray());
        using var br = new BinaryReader(ms, Encoding.UTF8, leaveOpen: true);

        // 读取并验证版本号
        var version = br.ReadByte();
        if (version != 1)
            throw new CorruptedFileException($"不支持的集合目录序列化版本: {version}（仅支持 v1）");

        var count = br.ReadUInt32();
        var entries = new List<CollectionCatalogEntry>((int)count);

        for (var i = 0u; i < count; i++)
        {
            var nameLen = br.ReadUInt32();
            var nameBytes = br.ReadBytes((int)nameLen);
            var name = Encoding.UTF8.GetString(nameBytes);

            entries.Add(new CollectionCatalogEntry
            {
                Name = name,
                Dimensions = (int)br.ReadUInt32(),
                DistanceMetric = (DistanceMetric)br.ReadByte(),
                HnswM = br.ReadInt32(),
                HnswEfConstruction = br.ReadInt32(),
                NextRecordId = br.ReadUInt64(),
                HNSWRootPage = br.ReadUInt64(),
                ScalarIndexRootPage = br.ReadUInt64(),
                TextStoreRootPage = br.ReadUInt64()
            });
        }

        return entries;
    }
}
