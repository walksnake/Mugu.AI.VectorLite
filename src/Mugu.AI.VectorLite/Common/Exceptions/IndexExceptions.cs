namespace Mugu.AI.VectorLite;

/// <summary>索引层异常基类</summary>
public class IndexException : VectorLiteException
{
    /// <inheritdoc />
    public IndexException() { }
    /// <inheritdoc />
    public IndexException(string message) : base(message) { }
    /// <inheritdoc />
    public IndexException(string message, Exception innerException) : base(message, innerException) { }
}

/// <summary>向量维度不匹配</summary>
public class DimensionMismatchException : IndexException
{
    /// <summary>期望的维度</summary>
    public int Expected { get; }
    /// <summary>实际的维度</summary>
    public int Actual { get; }

    /// <summary>创建维度不匹配异常</summary>
    /// <param name="expected">期望维度</param>
    /// <param name="actual">实际维度</param>
    public DimensionMismatchException(int expected, int actual)
        : base($"向量维度不匹配：期望 {expected}，实际 {actual}")
    {
        Expected = expected;
        Actual = actual;
    }
}

/// <summary>索引容量已满</summary>
public class IndexFullException : IndexException
{
    /// <inheritdoc />
    public IndexFullException() { }
    /// <inheritdoc />
    public IndexFullException(string message) : base(message) { }
}
