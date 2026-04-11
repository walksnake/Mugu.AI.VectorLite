using Microsoft.Extensions.Logging;

namespace Mugu.AI.VectorLite.Storage;

/// <summary>
/// 存储层门面：封装 PageManager 和 Wal，提供统一的存储操作接口。
/// 所有写操作均通过 WAL 保证原子性。
/// </summary>
internal sealed class FileStorage : IDisposable
{
    private readonly PageManager _pageManager;
    private readonly Wal _wal;
    private readonly ILogger? _logger;
    private bool _disposed;

    internal PageManager PageManager => _pageManager;
    internal ref FileHeader Header => ref _pageManager.Header;
    internal uint PageSize => _pageManager.PageSize;
    internal int UsablePageSize => _pageManager.UsablePageSize;

    private FileStorage(PageManager pageManager, Wal wal, ILogger? logger)
    {
        _pageManager = pageManager;
        _wal = wal;
        _logger = logger;
    }

    /// <summary>创建新的数据库文件</summary>
    internal static FileStorage CreateNew(string filePath, uint pageSize, uint maxDimensions,
        ILoggerFactory? loggerFactory = null)
    {
        var logger = loggerFactory?.CreateLogger<FileStorage>();
        var pm = PageManager.CreateNew(filePath, pageSize, maxDimensions,
            loggerFactory?.CreateLogger<PageManager>());
        var wal = Wal.Open(filePath, loggerFactory?.CreateLogger<Wal>());
        return new FileStorage(pm, wal, logger);
    }

    /// <summary>打开已有的数据库文件，必要时执行崩溃恢复</summary>
    internal static FileStorage Open(string filePath, ILoggerFactory? loggerFactory = null)
    {
        var logger = loggerFactory?.CreateLogger<FileStorage>();
        var pm = PageManager.Open(filePath, loggerFactory?.CreateLogger<PageManager>());
        var wal = Wal.Open(filePath, loggerFactory?.CreateLogger<Wal>());

        // 崩溃恢复：重放 WAL 中已提交但未检查点的事务
        wal.Replay(pm);

        return new FileStorage(pm, wal, logger);
    }

    /// <summary>
    /// 执行一个写入事务。传入的 action 可进行多次页操作，
    /// 所有操作作为一个原子事务提交。
    /// </summary>
    internal void WriteTransaction(Action<WriteContext> action)
    {
        EnsureNotDisposed();
        var txId = _wal.BeginTransaction();
        var ctx = new WriteContext(this, txId);

        try
        {
            action(ctx);
            _wal.Commit(txId);
        }
        catch
        {
            _wal.Rollback(txId);
            throw;
        }
    }

    /// <summary>分配新页（通过 WAL 记录）</summary>
    internal ulong AllocatePage(ulong txId, PageType type)
    {
        var pageId = _pageManager.AllocatePage(type);
        _wal.LogPageAlloc(txId, pageId, type);
        // 将 FileHeader（FreePageListHead 等）的变更也写入 WAL，
        // 确保崩溃恢复时能正确重放空闲链表状态
        var headerBytes = _pageManager.SerializeCurrentHeader();
        _wal.LogPageWrite(txId, 0, headerBytes);
        return pageId;
    }

    /// <summary>释放页（通过 WAL 记录）</summary>
    internal void FreePage(ulong txId, ulong pageId)
    {
        // 先记录WAL，再实际释放
        _wal.LogPageFree(txId, pageId);
        _pageManager.FreePage(pageId);
        // 将 FileHeader 的 FreePageListHead 变更写入 WAL
        var headerBytes = _pageManager.SerializeCurrentHeader();
        _wal.LogPageWrite(txId, 0, headerBytes);
    }

    /// <summary>写入完整页数据（通过 WAL 记录）</summary>
    internal void WritePage(ulong txId, ulong pageId, ReadOnlySpan<byte> fullPageData)
    {
        _wal.LogPageWrite(txId, pageId, fullPageData);
        _pageManager.WriteFullPage(pageId, fullPageData);
    }

    /// <summary>读取页头</summary>
    internal PageHeader ReadPageHeader(ulong pageId)
    {
        EnsureNotDisposed();
        return _pageManager.ReadPageHeader(pageId);
    }

    /// <summary>读取页数据</summary>
    internal int ReadPageData(ulong pageId, Span<byte> destination)
    {
        EnsureNotDisposed();
        return _pageManager.ReadPageData(pageId, destination);
    }

    /// <summary>读取完整页</summary>
    internal byte[] ReadFullPage(ulong pageId)
    {
        EnsureNotDisposed();
        return _pageManager.ReadFullPage(pageId);
    }

    /// <summary>执行检查点</summary>
    internal void Checkpoint()
    {
        EnsureNotDisposed();
        _wal.Checkpoint(_pageManager);
    }

    /// <summary>
    /// 追加逻辑 WAL 记录（RecordInsert / RecordDelete）。
    /// 在内存更新之前调用，确保零数据丢失。
    /// </summary>
    internal void LogLogicalOperation(WalOperationType opType, ReadOnlySpan<byte> data)
    {
        EnsureNotDisposed();
        _wal.AppendLogical(opType, data);
    }

    /// <summary>读取自上次检查点以来的逻辑 WAL 记录（恢复用）</summary>
    internal List<Wal.LogicalWalRecord> ReadLogicalRecords()
    {
        EnsureNotDisposed();
        return _wal.ReadLogicalRecords();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _wal.Dispose();
        _pageManager.Dispose();
    }

    private void EnsureNotDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    /// <summary>写入事务的上下文，提供事务内的页操作</summary>
    internal readonly struct WriteContext
    {
        private readonly FileStorage _storage;
        private readonly ulong _txId;

        internal WriteContext(FileStorage storage, ulong txId)
        {
            _storage = storage;
            _txId = txId;
        }

        /// <summary>分配新页</summary>
        public ulong AllocatePage(PageType type) => _storage.AllocatePage(_txId, type);

        /// <summary>释放页</summary>
        public void FreePage(ulong pageId) => _storage.FreePage(_txId, pageId);

        /// <summary>写入完整页数据</summary>
        public void WritePage(ulong pageId, ReadOnlySpan<byte> fullPageData)
            => _storage.WritePage(_txId, pageId, fullPageData);

        /// <summary>写入页头</summary>
        public void WritePageHeader(ulong pageId, in PageHeader header)
            => _storage.PageManager.WritePageHeader(pageId, header);

        /// <summary>写入页数据区域</summary>
        public void WritePageData(ulong pageId, ReadOnlySpan<byte> data)
            => _storage.PageManager.WritePageData(pageId, data);
    }
}
