namespace Mugu.AI.VectorLite.Storage;

/// <summary>WAL 操作类型</summary>
internal enum WalOperationType : byte
{
    /// <summary>整页写入</summary>
    PageWrite = 0x01,

    /// <summary>页分配（Data 为空，仅记录新页ID和类型）</summary>
    PageAlloc = 0x02,

    /// <summary>页释放</summary>
    PageFree = 0x03,

    /// <summary>事务开始</summary>
    TxBegin = 0x10,

    /// <summary>事务提交</summary>
    TxCommit = 0x11,

    /// <summary>事务回滚</summary>
    TxRollback = 0x12,

    /// <summary>检查点标记</summary>
    Checkpoint = 0x20,

    /// <summary>逻辑操作：插入记录</summary>
    RecordInsert = 0x30,

    /// <summary>逻辑操作：删除记录</summary>
    RecordDelete = 0x31
}
