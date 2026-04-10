namespace Mugu.AI.VectorLite;

/// <summary>集合操作异常基类</summary>
public class CollectionException : VectorLiteException
{
    /// <inheritdoc />
    public CollectionException() { }
    /// <inheritdoc />
    public CollectionException(string message) : base(message) { }
    /// <inheritdoc />
    public CollectionException(string message, Exception innerException) : base(message, innerException) { }
}

/// <summary>集合不存在</summary>
public class CollectionNotFoundException : CollectionException
{
    /// <summary>集合名称</summary>
    public string CollectionName { get; }

    /// <summary>创建集合不存在异常</summary>
    /// <param name="collectionName">集合名称</param>
    public CollectionNotFoundException(string collectionName)
        : base($"集合 '{collectionName}' 不存在")
    {
        CollectionName = collectionName;
    }
}

/// <summary>集合已存在（创建时重复）</summary>
public class CollectionAlreadyExistsException : CollectionException
{
    /// <summary>集合名称</summary>
    public string CollectionName { get; }

    /// <summary>创建集合已存在异常</summary>
    /// <param name="collectionName">集合名称</param>
    public CollectionAlreadyExistsException(string collectionName)
        : base($"集合 '{collectionName}' 已存在")
    {
        CollectionName = collectionName;
    }
}
