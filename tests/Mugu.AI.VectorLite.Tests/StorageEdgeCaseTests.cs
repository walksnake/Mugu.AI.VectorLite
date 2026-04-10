using FluentAssertions;
using Mugu.AI.VectorLite.Engine;
using Mugu.AI.VectorLite.Storage;

namespace Mugu.AI.VectorLite.Tests;

/// <summary>
/// 存储层边界条件测试：ScalarIndexSerializer 多类型、版本校验、WAL 边界。
/// </summary>
public class StorageEdgeCaseTests
{
    // ===== ScalarIndexSerializer 多类型序列化 =====

    [Fact]
    public void ScalarIndexSerializer_IntValue_ShouldRoundTrip()
    {
        var index = new ScalarIndex();
        index.Add(1, new Dictionary<string, object> { ["count"] = 42 }); // int
        var data = ScalarIndexSerializer.Serialize(index);

        var restored = ScalarIndexSerializer.Deserialize(data);
        var meta = restored.GetRecordMetadata(1);
        meta.Should().NotBeNull();
        // int 会被提升为 long
        meta!["count"].Should().Be(42L);
    }

    [Fact]
    public void ScalarIndexSerializer_FloatValue_ShouldRoundTrip()
    {
        var index = new ScalarIndex();
        index.Add(1, new Dictionary<string, object> { ["score"] = 3.14f }); // float
        var data = ScalarIndexSerializer.Serialize(index);

        var restored = ScalarIndexSerializer.Deserialize(data);
        var meta = restored.GetRecordMetadata(1);
        meta.Should().NotBeNull();
        // float 会被提升为 double
        ((double)meta!["score"]).Should().BeApproximately(3.14, 0.001);
    }

    [Fact]
    public void ScalarIndexSerializer_BoolValue_ShouldRoundTrip()
    {
        var index = new ScalarIndex();
        index.Add(1, new Dictionary<string, object>
        {
            ["active"] = true,
            ["deleted"] = false
        });
        var data = ScalarIndexSerializer.Serialize(index);

        var restored = ScalarIndexSerializer.Deserialize(data);
        var meta = restored.GetRecordMetadata(1);
        meta!["active"].Should().Be(true);
        meta["deleted"].Should().Be(false);
    }

    [Fact]
    public void ScalarIndexSerializer_LongValue_ShouldRoundTrip()
    {
        var index = new ScalarIndex();
        index.Add(1, new Dictionary<string, object> { ["bignum"] = long.MaxValue });
        var data = ScalarIndexSerializer.Serialize(index);

        var restored = ScalarIndexSerializer.Deserialize(data);
        var meta = restored.GetRecordMetadata(1);
        meta!["bignum"].Should().Be(long.MaxValue);
    }

    [Fact]
    public void ScalarIndexSerializer_DoubleValue_ShouldRoundTrip()
    {
        var index = new ScalarIndex();
        index.Add(1, new Dictionary<string, object> { ["pi"] = 3.14159265358979 });
        var data = ScalarIndexSerializer.Serialize(index);

        var restored = ScalarIndexSerializer.Deserialize(data);
        var meta = restored.GetRecordMetadata(1);
        ((double)meta!["pi"]).Should().BeApproximately(3.14159265358979, 1e-10);
    }

    [Fact]
    public void ScalarIndexSerializer_ObjectFallbackToString_ShouldSerializeAsString()
    {
        var index = new ScalarIndex();
        // DateTime 不是已知类型，应降级为字符串
        var dt = new DateTime(2024, 1, 15, 10, 30, 0, DateTimeKind.Utc);
        index.Add(1, new Dictionary<string, object> { ["created"] = dt });
        var data = ScalarIndexSerializer.Serialize(index);

        var restored = ScalarIndexSerializer.Deserialize(data);
        var meta = restored.GetRecordMetadata(1);
        meta!["created"].Should().BeOfType<string>();
        // 降级为 ToString()，值应包含日期信息
        meta["created"].ToString().Should().Contain("2024");
    }

    [Fact]
    public void ScalarIndexSerializer_StringValue_ShouldRoundTrip()
    {
        var index = new ScalarIndex();
        index.Add(1, new Dictionary<string, object> { ["name"] = "向量数据库" });
        var data = ScalarIndexSerializer.Serialize(index);

        var restored = ScalarIndexSerializer.Deserialize(data);
        var meta = restored.GetRecordMetadata(1);
        meta!["name"].Should().Be("向量数据库");
    }

    [Fact]
    public void ScalarIndexSerializer_EmptyIndex_ShouldRoundTrip()
    {
        var index = new ScalarIndex();
        var data = ScalarIndexSerializer.Serialize(index);

        var restored = ScalarIndexSerializer.Deserialize(data);
        restored.RecordMetadata.Should().BeEmpty();
    }

    [Fact]
    public void ScalarIndexSerializer_MultipleRecords_ShouldRoundTrip()
    {
        var index = new ScalarIndex();
        index.Add(1, new Dictionary<string, object> { ["type"] = "doc", ["size"] = 100L });
        index.Add(2, new Dictionary<string, object> { ["type"] = "note", ["size"] = 200L });
        index.Add(3, new Dictionary<string, object> { ["type"] = "doc", ["size"] = 300L });

        var data = ScalarIndexSerializer.Serialize(index);
        var restored = ScalarIndexSerializer.Deserialize(data);

        restored.RecordMetadata.Should().HaveCount(3);
        restored.GetRecordMetadata(2)!["type"].Should().Be("note");
    }

