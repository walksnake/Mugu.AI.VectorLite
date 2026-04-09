namespace Mugu.AI.VectorLite;

/// <summary>集合操作异常基类</summary>
public class CollectionException : VectorLiteException
{
    public CollectionException() { }
    public CollectionException(string message) : base(message) { }
    public CollectionException(string message, Exception innerException) : base(message, innerException) { }
}

/// <summary>集合不存在</summary>
public class CollectionNotFoundException : CollectionException
{
    public string CollectionName { get; }

    public CollectionNotFoundException(string collectionName)
        : base($"集合 '{collectionName}' 不存在")
    {
        CollectionName = collectionName;
    }
}

/// <summary>集合已存在（创建时重复）</summary>
public class CollectionAlreadyExistsException : CollectionException
{
    public string CollectionName { get; }

    public CollectionAlreadyExistsException(string collectionName)
        : base($"集合 '{collectionName}' 已存在")
    {
        CollectionName = collectionName;
    }
}
