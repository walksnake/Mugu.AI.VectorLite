using System.Buffers.Binary;
using System.Text;
using FluentAssertions;
using Mugu.AI.VectorLite.Engine;
using Mugu.AI.VectorLite.Engine.Distance;
using Mugu.AI.VectorLite.Storage;

namespace Mugu.AI.VectorLite.Tests;

/// <summary>
/// 序列化/反序列化测试：覆盖 RecordSerializer、CollectionCatalog、ScalarIndexSerializer、HNSW 索引的完整往返。
/// </summary>
public class SerializationTests
{
    // ===== RecordSerializer =====

    [Fact]
    public void RecordSerializer_RoundTrip_FullRecord()
    {
        var record = new VectorRecord
        {
            Id = 42,
            Vector = new float[] { 1.1f, 2.2f, 3.3f, 4.4f },
            Metadata = new() { ["name"] = "测试", ["count"] = 10L, ["ratio"] = 3.14, ["flag"] = true },
            Text = "这是一段中文文本"
        };

        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);
        RecordSerializer.Write(bw, record);
        bw.Flush();

        ms.Position = 0;
        using var br = new BinaryReader(ms, Encoding.UTF8, leaveOpen: true);
        var deserialized = RecordSerializer.Read(br);

        deserialized.Id.Should().Be(42);
        deserialized.Vector.Should().BeEquivalentTo(record.Vector);
        deserialized.Metadata!["name"].Should().Be("测试");
        deserialized.Metadata["count"].Should().Be(10L);
        deserialized.Metadata["ratio"].Should().Be(3.14);
        deserialized.Metadata["flag"].Should().Be(true);
        deserialized.Text.Should().Be("这是一段中文文本");
    }

    [Fact]
    public void RecordSerializer_RoundTrip_NoMetadataNoText()
    {
        var record = new VectorRecord
        {
            Id = 1,
            Vector = new float[] { 1, 0, 0 },
            Metadata = null,
            Text = null
        };

        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);
        RecordSerializer.Write(bw, record);
        bw.Flush();

        ms.Position = 0;
        using var br = new BinaryReader(ms, Encoding.UTF8, leaveOpen: true);
        var deserialized = RecordSerializer.Read(br);

        deserialized.Id.Should().Be(1);
        deserialized.Vector.Should().HaveCount(3);
        deserialized.Metadata.Should().BeNull();
        deserialized.Text.Should().BeNull();
    }

    [Fact]
    public void RecordSerializer_RoundTrip_EmptyMetadata()
    {
        var record = new VectorRecord
        {
            Id = 5,
            Vector = new float[] { 1, 2 },
            Metadata = new(),
            Text = ""
        };

        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);
        RecordSerializer.Write(bw, record);
        bw.Flush();

        ms.Position = 0;
        using var br = new BinaryReader(ms, Encoding.UTF8, leaveOpen: true);
        var deserialized = RecordSerializer.Read(br);

        deserialized.Metadata.Should().BeNull(); // 空元数据不序列化
    }

    [Fact]
    public void RecordSerializer_InsertDeleteRoundTrip()
    {
        var record = new VectorRecord
        {
            Id = 100,
            Vector = new float[] { 1, 2, 3 },
            Metadata = new() { ["key"] = "val" },
            Text = "文本"
        };

        var insertData = RecordSerializer.SerializeInsert("my_collection", record);
        var (collName, rec) = RecordSerializer.DeserializeInsert(insertData);
        collName.Should().Be("my_collection");
        rec.Id.Should().Be(100);
        rec.Text.Should().Be("文本");

        var deleteData = RecordSerializer.SerializeDelete("my_collection", 100);
        var (delCollName, delId) = RecordSerializer.DeserializeDelete(deleteData);
        delCollName.Should().Be("my_collection");
        delId.Should().Be(100UL);
    }

    [Fact]
    public void RecordSerializer_ZeroDimensions_ShouldThrow()
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);
        bw.Write(1UL);  // RecordId
        bw.Write(0u);   // Dimensions = 0
        bw.Flush();

        ms.Position = 0;
        using var br = new BinaryReader(ms);
        var act = () => RecordSerializer.Read(br);
        act.Should().Throw<StorageException>();
    }

    // ===== CollectionCatalog =====

    [Fact]
    public void CollectionCatalog_RoundTrip()
    {
        var entries = new List<CollectionCatalogEntry>
        {
            new()
            {
                Name = "测试集合",
                Dimensions = 128,
                DistanceMetric = DistanceMetric.Cosine,
                HnswM = 16,
                HnswEfConstruction = 200,
                NextRecordId = 1000,
                HNSWRootPage = 10,
                ScalarIndexRootPage = 20,
                TextStoreRootPage = 30
            },
            new()
            {
                Name = "second",
                Dimensions = 64,
                DistanceMetric = DistanceMetric.Euclidean,
                HnswM = 32,
                HnswEfConstruction = 100,
                NextRecordId = 5,
                HNSWRootPage = 0,
                ScalarIndexRootPage = 0,
                TextStoreRootPage = 0
            }
        };

        var bytes = CollectionCatalog.Serialize(entries);
        var result = CollectionCatalog.Deserialize(bytes);

        result.Should().HaveCount(2);
        result[0].Name.Should().Be("测试集合");
        result[0].Dimensions.Should().Be(128);
        result[0].DistanceMetric.Should().Be(DistanceMetric.Cosine);
        result[0].HnswM.Should().Be(16);
        result[0].NextRecordId.Should().Be(1000);
        result[0].HNSWRootPage.Should().Be(10);
        result[1].Name.Should().Be("second");
        result[1].Dimensions.Should().Be(64);
    }

    [Fact]
    public void CollectionCatalog_EmptyList_RoundTrip()
    {
        var bytes = CollectionCatalog.Serialize(Array.Empty<CollectionCatalogEntry>());
        var result = CollectionCatalog.Deserialize(bytes);
        result.Should().BeEmpty();
    }

    [Fact]
    public void CollectionCatalog_InvalidVersion_ShouldThrow()
    {
        var data = new byte[] { 99 }; // 版本号 99，不支持
        var act = () => CollectionCatalog.Deserialize(data);
        act.Should().Throw<CorruptedFileException>();
    }

    [Fact]
    public void CollectionCatalog_ExcessiveNameLength_ShouldThrow()
    {
        // 构造含超长名称的数据
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);
        bw.Write((byte)1); // 版本号
        bw.Write(1u);      // 1 个条目
        bw.Write(999_999u); // nameLen > 1024
        bw.Flush();

        var act = () => CollectionCatalog.Deserialize(ms.ToArray());
        act.Should().Throw<CorruptedFileException>();
    }

    // ===== ScalarIndexSerializer =====

    [Fact]
    public void ScalarIndexSerializer_RoundTrip()
    {
        var index = new ScalarIndex();
        index.Add(1, new() { ["type"] = "doc", ["score"] = 42L });
        index.Add(2, new() { ["type"] = "note", ["score"] = 100L, ["rate"] = 3.14 });
        index.Add(3, new() { ["type"] = "doc", ["active"] = true });

        var bytes = ScalarIndexSerializer.Serialize(index);
        var restored = ScalarIndexSerializer.Deserialize(bytes);

        restored.Count.Should().Be(3);

        // 验证过滤功能
        var docs = restored.Filter(new EqualFilter("type", "doc"));
        docs.Should().HaveCount(2);
        docs.Should().Contain(1UL);
        docs.Should().Contain(3UL);

        var notes = restored.Filter(new EqualFilter("type", "note"));
        notes.Should().ContainSingle().Which.Should().Be(2UL);
    }

    [Fact]
    public void ScalarIndexSerializer_EmptyIndex_RoundTrip()
    {
        var index = new ScalarIndex();
        var bytes = ScalarIndexSerializer.Serialize(index);
        var restored = ScalarIndexSerializer.Deserialize(bytes);
        restored.Count.Should().Be(0);
    }

    [Fact]
    public void ScalarIndexSerializer_InvalidVersion_ShouldThrow()
    {
        var data = new byte[] { 99 };
        var act = () => ScalarIndexSerializer.Deserialize(data);
        act.Should().Throw<CorruptedFileException>();
    }

    // ===== HNSW 索引序列化/反序列化 =====

    [Fact]
    public void HNSWIndex_SerializeDeserialize_RoundTrip()
    {
        var distFunc = DistanceFunctionFactory.Get(DistanceMetric.Cosine);
        var index = new HNSWIndex(distFunc, m: 8, efConstruction: 50);

        // 插入一些记录
        index.Insert(1, new float[] { 1, 0, 0, 0 });
        index.Insert(2, new float[] { 0, 1, 0, 0 });
        index.Insert(3, new float[] { 0.5f, 0.5f, 0, 0 });
        index.Insert(4, new float[] { 0, 0, 1, 0 });
        index.Insert(5, new float[] { 0, 0, 0, 1 });

        var data = index.Serialize();
        var restored = HNSWIndex.Deserialize(data, distFunc, m: 8, efConstruction: 50);

        restored.Count.Should().Be(5);

        // 验证搜索结果一致
        var query = new float[] { 1, 0, 0, 0 };
        var results = restored.Search(query, 3, efSearch: 50);
        results.Should().HaveCount(3);
        results[0].RecordId.Should().Be(1); // 最近的
    }

    [Fact]
    public void HNSWIndex_SerializeDeserialize_WithDeleted()
    {
        var distFunc = DistanceFunctionFactory.Get(DistanceMetric.Cosine);
        var index = new HNSWIndex(distFunc, m: 8, efConstruction: 50);

        index.Insert(1, new float[] { 1, 0, 0 });
        index.Insert(2, new float[] { 0, 1, 0 });
        index.Insert(3, new float[] { 0, 0, 1 });
        index.MarkDeleted(2);

        var data = index.Serialize();
        var restored = HNSWIndex.Deserialize(data, distFunc, m: 8, efConstruction: 50);

        // 反序列化后搜索不应返回已删除节点
        var results = restored.Search(new float[] { 0, 1, 0 }, 3, efSearch: 50);
        results.Should().NotContain(r => r.RecordId == 2);
    }

    [Fact]
    public void HNSWIndex_Deserialize_InvalidVersion_ShouldThrow()
    {
        var data = new byte[17];
        data[0] = 99; // 不支持的版本
        var act = () => HNSWIndex.Deserialize(data, DistanceFunctionFactory.Get(DistanceMetric.Cosine));
        act.Should().Throw<StorageException>();
    }
}
