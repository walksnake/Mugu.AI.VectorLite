using FluentAssertions;
using Mugu.AI.VectorLite.Engine;

namespace Mugu.AI.VectorLite.Tests;

/// <summary>
/// MemoryManager 对象池测试：验证数组租借和归还行为。
/// </summary>
public class MemoryManagerTests
{
    [Fact]
    public void RentFloatArray_ShouldReturnArrayOfAtLeastRequestedSize()
    {
        var mm = new MemoryManager();
        var arr = mm.RentFloatArray(128);
        arr.Should().NotBeNull();
        arr.Length.Should().BeGreaterThanOrEqualTo(128);
        mm.ReturnFloatArray(arr);
    }

    [Fact]
    public void ReturnFloatArray_WithClear_ShouldZeroArray()
    {
        var mm = new MemoryManager();
        var arr = mm.RentFloatArray(64);
        Array.Fill(arr, 1.0f);

        mm.ReturnFloatArray(arr, clearArray: true);

        // 重新租借（可能拿到同一块内存）
        var arr2 = mm.RentFloatArray(64);
        // 如果是同一块，应该已被清零
        if (ReferenceEquals(arr, arr2))
        {
            arr2.AsSpan(0, 64).ToArray().Should().OnlyContain(f => f == 0f);
        }
        mm.ReturnFloatArray(arr2);
    }

    [Fact]
    public void ReturnFloatArray_WithoutClear_ShouldNotThrow()
    {
        var mm = new MemoryManager();
        var arr = mm.RentFloatArray(32);
        Array.Fill(arr, 42f);
        var act = () => mm.ReturnFloatArray(arr, clearArray: false);
        act.Should().NotThrow();
    }

    [Fact]
    public void RentMultipleArrays_ShouldReturnIndependentArrays()
    {
        var mm = new MemoryManager();
        var a1 = mm.RentFloatArray(100);
        var a2 = mm.RentFloatArray(100);
        a1.Should().NotBeSameAs(a2);
        mm.ReturnFloatArray(a1);
        mm.ReturnFloatArray(a2);
    }

    [Fact]
    public void RentFloatArray_SmallSize_ShouldWork()
    {
        var mm = new MemoryManager();
        var arr = mm.RentFloatArray(1);
        arr.Length.Should().BeGreaterThanOrEqualTo(1);
        mm.ReturnFloatArray(arr);
    }
}
