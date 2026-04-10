using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace Mugu.AI.VectorLite.Storage;

/// <summary>
/// 页面管理器：负责页的分配、回收、读写。
/// 所有页操作均通过 mmap 视图完成。
/// </summary>
internal sealed class PageManager : IDisposable
{
    private readonly string _filePath;
    private readonly uint _pageSize;
    private readonly ILogger? _logger;
    private MemoryMappedFile? _mmf;
    private MemoryMappedViewAccessor? _accessor;
    private FileHeader _header;
    private bool _disposed;

    /// <summary>页数据可用空间（PageSize - PageHeader）</summary>
    internal int UsablePageSize => (int)_pageSize - PageHeader.SizeInBytes;

    /// <summary>当前文件头</summary>
    internal ref FileHeader Header => ref _header;

    /// <summary>页大小</summary>
    internal uint PageSize => _pageSize;

    private PageManager(string filePath, uint pageSize, ILogger? logger)
    {
        _filePath = filePath;
        _pageSize = pageSize;
        _logger = logger;
    }

    /// <summary>创建新的数据库文件并初始化</summary>
    internal static PageManager CreateNew(string filePath, uint pageSize, uint maxDimensions, ILogger? logger = null)
    {
        if (File.Exists(filePath))
            throw new StorageException($"文件已存在: {filePath}");

        var pm = new PageManager(filePath, pageSize, logger);
        pm._header = FileHeader.CreateNew(pageSize, maxDimensions);

        // 创建初始文件，128页（1MB @ 8KB页大小）
        var initialPages = Math.Max(128u, (uint)(1024 * 1024 / pageSize));
        var initialSize = (long)initialPages * pageSize;

        using (var fs = new FileStream(filePath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            fs.SetLength(initialSize);
            // 写入文件头
            var headerSpan = MemoryMarshal.AsBytes(
                MemoryMarshal.CreateReadOnlySpan(ref pm._header, 1));
            fs.Write(headerSpan);
            fs.Flush(true);
        }

        pm._header.TotalPages = initialPages;
        // 初始化空闲页链表（页1..initialPages-1均为空闲页）
        pm.MapFile();
        pm.InitializeFreeList(1, initialPages);
        pm.FlushHeader();

        logger?.LogInformation("创建新数据库: {Path}, 页大小={PageSize}, 初始页数={Pages}",
            filePath, pageSize, initialPages);
        return pm;
    }

    /// <summary>打开已有的数据库文件</summary>
    internal static PageManager Open(string filePath, ILogger? logger = null)
    {
        if (!File.Exists(filePath))
            throw new StorageException($"文件不存在: {filePath}");

        // 读取文件头
        FileHeader header;
        using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            Span<byte> buf = stackalloc byte[FileHeader.SizeInBytes];
            if (fs.Read(buf) < FileHeader.SizeInBytes)
                throw new CorruptedFileException("文件头读取不完整");
            header = MemoryMarshal.Read<FileHeader>(buf);
        }

        if (!header.IsValid())
            throw new CorruptedFileException($"无效的文件头：Magic=0x{header.Magic:X8}, Version={header.Version}");

        if (!header.VerifyChecksum())
            throw new CorruptedFileException("文件头 CRC32 校验失败");

        var pm = new PageManager(filePath, header.PageSize, logger)
        {
            _header = header
        };
        pm.MapFile();

        logger?.LogInformation("打开数据库: {Path}, 页大小={PageSize}, 总页数={Pages}",
            filePath, header.PageSize, header.TotalPages);
        return pm;
    }

    /// <summary>读取指定页的头部信息</summary>
    internal PageHeader ReadPageHeader(ulong pageId)
    {
        EnsureNotDisposed();
        ValidatePageId(pageId);

        var offset = GetPageOffset(pageId);
        var headerBytes = new byte[PageHeader.SizeInBytes];
        _accessor!.ReadArray(offset, headerBytes, 0, PageHeader.SizeInBytes);
        return MemoryMarshal.Read<PageHeader>(headerBytes);
    }

