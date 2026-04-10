using System.Buffers;
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

    private FileStream _walStream;
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
    /// 检查点操作：将WAL中已提交事务的页写入刷入主文件，原子替换WAL。
    /// </summary>
    internal void Checkpoint(PageManager pageManager)
    {
        lock (_lock)
        {
            _logger?.LogInformation("开始检查点...");

            var records = ReadAllRecords();
            var committedTxIds = new HashSet<ulong>();

            foreach (var record in records)
            {
                if (record.OperationType == WalOperationType.TxCommit)
                    committedTxIds.Add(record.TransactionId);
            }

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
            pageManager.Header.LastCheckpoint =
                (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            pageManager.FlushHeader();

            // 原子重写 WAL：先写临时文件再替换，防止截断期间崩溃丢失数据
            var tempPath = FilePath + ".tmp";
            ulong tempSeq = 0;
            using (var tempStream = new FileStream(tempPath, FileMode.Create,
                FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                WriteRecordToStream(tempStream, ref tempSeq, 0,
                    WalOperationType.Checkpoint, 0, ReadOnlySpan<byte>.Empty);
                tempStream.Flush(true);
            }

            _walStream.Close();
            File.Move(tempPath, FilePath, overwrite: true);
            _walStream = new FileStream(FilePath, FileMode.OpenOrCreate,
                FileAccess.ReadWrite, FileShare.Read, 4096, FileOptions.WriteThrough);
            _nextSequenceNumber = tempSeq;
            _nextTransactionId = 0;

            _logger?.LogInformation("检查点完成: 刷入 {Count} 个页写入", pageWrites);
        }
    }

    /// <summary>
    /// 崩溃恢复：读取WAL，重放已提交的物理事务，
    /// 逻辑记录通过原子重写保留在新 WAL 中供后续恢复。
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

            var committedTxIds = new HashSet<ulong>();
            foreach (var record in records)
            {
                if (record.OperationType == WalOperationType.TxCommit)
                    committedTxIds.Add(record.TransactionId);
            }

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

            // 收集已提交的逻辑记录
            var logicalRecords = new List<(WalOperationType OpType, byte[] Data)>();
            foreach (var record in records)
            {
                if (IsLogicalOperation(record.OperationType)
                    && committedTxIds.Contains(record.TransactionId))
                {
                    logicalRecords.Add((record.OperationType, record.Data ?? []));
                }
            }

            // 原子重写 WAL：先写临时文件再替换，确保逻辑记录不丢失
            var tempPath = FilePath + ".tmp";
            ulong tempSeq = 0;
            ulong tempTxId = 0;
            using (var tempStream = new FileStream(tempPath, FileMode.Create,
                FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                WriteRecordToStream(tempStream, ref tempSeq, 0,
                    WalOperationType.Checkpoint, 0, ReadOnlySpan<byte>.Empty);

                foreach (var (opType, data) in logicalRecords)
                {
                    var txId = ++tempTxId;
                    WriteRecordToStream(tempStream, ref tempSeq, txId,
                        WalOperationType.TxBegin, 0, ReadOnlySpan<byte>.Empty);
                    WriteRecordToStream(tempStream, ref tempSeq, txId,
                        opType, 0, data);
                    WriteRecordToStream(tempStream, ref tempSeq, txId,
                        WalOperationType.TxCommit, 0, ReadOnlySpan<byte>.Empty);
                }

                tempStream.Flush(true);
            }

            _walStream.Close();
            File.Move(tempPath, FilePath, overwrite: true);
            _walStream = new FileStream(FilePath, FileMode.OpenOrCreate,
                FileAccess.ReadWrite, FileShare.Read, 4096, FileOptions.WriteThrough);
            _nextSequenceNumber = tempSeq;
            _nextTransactionId = tempTxId;

            _logger?.LogInformation(
                "WAL 恢复完成: 重放 {PhysCount} 个页写入, 保留 {LogCount} 条逻辑记录",
                replayed, logicalRecords.Count);
        }
    }

    /// <summary>
    /// 追加一条逻辑 WAL 记录（RecordInsert / RecordDelete）。
    /// 自动包装为微事务（TxBegin → op → TxCommit）并 fsync。
    /// </summary>
    internal void AppendLogical(WalOperationType opType, ReadOnlySpan<byte> data)
    {
        if (!IsLogicalOperation(opType))
            throw new ArgumentException($"非逻辑操作类型: {opType}", nameof(opType));

        lock (_lock)
        {
            var txId = ++_nextTransactionId;
            WriteRecord(txId, WalOperationType.TxBegin, 0, ReadOnlySpan<byte>.Empty);
            WriteRecord(txId, opType, 0, data);
            WriteRecord(txId, WalOperationType.TxCommit, 0, ReadOnlySpan<byte>.Empty);
            _walStream.Flush(true);
        }
    }

    /// <summary>
    /// 读取 WAL 中自上次检查点以来所有已提交的逻辑记录。
    /// 用于数据库启动时的逻辑恢复。
    /// </summary>
    internal List<LogicalWalRecord> ReadLogicalRecords()
    {
        lock (_lock)
        {
            var allRecords = ReadAllRecords();
            var result = new List<LogicalWalRecord>();

            // 找到最后一个 Checkpoint 标记的位置
            var lastCheckpointIndex = -1;
            for (var i = allRecords.Count - 1; i >= 0; i--)
            {
                if (allRecords[i].OperationType == WalOperationType.Checkpoint)
                {
                    lastCheckpointIndex = i;
                    break;
                }
            }

            // 收集已提交事务
            var committedTxIds = new HashSet<ulong>();
            var startIndex = lastCheckpointIndex + 1;
            for (var i = startIndex; i < allRecords.Count; i++)
            {
                if (allRecords[i].OperationType == WalOperationType.TxCommit)
                    committedTxIds.Add(allRecords[i].TransactionId);
            }

            // 收集已提交的逻辑记录
            for (var i = startIndex; i < allRecords.Count; i++)
            {
                var record = allRecords[i];
                if (IsLogicalOperation(record.OperationType)
                    && committedTxIds.Contains(record.TransactionId))
                {
                    result.Add(new LogicalWalRecord
                    {
                        OperationType = record.OperationType,
                        Data = record.Data ?? []
                    });
                }
            }

            return result;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _walStream.Dispose();
    }

    /// <summary>写入一条 WAL 记录到当前 WAL 流</summary>
    private void WriteRecord(ulong txId, WalOperationType opType, ulong pageId, ReadOnlySpan<byte> data)
    {
        _walStream.Seek(0, SeekOrigin.End);
        WriteRecordToStream(_walStream, ref _nextSequenceNumber, txId, opType, pageId, data);
    }

    /// <summary>写入一条 WAL 记录到指定流（支持原子重写场景）</summary>
    private static void WriteRecordToStream(
        Stream stream, ref ulong nextSeqNo,
        ulong txId, WalOperationType opType, ulong pageId, ReadOnlySpan<byte> data)
    {
        if (data.Length > 256 * 1024 * 1024 - MinRecordSize)
            throw new StorageException($"WAL 数据载荷过大: {data.Length} 字节");

        var recordLength = (uint)(MinRecordSize + data.Length);
        var seqNo = ++nextSeqNo;

        var buffer = ArrayPool<byte>.Shared.Rent((int)recordLength);
        try
        {
            var span = buffer.AsSpan(0, (int)recordLength);
            BinaryPrimitives.WriteUInt32LittleEndian(span[0..4], recordLength);
            BinaryPrimitives.WriteUInt64LittleEndian(span[4..12], txId);
            BinaryPrimitives.WriteUInt64LittleEndian(span[12..20], seqNo);
            span[20] = (byte)opType;
            BinaryPrimitives.WriteUInt64LittleEndian(span[21..29], pageId);
            BinaryPrimitives.WriteUInt32LittleEndian(span[29..33], (uint)data.Length);
            data.CopyTo(span[33..]);

            var crc = Crc32.HashToUInt32(span[..^4]);
            BinaryPrimitives.WriteUInt32LittleEndian(span[^4..], crc);

            stream.Write(span);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
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
            // 上界校验：单条 WAL 记录不应超过 256MB
            const uint MaxRecordSize = 256 * 1024 * 1024;
            if (recordLength < MinRecordSize || recordLength > MaxRecordSize)
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

    /// <summary>WAL 记录的内存表示（内部）</summary>
    private struct WalRecord
    {
        public ulong TransactionId;
        public ulong SequenceNumber;
        public WalOperationType OperationType;
        public ulong TargetPageId;
        public byte[] Data;
    }

    /// <summary>判断操作类型是否为逻辑操作</summary>
    private static bool IsLogicalOperation(WalOperationType opType)
        => opType == WalOperationType.RecordInsert || opType == WalOperationType.RecordDelete;

    /// <summary>逻辑 WAL 记录（对外暴露，用于恢复）</summary>
    internal struct LogicalWalRecord
    {
        public WalOperationType OperationType;
        public byte[] Data;
    }
}
