namespace Mugu.AI.VectorLite.Storage;

/// <summary>页面类型枚举</summary>
internal enum PageType : byte
{
    /// <summary>空闲页（链表节点）</summary>
    Free = 0x00,

    /// <summary>集合元数据</summary>
    CollectionMeta = 0x01,

    /// <summary>向量记录数据</summary>
    VectorData = 0x02,

    /// <summary>HNSW 图节点</summary>
    HNSWGraph = 0x03,

    /// <summary>标量倒排索引</summary>
    ScalarIndex = 0x04,

    /// <summary>溢出页（大记录续页）</summary>
    Overflow = 0x06
}
