using System.Buffers;

namespace Mugu.AI.VectorLite.Storage;

/// <summary>
/// 页链读写工具：将任意长度字节数据写入/读出页面链。
/// 页间通过 PageHeader.NextPageId 串联。
/// </summary>
internal static class PageChainIO
{
    /// <summary>
    /// 将数据写入新分配的页链，返回首页 ID。
    /// 所有页通过 ctx 在 WriteTransaction 内分配和写入。
    /// </summary>
    internal static ulong WriteChain(
        FileStorage.WriteContext ctx,
        FileStorage storage,
        PageType pageType,
        ReadOnlySpan<byte> data)
    {
        var usable = storage.UsablePageSize;
        if (usable <= 0)
            throw new StorageException("页可用空间不足");

        // 计算需要的页数
        var pageCount = data.Length == 0 ? 1 : (data.Length + usable - 1) / usable;

        // 前向分配所有页
        var pageIds = new ulong[pageCount];
        for (var i = 0; i < pageCount; i++)
        {
            pageIds[i] = ctx.AllocatePage(pageType);
        }

        // 逐页写入，设置 NextPageId 串联
        for (var i = 0; i < pageCount; i++)
        {
            var offset = i * usable;
            var chunkLen = Math.Min(usable, data.Length - offset);
            var chunk = chunkLen > 0 ? data.Slice(offset, chunkLen) : ReadOnlySpan<byte>.Empty;
            var nextPageId = i < pageCount - 1 ? pageIds[i + 1] : 0UL;

            // 构造完整页：PageHeader + 数据 + 零填充
            var fullPage = new byte[storage.PageSize];
            var header = new PageHeader
            {
                PageId = pageIds[i],
                PageType = pageType,
                UsedBytes = (uint)chunkLen,
                NextPageId = nextPageId,
                Checksum = 0
            };
            // 先写入页头（不含校验和）
            header.AsReadOnlySpan().CopyTo(fullPage);
            // 写入数据
            if (chunkLen > 0)
                chunk.CopyTo(fullPage.AsSpan(PageHeader.SizeInBytes));
            // 计算并写入校验和
            header.Checksum = PageHeader.CalculatePageChecksum(fullPage);
            header.AsReadOnlySpan().CopyTo(fullPage);

            ctx.WritePage(pageIds[i], fullPage);
        }

        return pageIds[0];
    }

    /// <summary>
    /// 从页链读取全部数据到连续字节数组。
    /// 用于 HNSW / ScalarIndex 的一次性加载。
    /// </summary>
    internal static byte[] ReadChain(FileStorage storage, ulong firstPageId)
    {
        if (firstPageId == 0)
            return [];

        using var ms = new MemoryStream();
        var currentPageId = firstPageId;
        // 循环检测：损坏文件中 NextPageId 可能指向已访问页，若不加防护将无限循环
        var visited = new HashSet<ulong>();

        while (currentPageId != 0)
        {
            if (!visited.Add(currentPageId))
                throw new CorruptedFileException(
                    $"页链中检测到循环引用，页ID={currentPageId}，可能文件已损坏");

            var header = storage.ReadPageHeader(currentPageId);
            if (header.UsedBytes > 0)
            {
                var buffer = new byte[header.UsedBytes];
                storage.ReadPageData(currentPageId, buffer);
                ms.Write(buffer, 0, (int)header.UsedBytes);
            }
            currentPageId = header.NextPageId;
        }

        return ms.ToArray();
    }

    /// <summary>
    /// 遍历页链，返回所有页ID的有序列表。
    /// 用于 TextStore 建立随机访问映射。
    /// </summary>
    internal static List<ulong> WalkChain(FileStorage storage, ulong firstPageId)
    {
        var pageIds = new List<ulong>();
        var currentPageId = firstPageId;
        // 循环检测：损坏文件中 NextPageId 可能形成环，若不加防护将 OOM 或无限挂起
        var visited = new HashSet<ulong>();

        while (currentPageId != 0)
        {
            if (!visited.Add(currentPageId))
                throw new CorruptedFileException(
                    $"页链中检测到循环引用，页ID={currentPageId}，可能文件已损坏");

            pageIds.Add(currentPageId);
            var header = storage.ReadPageHeader(currentPageId);
            currentPageId = header.NextPageId;
        }

        return pageIds;
    }

    /// <summary>
    /// 从页链的指定字节偏移处读取指定长度的数据（随机访问）。
    /// chainPageIds 为 WalkChain 返回的页ID序列。
    /// </summary>
    internal static int ReadAt(
        FileStorage storage,
        List<ulong> chainPageIds,
        long byteOffset,
        Span<byte> destination)
    {
        var usable = storage.UsablePageSize;
        var pageIndex = (int)(byteOffset / usable);
        var offsetInPage = (int)(byteOffset % usable);
        var bytesRead = 0;

        var pageData = ArrayPool<byte>.Shared.Rent(usable);
        try
        {
            while (bytesRead < destination.Length && pageIndex < chainPageIds.Count)
            {
                var pageId = chainPageIds[pageIndex];
                var availableInPage = usable - offsetInPage;
                var toRead = Math.Min(destination.Length - bytesRead, availableInPage);

                storage.ReadPageData(pageId, pageData.AsSpan(0, usable));
                pageData.AsSpan(offsetInPage, toRead).CopyTo(destination.Slice(bytesRead, toRead));

                bytesRead += toRead;
                pageIndex++;
                offsetInPage = 0;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(pageData);
        }

        return bytesRead;
    }

    /// <summary>
    /// 释放整个页链的所有页。
    /// 幂等保护：若遇到已为 Free 状态的页则停止，
    /// 防止崩溃恢复时双重释放导致 free list 形成循环环。
    /// </summary>
    internal static void FreeChain(
        FileStorage.WriteContext ctx,
        FileStorage storage,
        ulong firstPageId)
    {
        var currentPageId = firstPageId;
        // 循环检测：防止损坏页链的 NextPageId 形成环导致无限循环
        var visited = new HashSet<ulong>();

        while (currentPageId != 0)
        {
            if (!visited.Add(currentPageId))
                throw new CorruptedFileException(
                    $"FreeChain 检测到循环引用，页ID={currentPageId}，可能文件已损坏");

            var header = storage.ReadPageHeader(currentPageId);

            // 幂等保护：该页已在 free list 中，说明此链在之前的崩溃恢复中已被释放，
            // 后续页同样已释放，直接停止，避免 free list 形成环
            if (header.PageType == PageType.Free)
                break;

            var nextPageId = header.NextPageId;
            ctx.FreePage(currentPageId);
            currentPageId = nextPageId;
        }
    }
}
