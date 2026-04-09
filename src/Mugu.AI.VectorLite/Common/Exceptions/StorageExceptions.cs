namespace Mugu.AI.VectorLite;

/// <summary>存储层异常基类</summary>
public class StorageException : VectorLiteException
{
    public StorageException() { }
    public StorageException(string message) : base(message) { }
    public StorageException(string message, Exception innerException) : base(message, innerException) { }
}

/// <summary>文件格式损坏或不兼容</summary>
public class CorruptedFileException : StorageException
{
    public CorruptedFileException() { }
    public CorruptedFileException(string message) : base(message) { }
    public CorruptedFileException(string message, Exception innerException) : base(message, innerException) { }
}

/// <summary>WAL 日志损坏</summary>
public class WalCorruptedException : StorageException
{
    public WalCorruptedException() { }
    public WalCorruptedException(string message) : base(message) { }
    public WalCorruptedException(string message, Exception innerException) : base(message, innerException) { }
}

/// <summary>页操作异常（越界、CRC 校验失败等）</summary>
public class PageException : StorageException
{
    public PageException() { }
    public PageException(string message) : base(message) { }
    public PageException(string message, Exception innerException) : base(message, innerException) { }
}