    [Fact]
    public void ScalarIndexSerializer_InvalidVersion_ShouldThrow()
    {
        var index = new ScalarIndex();
        index.Add(1, new Dictionary<string, object> { ["k"] = "v" });
        var data = ScalarIndexSerializer.Serialize(index);

        // 篡改版本号（第一个字节）
        var corrupted = data.ToArray();
        corrupted[0] = 99;

        var act = () => ScalarIndexSerializer.Deserialize(corrupted);
        act.Should().Throw<CorruptedFileException>();
    }

    [Fact]
    public void ScalarIndexSerializer_UnknownTypeByteInData_ShouldThrow()
    {
        // 手工构建含未知类型字节的二进制数据
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);

        bw.Write((byte)1);       // version
        bw.Write((uint)1);       // recordCount
        bw.Write((ulong)1);      // recordId
        bw.Write((uint)1);       // fieldCount
        var nameBytes = System.Text.Encoding.UTF8.GetBytes("field");
        bw.Write((uint)nameBytes.Length);
        bw.Write(nameBytes);
        bw.Write((byte)255);     // 未知类型字节
        bw.Flush();

        var act = () => ScalarIndexSerializer.Deserialize(ms.ToArray());
        act.Should().Throw<CorruptedFileException>();
    }

    // ===== CollectionCatalog 边界 =====

    [Fact]
    public void CollectionCatalog_EmptyList_ShouldRoundTrip()
    {
        var entries = new List<CollectionCatalogEntry>();
        var data = CollectionCatalog.Serialize(entries);
        var restored = CollectionCatalog.Deserialize(data);
        restored.Should().BeEmpty();
    }

    [Fact]
    public void CollectionCatalog_MultipleEntries_ShouldRoundTrip()
    {
        var entries = new List<CollectionCatalogEntry>
        {
            new() { Name = "alpha", Dimensions = 128, NextRecordId = 10 },
            new() { Name = "beta", Dimensions = 256, NextRecordId = 20 }
        };

        var data = CollectionCatalog.Serialize(entries);
        var restored = CollectionCatalog.Deserialize(data);

        restored.Should().HaveCount(2);
        restored[0].Name.Should().Be("alpha");
        restored[1].Dimensions.Should().Be(256);
    }

    [Fact]
    public void CollectionCatalog_LongName_ShouldThrowOnDeserialize()
    {
        // 名称长度校验在反序列化时进行（防御恶意/损坏数据）
        var longName = new string('x', 2000);
        var entries = new List<CollectionCatalogEntry>
        {
            new() { Name = longName, Dimensions = 4 }
        };

        // 序列化不会抛异常（写入方无长度限制）
        var data = CollectionCatalog.Serialize(entries);

        // 反序列化时应检测到名称过长
        var act = () => CollectionCatalog.Deserialize(data);
        act.Should().Throw<CorruptedFileException>();
    }

    // ===== RecordSerializer 边界 =====

    [Fact]
    public void RecordSerializer_InsertRoundTrip_WithNullText()
    {
        var record = new VectorRecord
        {
            Id = 42,
            Vector = new float[] { 1, 2, 3, 4 },
            Metadata = new() { ["key"] = "value" },
            Text = null
        };

        var data = RecordSerializer.SerializeInsert("test_coll", record);
        var (name, restored) = RecordSerializer.DeserializeInsert(data);

        name.Should().Be("test_coll");
        restored.Id.Should().Be(42);
        restored.Vector.Should().BeEquivalentTo(new float[] { 1, 2, 3, 4 });
        restored.Text.Should().BeNull();
    }

    [Fact]
    public void RecordSerializer_InsertRoundTrip_WithEmptyMetadata()
    {
        var record = new VectorRecord
        {
            Id = 1,
            Vector = new float[] { 0.5f, 0.5f },
            Metadata = new(),
            Text = "hello"
        };

        var data = RecordSerializer.SerializeInsert("coll", record);
        var (_, restored) = RecordSerializer.DeserializeInsert(data);

        // 空字典序列化为 metadataLen=0，反序列化后为 null
        restored.Metadata.Should().BeNull();
        restored.Text.Should().Be("hello");
    }

    [Fact]
    public void RecordSerializer_DeleteRoundTrip()
    {
        var data = RecordSerializer.SerializeDelete("my_collection", 12345UL);
        var (name, id) = RecordSerializer.DeserializeDelete(data);

        name.Should().Be("my_collection");
        id.Should().Be(12345UL);
    }

    [Fact]
    public void RecordSerializer_InsertWithChineseText_ShouldRoundTrip()
    {
        var record = new VectorRecord
        {
            Id = 1,
            Vector = new float[] { 1, 0 },
            Text = "这是一段中文文本，用于测试序列化。包含特殊字符：①②③"
        };

        var data = RecordSerializer.SerializeInsert("中文集合", record);
        var (name, restored) = RecordSerializer.DeserializeInsert(data);

        name.Should().Be("中文集合");
        restored.Text.Should().Be("这是一段中文文本，用于测试序列化。包含特殊字符：①②③");
    }
}
