using FluentAssertions;

namespace Mugu.AI.VectorLite.Tests;

/// <summary>
/// 异常类构造函数覆盖测试：确保所有异常类型的全部构造函数正常工作。
/// </summary>
public class ExceptionTests
{
    // ===== VectorLiteException =====

    [Fact]
    public void VectorLiteException_Parameterless()
    {
        var ex = new VectorLiteException();
        ex.Should().BeAssignableTo<Exception>();
        ex.Message.Should().NotBeEmpty();
    }

    [Fact]
    public void VectorLiteException_WithMessage()
    {
        var ex = new VectorLiteException("测试消息");
        ex.Message.Should().Be("测试消息");
    }

    [Fact]
    public void VectorLiteException_WithInnerException()
    {
        var inner = new InvalidOperationException("内部异常");
        var ex = new VectorLiteException("外部消息", inner);
        ex.Message.Should().Be("外部消息");
        ex.InnerException.Should().BeSameAs(inner);
    }

    // ===== StorageException =====

    [Fact]
    public void StorageException_Parameterless()
    {
        var ex = new StorageException();
        ex.Should().BeAssignableTo<VectorLiteException>();
    }

    [Fact]
    public void StorageException_WithMessage()
    {
        var ex = new StorageException("存储错误");
        ex.Message.Should().Be("存储错误");
    }

    [Fact]
    public void StorageException_WithInnerException()
    {
        var inner = new IOException("IO");
        var ex = new StorageException("存储错误", inner);
        ex.InnerException.Should().BeSameAs(inner);
    }

    // ===== CorruptedFileException =====

    [Fact]
    public void CorruptedFileException_Parameterless()
    {
        var ex = new CorruptedFileException();
        ex.Should().BeAssignableTo<StorageException>();
    }

    [Fact]
    public void CorruptedFileException_WithMessage()
    {
        var ex = new CorruptedFileException("文件损坏");
        ex.Message.Should().Be("文件损坏");
    }

    [Fact]
    public void CorruptedFileException_WithInnerException()
    {
        var inner = new Exception("原因");
        var ex = new CorruptedFileException("文件损坏", inner);
        ex.InnerException.Should().BeSameAs(inner);
    }

    // ===== WalCorruptedException =====

    [Fact]
    public void WalCorruptedException_Parameterless()
    {
        var ex = new WalCorruptedException();
        ex.Should().BeAssignableTo<StorageException>();
    }

    [Fact]
    public void WalCorruptedException_WithMessage()
    {
        var ex = new WalCorruptedException("WAL损坏");
        ex.Message.Should().Be("WAL损坏");
    }

    [Fact]
    public void WalCorruptedException_WithInnerException()
    {
        var inner = new Exception("原因");
        var ex = new WalCorruptedException("WAL损坏", inner);
        ex.InnerException.Should().BeSameAs(inner);
    }

    // ===== PageException =====

    [Fact]
    public void PageException_Parameterless()
    {
        var ex = new PageException();
        ex.Should().BeAssignableTo<StorageException>();
    }

    [Fact]
    public void PageException_WithMessage()
    {
        var ex = new PageException("页错误");
        ex.Message.Should().Be("页错误");
    }

    [Fact]
    public void PageException_WithInnerException()
    {
        var inner = new Exception("原因");
        var ex = new PageException("页错误", inner);
        ex.InnerException.Should().BeSameAs(inner);
    }

    // ===== IndexException =====

    [Fact]
    public void IndexException_Parameterless()
    {
        var ex = new IndexException();
        ex.Should().BeAssignableTo<VectorLiteException>();
    }

    [Fact]
    public void IndexException_WithMessage()
    {
        var ex = new IndexException("索引错误");
        ex.Message.Should().Be("索引错误");
    }

    [Fact]
    public void IndexException_WithInnerException()
    {
        var inner = new Exception("原因");
        var ex = new IndexException("索引错误", inner);
        ex.InnerException.Should().BeSameAs(inner);
    }

    // ===== DimensionMismatchException =====

    [Fact]
    public void DimensionMismatchException_ShouldStoreExpectedAndActual()
    {
        var ex = new DimensionMismatchException(128, 256);
        ex.Expected.Should().Be(128);
        ex.Actual.Should().Be(256);
        ex.Message.Should().Contain("128");
        ex.Message.Should().Contain("256");
        ex.Should().BeAssignableTo<IndexException>();
    }

    // ===== IndexFullException =====

    [Fact]
    public void IndexFullException_Parameterless()
    {
        var ex = new IndexFullException();
        ex.Should().BeAssignableTo<IndexException>();
    }

    [Fact]
    public void IndexFullException_WithMessage()
    {
        var ex = new IndexFullException("索引已满");
        ex.Message.Should().Be("索引已满");
    }

    // ===== CollectionException =====

    [Fact]
    public void CollectionException_Parameterless()
    {
        var ex = new CollectionException();
        ex.Should().BeAssignableTo<VectorLiteException>();
    }

    [Fact]
    public void CollectionException_WithMessage()
    {
        var ex = new CollectionException("集合错误");
        ex.Message.Should().Be("集合错误");
    }

    [Fact]
    public void CollectionException_WithInnerException()
    {
        var inner = new Exception("原因");
        var ex = new CollectionException("集合错误", inner);
        ex.InnerException.Should().BeSameAs(inner);
    }

    // ===== CollectionNotFoundException =====

    [Fact]
    public void CollectionNotFoundException_ShouldStoreCollectionName()
    {
        var ex = new CollectionNotFoundException("my_collection");
        ex.CollectionName.Should().Be("my_collection");
        ex.Message.Should().Contain("my_collection");
        ex.Should().BeAssignableTo<CollectionException>();
    }

    // ===== CollectionAlreadyExistsException =====

    [Fact]
    public void CollectionAlreadyExistsException_ShouldStoreCollectionName()
    {
        var ex = new CollectionAlreadyExistsException("dup_coll");
        ex.CollectionName.Should().Be("dup_coll");
        ex.Message.Should().Contain("dup_coll");
        ex.Should().BeAssignableTo<CollectionException>();
    }
}
