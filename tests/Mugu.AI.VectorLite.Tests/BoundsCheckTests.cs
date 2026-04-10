using System.Buffers.Binary;
using System.Text;
using FluentAssertions;
using Mugu.AI.VectorLite.Engine;
using Mugu.AI.VectorLite.Engine.Distance;
using Mugu.AI.VectorLite.Storage;

namespace Mugu.AI.VectorLite.Tests;

/// <summary>
/// 反序列化边界校验测试：验证恶意/损坏数据不会导致 OOM 或崩溃。
/// </summary>
public class BoundsCheckTests
{
    private static IDistanceFunction CosineFunc => DistanceFunctionFactory.Get(DistanceMetric.Cosine);

    [Fact]
    public void HNSWDeserialize_InvalidDimensions_ShouldThrow()
    {
        var data = new byte[8 + 4 + 4 + 8 + 4 + 1 + 4];
        var offset = 0;

        BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(offset), 1); offset += 8;
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(offset), 0); offset += 4;
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(offset), 1); offset += 4;
        BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(offset), 1); offset += 8;
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(offset), 0); offset += 4;
        data[offset] = 0; offset += 1;
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(offset), 999_999_999);

        var act = () => HNSWIndex.Deserialize(data.AsSpan(), CosineFunc);
        act.Should().Throw<StorageException>();
    }

    [Fact]
    public void HNSWDeserialize_TruncatedData_ShouldThrow()
    {
        var data = new byte[16];
        BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(0), 0);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(8), 0);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(12), 10);

        var act = () => HNSWIndex.Deserialize(data.AsSpan(), CosineFunc);
        act.Should().Throw<StorageException>();
    }

    [Fact]
    public void HNSWDeserialize_ExcessiveMaxLayer_ShouldThrow()
    {
        var data = new byte[16];
        BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(0), 0);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(8), 999);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(12), 0);

        var act = () => HNSWIndex.Deserialize(data.AsSpan(), CosineFunc);
        act.Should().Throw<StorageException>();
    }

    [Fact]
    public void RecordSerializer_ExcessiveDimensions_ShouldThrow()
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);

        bw.Write(1UL);         // RecordId
        bw.Write(999_999u);    // Dimensions > 100000

        ms.Position = 0;
        using var br = new BinaryReader(ms);

        var act = () => RecordSerializer.Read(br);
        act.Should().Throw<StorageException>();
    }

    [Fact]
    public void RecordSerializer_DeserializeInsert_ExcessiveNameLength_ShouldThrow()
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);

        bw.Write(999_999u); // nameLen > 4096
        bw.Write(new byte[100]);

        var act = () => RecordSerializer.DeserializeInsert(ms.ToArray());
        act.Should().Throw<StorageException>();
    }

    [Fact]
    public void RecordSerializer_DeserializeDelete_ExcessiveNameLength_ShouldThrow()
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);

        bw.Write(999_999u); // nameLen > 4096
        bw.Write(new byte[100]);

        var act = () => RecordSerializer.DeserializeDelete(ms.ToArray());
        act.Should().Throw<StorageException>();
    }

    [Fact]
    public void HNSWDeserialize_ValidData_ShouldSucceed()
    {
        // 构造一个合法的最小 HNSW 索引数据
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);

        bw.Write(1UL);   // EntryPointId = 1
        bw.Write(0);     // MaxLayer = 0
        bw.Write(1u);    // NodeCount = 1
        // Node 1:
        bw.Write(1UL);   // RecordId
        bw.Write(0);     // MaxLayer
        bw.Write((byte)0); // IsDeleted
        bw.Write(2);     // Dimensions = 2
        bw.Write(1.0f);  // vec[0]
        bw.Write(0.0f);  // vec[1]
        bw.Write(0u);    // Layer 0 neighbor count = 0
        bw.Flush();

        var data = ms.ToArray();
        var index = HNSWIndex.Deserialize(data.AsSpan(), CosineFunc);
        index.Count.Should().Be(1);
    }
}
