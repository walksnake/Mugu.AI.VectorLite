using System.Buffers.Binary;
using System.IO.Hashing;
using Microsoft.Extensions.Logging;

namespace Mugu.AI.VectorLite.Storage;

/// <summary>
/// 预写日志：保证写操作的原子性和崩溃恢复能力。
/// WAL文件路径 = 数据库文件路径 + "-wal" 后缀。
/// </summary>
internal sealed class Wal : IDisposable
{
    // WAL 记录布局:
    // [0..3]   RecordLength (uint, 含本字段)
    // [4..11]  TransactionId (ulong)
    // [12..19] SequenceNumber (ulong)
    // [20]     OperationType (byte)
    // [21..28] TargetPageId (ulong)
    // [29..32] DataLength (uint)
    // [33..33+DataLength-1] Data
    // [最后4字节] Checksum (CRC32)
    private const int MinRecordSize = 37; // 无数据时的最小记录大小

    private readonly FileStream _walStream;
    private readonly ILogger? _logger;
    private readonly object _lock = new();
    private ulong _nextSequenceNumber;
    private ulong _nextTransactionId;
    private bool _disposed;

    internal string FilePath { get; }

    private Wal(string walFilePath, ILogger? logger)
    {
        FilePath = walFilePath;
        _logger = logger;
        _walStream = new FileStream(walFilePath, FileMode.OpenOrCreate,
            FileAccess.ReadWrite, FileShare.Read, 4096, FileOptions.WriteThrough);
    }

    /// <summary>创建或打开 WAL 文件</summary>
    internal static Wal Open(string dbFilePath, ILogger? logger = null)
    {
        var walPath = dbFilePath + "-wal";
        var wal = new Wal(walPath, logger);
        wal.ScanForState();
        return wal;
    }

    /// <summary>开始新事务，返回事务ID</summary>
    internal ulong BeginTransaction()
    {
        lock (_lock)
        {
            var txId = ++_nextTransactionId;
            WriteRecord(txId, WalOperationType.TxBegin, 0, ReadOnlySpan<byte>.Empty);
            return txId;
        }
    }

    /// <summary>追加页写入日志</summary>
    internal void LogPageWrite(ulong txId, ulong pageId, ReadOnlySpan<byte> pageData)
    {
        lock (_lock)
        {
            WriteRecord(txId, WalOperationType.PageWrite, pageId, pageData);
        }
    }

    /// <summary>追加页分配日志</summary>
    internal void LogPageAlloc(ulong txId, ulong pageId, PageType type)
    {
        lock (_lock)
        {
            WriteRecord(txId, WalOperationType.PageAlloc, pageId, [(byte)type]);
        }
    }

    /// <summary>追加页释放日志</summary>
    internal void LogPageFree(ulong txId, ulong pageId)
    {
        lock (_lock)
        {
            WriteRecord(txId, WalOperationType.PageFree, pageId, ReadOnlySpan<byte>.Empty);
        }
    }

    /// <summary>提交事务（写入TxCommit记录并 Flush）</summary>
    internal void Commit(ulong txId)
    {
        lock (_lock)
        {
            WriteRecord(txId, WalOperationType.TxCommit, 0, ReadOnlySpan<byte>.Empty);
            _walStream.Flush(true);
            _logger?.LogDebug("事务 {TxId} 已提交", txId);
        }
    }

    /// <summary>回滚事务（写入TxRollback记录）</summary>
    internal void Rollback(ulong txId)
    {
        lock (_lock)
        {
            WriteRecord(txId, WalOperationType.TxRollback, 0, ReadOnlySpan<byte>.Empty);
            _walStream.Flush(true);
            _logger?.LogDebug("事务 {TxId} 已回滚", txId);
        }
    }

