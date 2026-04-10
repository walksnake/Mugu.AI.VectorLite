namespace Mugu.AI.VectorLite;

/// <summary>存储层异常基类</summary>
public class StorageException : VectorLiteException
{
    /// <inheritdoc />
    public StorageException() { }
    /// <inheritdoc />
    public StorageException(string message) : base(message) { }
    /// <inheritdoc />
    public StorageException(string message, Exception innerException) : base(message, innerException) { }
}

/// <summary>文件格式损坏或不兼容</summary>
public class CorruptedFileException : StorageException
{
    /// <inheritdoc />
    public CorruptedFileException() { }
    /// <inheritdoc />
    public CorruptedFileException(string message) : base(message) { }
    /// <inheritdoc />
    public CorruptedFileException(string message, Exception innerException) : base(message, innerException) { }
}

/// <summary>WAL 日志损坏</summary>
public class WalCorruptedException : StorageException
{
    /// <inheritdoc />
    public WalCorruptedException() { }
    /// <inheritdoc />
    public WalCorruptedException(string message) : base(message) { }
    /// <inheritdoc />
    public WalCorruptedException(string message, Exception innerException) : base(message, innerException) { }
}

/// <summary>页操作异常（越界、CRC 校验失败等）</summary>
public class PageException : StorageException
{
    /// <inheritdoc />
    public PageException() { }
    /// <inheritdoc />
    public PageException(string message) : base(message) { }
    /// <inheritdoc />
    public PageException(string message, Exception innerException) : base(message, innerException) { }
}
