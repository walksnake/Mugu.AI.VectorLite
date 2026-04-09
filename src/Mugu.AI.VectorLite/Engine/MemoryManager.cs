using System.Buffers;

namespace Mugu.AI.VectorLite.Engine;

/// <summary>
/// 内存管理器：通过对象池复用向量数组，减少 GC 压力。
/// </summary>
internal sealed class MemoryManager
{
    private readonly ArrayPool<float> _floatPool = ArrayPool<float>.Shared;

    /// <summary>从池中租借一个 float 数组</summary>
    internal float[] RentFloatArray(int minimumLength)
        => _floatPool.Rent(minimumLength);

    /// <summary>归还 float 数组到池中</summary>
    internal void ReturnFloatArray(float[] array, bool clearArray = false)
        => _floatPool.Return(array, clearArray);
}
