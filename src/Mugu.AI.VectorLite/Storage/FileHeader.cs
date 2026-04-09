using System.IO.Hashing;
using System.Runtime.InteropServices;

namespace Mugu.AI.VectorLite.Storage;

/// <summary>
/// 数据库文件头，占据页0前76字节。
/// 使用 StructLayout 保证内存布局与磁盘格式一致。
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct FileHeader
{
    public const uint MagicNumber = 0x54494C56; // "VLIT" LE
    public const uint CurrentVersion = 1;
    public const int SizeInBytes = 76;

    public uint Magic;
    public uint Version;
    public uint PageSize;
    public ulong TotalPages;
    public ulong FreePageListHead;
    public ulong CollectionRootPage;
    public uint MaxDimensions;
    public ulong CreatedAt;
    public ulong LastCheckpoint;
    public unsafe fixed byte Reserved[16];
    public uint HeaderChecksum;

    /// <summary>验证魔数与版本号</summary>
    public readonly bool IsValid()
        => Magic == MagicNumber && Version <= CurrentVersion;

    /// <summary>计算并写入 CRC32 校验和（覆盖前72字节）</summary>
    public void UpdateChecksum()
    {
        var span = AsReadOnlySpan();
        // 校验和覆盖 0x00..0x47（前72字节），不含最后4字节校验和字段本身
        HeaderChecksum = CalculateCrc32(span[..^4]);
    }

    /// <summary>校验 CRC32 是否正确</summary>
    public readonly bool VerifyChecksum()
    {
        var span = AsReadOnlySpan();
        var expected = CalculateCrc32(span[..^4]);
        return HeaderChecksum == expected;
    }

    /// <summary>创建新的数据库文件头</summary>
    public static FileHeader CreateNew(uint pageSize, uint maxDimensions)
    {
        var header = new FileHeader
        {
            Magic = MagicNumber,
            Version = CurrentVersion,
            PageSize = pageSize,
            TotalPages = 1, // 只有文件头页
            FreePageListHead = 0,
            CollectionRootPage = 0,
            MaxDimensions = maxDimensions,
            CreatedAt = (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            LastCheckpoint = 0
        };
        header.UpdateChecksum();
        return header;
    }

    /// <summary>将结构体作为字节 Span 访问</summary>
    private readonly ReadOnlySpan<byte> AsReadOnlySpan()
    {
        return MemoryMarshal.AsBytes(
            MemoryMarshal.CreateReadOnlySpan(in this, 1));
    }

    private static uint CalculateCrc32(ReadOnlySpan<byte> data)
    {
        return Crc32.HashToUInt32(data);
    }
}
