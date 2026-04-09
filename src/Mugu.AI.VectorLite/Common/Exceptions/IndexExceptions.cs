namespace Mugu.AI.VectorLite;

/// <summary>索引层异常基类</summary>
public class IndexException : VectorLiteException
{
    public IndexException() { }
    public IndexException(string message) : base(message) { }
    public IndexException(string message, Exception innerException) : base(message, innerException) { }
}

/// <summary>向量维度不匹配</summary>
public class DimensionMismatchException : IndexException
{
    public int Expected { get; }
    public int Actual { get; }

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
    public IndexFullException() { }
    public IndexFullException(string message) : base(message) { }
}