    /// <summary>
    /// 检查点操作：将WAL中已提交事务的页写入刷入主文件，截断WAL。
    /// </summary>
    internal void Checkpoint(PageManager pageManager)
    {
        lock (_lock)
        {
            _logger?.LogInformation("开始检查点...");

            // 第一遍扫描：收集已提交事务
            var records = ReadAllRecords();
            var committedTxIds = new HashSet<ulong>();
            var rolledBackTxIds = new HashSet<ulong>();

            foreach (var record in records)
            {
                if (record.OperationType == WalOperationType.TxCommit)
                    committedTxIds.Add(record.TransactionId);
                else if (record.OperationType == WalOperationType.TxRollback)
                    rolledBackTxIds.Add(record.TransactionId);
            }

            // 第二遍：将已提交事务的 PageWrite 刷入主文件
            var pageWrites = 0;
            foreach (var record in records)
            {
                if (record.OperationType == WalOperationType.PageWrite
                    && committedTxIds.Contains(record.TransactionId))
                {
                    pageManager.WriteFullPage(record.TargetPageId, record.Data);
                    pageWrites++;
                }
            }

            pageManager.Flush();

            // 更新文件头检查点时间
            pageManager.Header.LastCheckpoint =
                (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            pageManager.FlushHeader();

            // 截断 WAL
            _walStream.SetLength(0);
            _walStream.Flush(true);

            // 写入检查点标记
            WriteRecord(0, WalOperationType.Checkpoint, 0, ReadOnlySpan<byte>.Empty);

            _logger?.LogInformation("检查点完成: 刷入 {Count} 个页写入", pageWrites);
        }
    }

    /// <summary>
    /// 崩溃恢复：读取WAL，重放所有已提交但未检查点的事务。
    /// 在数据库打开时调用。
    /// </summary>
    internal void Replay(PageManager pageManager)
    {
        lock (_lock)
        {
            if (_walStream.Length == 0)
                return;

            _logger?.LogInformation("开始 WAL 恢复...");

            var records = ReadAllRecords();
            if (records.Count == 0)
                return;

            // 收集已提交和已回滚的事务
            var committedTxIds = new HashSet<ulong>();

            foreach (var record in records)
            {
                if (record.OperationType == WalOperationType.TxCommit)
                    committedTxIds.Add(record.TransactionId);
            }

            // 重放已提交事务的页写入
            var replayed = 0;
            foreach (var record in records)
            {
                if (record.OperationType == WalOperationType.PageWrite
                    && committedTxIds.Contains(record.TransactionId))
                {
                    pageManager.WriteFullPage(record.TargetPageId, record.Data);
                    replayed++;
                }
            }

            if (replayed > 0)
            {
                pageManager.Flush();
                pageManager.FlushHeader();
            }

            // 截断 WAL
            _walStream.SetLength(0);
            _walStream.Flush(true);

            _logger?.LogInformation("WAL 恢复完成: 重放 {Count} 个页写入", replayed);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _walStream.Dispose();
    }

    /// <summary>写入一条 WAL 记录</summary>
    private void WriteRecord(ulong txId, WalOperationType opType, ulong pageId, ReadOnlySpan<byte> data)
    {
        var recordLength = (uint)(MinRecordSize + data.Length);
        var seqNo = ++_nextSequenceNumber;

        // 构造记录（不含 CRC）
        var buffer = new byte[recordLength];
        var span = buffer.AsSpan();
        BinaryPrimitives.WriteUInt32LittleEndian(span[0..4], recordLength);
        BinaryPrimitives.WriteUInt64LittleEndian(span[4..12], txId);
        BinaryPrimitives.WriteUInt64LittleEndian(span[12..20], seqNo);
        span[20] = (byte)opType;
        BinaryPrimitives.WriteUInt64LittleEndian(span[21..29], pageId);
        BinaryPrimitives.WriteUInt32LittleEndian(span[29..33], (uint)data.Length);
        data.CopyTo(span[33..]);

        // CRC32 覆盖除最后4字节外的全部内容
        var crc = Crc32.HashToUInt32(span[..^4]);
        BinaryPrimitives.WriteUInt32LittleEndian(span[^4..], crc);

        _walStream.Seek(0, SeekOrigin.End);
        _walStream.Write(buffer);
    }

    /// <summary>读取所有 WAL 记录</summary>
    private List<WalRecord> ReadAllRecords()
    {
        var records = new List<WalRecord>();
        _walStream.Seek(0, SeekOrigin.Begin);

        var headerBuf = new byte[MinRecordSize];
        while (_walStream.Position < _walStream.Length)
        {
            var startPos = _walStream.Position;

            // 读取记录长度
            if (_walStream.Read(headerBuf, 0, 4) < 4) break;

            var recordLength = BinaryPrimitives.ReadUInt32LittleEndian(headerBuf.AsSpan(0, 4));
            if (recordLength < MinRecordSize)
            {
                _logger?.LogWarning("WAL 记录长度异常: {Length} @ offset {Offset}", recordLength, startPos);
                break;
            }

            // 回退并读取完整记录
            _walStream.Seek(startPos, SeekOrigin.Begin);
            var recordBuf = new byte[recordLength];
            if (_walStream.Read(recordBuf) < (int)recordLength) break;

            // 验证 CRC
            var expectedCrc = BinaryPrimitives.ReadUInt32LittleEndian(recordBuf.AsSpan((int)recordLength - 4, 4));
            var actualCrc = Crc32.HashToUInt32(recordBuf.AsSpan(0, (int)recordLength - 4));

            if (expectedCrc != actualCrc)
            {
                _logger?.LogWarning("WAL 记录 CRC 校验失败 @ offset {Offset}", startPos);
                break; // 截断处理：后续记录视为无效
            }

            var span = recordBuf.AsSpan();
            var record = new WalRecord
            {
                TransactionId = BinaryPrimitives.ReadUInt64LittleEndian(span[4..12]),
                SequenceNumber = BinaryPrimitives.ReadUInt64LittleEndian(span[12..20]),
                OperationType = (WalOperationType)span[20],
                TargetPageId = BinaryPrimitives.ReadUInt64LittleEndian(span[21..29]),
            };

            var dataLength = BinaryPrimitives.ReadUInt32LittleEndian(span[29..33]);
            if (dataLength > 0)
            {
                record.Data = span.Slice(33, (int)dataLength).ToArray();
            }

            records.Add(record);
        }

        return records;
    }

    /// <summary>扫描 WAL 以恢复序列号和事务ID状态</summary>
    private void ScanForState()
    {
        if (_walStream.Length == 0)
        {
            _nextSequenceNumber = 0;
            _nextTransactionId = 0;
            return;
        }

        var records = ReadAllRecords();
        foreach (var r in records)
        {
            if (r.SequenceNumber > _nextSequenceNumber)
                _nextSequenceNumber = r.SequenceNumber;
            if (r.TransactionId > _nextTransactionId)
                _nextTransactionId = r.TransactionId;
        }
    }

    /// <summary>WAL 记录的内存表示</summary>
    private struct WalRecord
    {
        public ulong TransactionId;
        public ulong SequenceNumber;
        public WalOperationType OperationType;
        public ulong TargetPageId;
        public byte[] Data;
    }
}