    /// <summary>读取指定页的数据部分</summary>
    internal int ReadPageData(ulong pageId, Span<byte> destination)
    {
        EnsureNotDisposed();
        ValidatePageId(pageId);

        var offset = GetPageOffset(pageId) + PageHeader.SizeInBytes;
        var length = Math.Min(destination.Length, UsablePageSize);

        var buffer = new byte[length];
        _accessor!.ReadArray(offset, buffer, 0, length);
        buffer.CopyTo(destination);
        return length;
    }

    /// <summary>读取完整页（包含页头和数据）</summary>
    internal byte[] ReadFullPage(ulong pageId)
    {
        EnsureNotDisposed();
        ValidatePageId(pageId);

        var offset = GetPageOffset(pageId);
        var buffer = new byte[_pageSize];
        _accessor!.ReadArray(offset, buffer, 0, (int)_pageSize);
        return buffer;
    }

    /// <summary>将页头写入指定页</summary>
    internal void WritePageHeader(ulong pageId, in PageHeader header)
    {
        EnsureNotDisposed();
        ValidatePageId(pageId);

        var offset = GetPageOffset(pageId);
        var bytes = MemoryMarshal.AsBytes(
            MemoryMarshal.CreateReadOnlySpan(in header, 1));
        _accessor!.WriteArray(offset, bytes.ToArray(), 0, PageHeader.SizeInBytes);
    }

    /// <summary>将数据写入指定页的数据区域</summary>
    internal void WritePageData(ulong pageId, ReadOnlySpan<byte> source)
    {
        EnsureNotDisposed();
        ValidatePageId(pageId);

        if (source.Length > UsablePageSize)
            throw new PageException($"数据长度 {source.Length} 超过页可用空间 {UsablePageSize}");

        var offset = GetPageOffset(pageId) + PageHeader.SizeInBytes;
        _accessor!.WriteArray(offset, source.ToArray(), 0, source.Length);
    }

    /// <summary>写入完整页数据（页头+数据）</summary>
    internal void WriteFullPage(ulong pageId, ReadOnlySpan<byte> fullPageData)
    {
        EnsureNotDisposed();
        ValidatePageId(pageId);

        var offset = GetPageOffset(pageId);
        var length = Math.Min(fullPageData.Length, (int)_pageSize);
        _accessor!.WriteArray(offset, fullPageData[..length].ToArray(), 0, length);
    }

    /// <summary>
    /// 分配一个新页。优先从空闲链表取，链表为空时扩展文件。
    /// </summary>
    internal ulong AllocatePage(PageType type)
    {
        EnsureNotDisposed();

        ulong pageId;

        if (_header.FreePageListHead != 0)
        {
            // 从空闲链表取一页
            pageId = _header.FreePageListHead;
            var freeHeader = ReadPageHeader(pageId);
            _header.FreePageListHead = freeHeader.NextPageId;
        }
        else
        {
            // 空闲链表为空，扩展文件
            GrowFile();
            // 扩展后空闲链表一定非空
            pageId = _header.FreePageListHead;
            var freeHeader = ReadPageHeader(pageId);
            _header.FreePageListHead = freeHeader.NextPageId;
        }

        // 初始化页头
        var newHeader = new PageHeader
        {
            PageId = pageId,
            PageType = type,
            UsedBytes = 0,
            NextPageId = 0,
            Checksum = 0
        };
        WritePageHeader(pageId, newHeader);

        // 清零数据区域
        var zeros = new byte[UsablePageSize];
        WritePageData(pageId, zeros);

        FlushHeader();
        return pageId;
    }

    /// <summary>将页标记为 Free 并加入空闲链表头部</summary>
    internal void FreePage(ulong pageId)
    {
        EnsureNotDisposed();
        ValidatePageId(pageId);

        if (pageId == 0)
            throw new PageException("不能释放文件头页");

        var freeHeader = new PageHeader
        {
            PageId = pageId,
            PageType = PageType.Free,
            UsedBytes = 0,
            NextPageId = _header.FreePageListHead,
            Checksum = 0
        };
        WritePageHeader(pageId, freeHeader);
        _header.FreePageListHead = pageId;
        FlushHeader();
    }

