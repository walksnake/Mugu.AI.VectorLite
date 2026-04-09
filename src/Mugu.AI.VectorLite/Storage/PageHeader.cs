using System.IO.Hashing;
using System.Runtime.InteropServices;

namespace Mugu.AI.VectorLite.Storage;

/// <summary>
/// 页面头部，每页前25字节为公共头部。
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct PageHeader
{
    public const int SizeInBytes = 25;

    /// <summary>页面唯一ID（从1开始，0为文件头页）</summary>
    public ulong PageId;

    /// <summary>页面类型</summary>
    public PageType PageType;

    /// <summary>本页已使用的有效字节数（不含页头）</summary>
    public uint UsedBytes;

    /// <summary>下一页ID（溢出链/空闲链，0表示无）</summary>
    public ulong NextPageId;

    /// <summary>CRC32 校验（覆盖本页全部数据）</summary>
    public uint Checksum;

    /// <summary>将结构体作为字节 Span 访问</summary>
    public readonly ReadOnlySpan<byte> AsReadOnlySpan()
    {
        return MemoryMarshal.AsBytes(
            MemoryMarshal.CreateReadOnlySpan(in this, 1));
    }

    /// <summary>从字节 Span 读取页头</summary>
    public static PageHeader FromSpan(ReadOnlySpan<byte> source)
    {
        return MemoryMarshal.Read<PageHeader>(source);
    }

    /// <summary>计算整页（页头 + 数据）的 CRC32 校验和</summary>
    public static uint CalculatePageChecksum(ReadOnlySpan<byte> fullPageData)
    {
        // 校验和覆盖整页数据，但排除 Checksum 字段本身（最后4字节 of 页头不参与计算）
        // 即覆盖 [0..20] + [25..end]，跳过 Checksum 字段 [21..24]
        var crc = new Crc32();
        crc.Append(fullPageData[..21]);
        if (fullPageData.Length > SizeInBytes)
        {
            crc.Append(fullPageData[SizeInBytes..]);
        }
        return crc.GetCurrentHashAsUInt32();
    }
}
