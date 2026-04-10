namespace Mugu.AI.VectorLite;

/// <summary>VectorLite 所有异常的基类</summary>
public class VectorLiteException : Exception
{
    /// <inheritdoc />
    public VectorLiteException() { }
    /// <inheritdoc />
    public VectorLiteException(string message) : base(message) { }
    /// <inheritdoc />
    public VectorLiteException(string message, Exception innerException) : base(message, innerException) { }
}