    /// <summary>刷新文件头到磁盘</summary>
    internal void FlushHeader()
    {
        _header.UpdateChecksum();
        var bytes = MemoryMarshal.AsBytes(
            MemoryMarshal.CreateReadOnlySpan(in _header, 1));
        _accessor!.WriteArray(0, bytes.ToArray(), 0, FileHeader.SizeInBytes);
        _accessor.Flush();
    }

    /// <summary>刷新所有内存映射数据到磁盘</summary>
    internal void Flush()
    {
        EnsureNotDisposed();
        _accessor?.Flush();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _accessor?.Dispose();
        _mmf?.Dispose();
    }

    /// <summary>映射文件到内存</summary>
    private void MapFile()
    {
        var fileSize = new FileInfo(_filePath).Length;
        _mmf = MemoryMappedFile.CreateFromFile(
            _filePath,
            FileMode.Open,
            null,
            fileSize,
            MemoryMappedFileAccess.ReadWrite);
        _accessor = _mmf.CreateViewAccessor(0, fileSize, MemoryMappedFileAccess.ReadWrite);
    }

    /// <summary>关闭并重新创建 MemoryMappedFile 和 ViewAccessor</summary>
    private void RemapFile()
    {
        _accessor?.Dispose();
        _mmf?.Dispose();
        MapFile();
    }

    /// <summary>
    /// 扩展文件大小。按当前大小的25%增长，最小增长1MB。
    /// </summary>
    private void GrowFile()
    {
        var currentSize = (long)_header.TotalPages * _pageSize;
        var growthSize = Math.Max(currentSize / 4, 1024 * 1024);
        // 对齐到页大小
        growthSize = (growthSize / _pageSize + 1) * _pageSize;
        var newSize = currentSize + growthSize;
        var newTotalPages = (ulong)(newSize / _pageSize);

        _logger?.LogDebug("扩展文件: {OldPages} -> {NewPages} 页", _header.TotalPages, newTotalPages);

        // 保存旧映射引用以便失败时恢复
        var oldAccessor = _accessor;
        var oldMmf = _mmf;
        _accessor = null;
        _mmf = null;

        try
        {
            // 先关闭旧映射
            oldAccessor?.Dispose();
            oldMmf?.Dispose();

            // 扩展文件
            using (var fs = new FileStream(_filePath, FileMode.Open, FileAccess.Write, FileShare.None))
            {
                fs.SetLength(newSize);
            }

            var oldTotalPages = _header.TotalPages;
            _header.TotalPages = newTotalPages;

            // 重新映射
            MapFile();

            // 初始化新增的空闲页
            InitializeFreeList(oldTotalPages, newTotalPages);
            FlushHeader();
        }
        catch
        {
            // 扩展失败：尝试以当前实际文件大小重新映射，恢复可用状态
            try { MapFile(); }
            catch { /* 映射恢复也失败，上层需处理 */ }
            throw;
        }
    }

    /// <summary>初始化空闲链表：将 [startPage, endPage) 范围内的页串联成链表</summary>
    private void InitializeFreeList(ulong startPage, ulong endPage)
    {
        // 从后向前串联，使分配时按从小到大的顺序分配
        for (var i = endPage - 1; i >= startPage; i--)
        {
            var freeHeader = new PageHeader
            {
                PageId = i,
                PageType = PageType.Free,
                UsedBytes = 0,
                NextPageId = _header.FreePageListHead,
                Checksum = 0
            };
            WritePageHeader(i, freeHeader);
            _header.FreePageListHead = i;

            if (i == 0) break; // 防止 ulong 下溢
        }
    }

    /// <summary>计算页在文件中的字节偏移</summary>
    private long GetPageOffset(ulong pageId)
    {
        return checked((long)pageId * _pageSize);
    }

    private void ValidatePageId(ulong pageId)
    {
        if (pageId >= _header.TotalPages)
            throw new PageException($"页ID {pageId} 超出范围 [0, {_header.TotalPages})");
    }

    private void EnsureNotDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
