namespace Mugu.AI.VectorLite;

/// <summary>VectorLite 所有异常的基类</summary>
public class VectorLiteException : Exception
{
    public VectorLiteException() { }
    public VectorLiteException(string message) : base(message) { }
    public VectorLiteException(string message, Exception innerException) : base(message, innerException) { }
}
