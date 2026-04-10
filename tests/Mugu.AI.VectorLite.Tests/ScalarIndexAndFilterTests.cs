using FluentAssertions;
using Mugu.AI.VectorLite.Engine;

namespace Mugu.AI.VectorLite.Tests;

/// <summary>
/// ScalarIndex 和 FilterExpression 测试：覆盖增删改查、过滤表达式组合和边界条件。
/// </summary>
public class ScalarIndexAndFilterTests
{
    // ===== ScalarIndex 基本操作 =====

    [Fact]
    public void Add_ShouldIndexAllFields()
    {
        var idx = new ScalarIndex();
        idx.Add(1, new() { ["type"] = "doc", ["author"] = "alice" });
        idx.Count.Should().Be(1);
    }

    [Fact]
    public void Add_NullMetadata_ShouldBeNoOp()
    {
        var idx = new ScalarIndex();
        idx.Add(1, null);
        idx.Count.Should().Be(0);
    }

    [Fact]
    public void Add_EmptyMetadata_ShouldBeNoOp()
    {
        var idx = new ScalarIndex();
        idx.Add(1, new());
        idx.Count.Should().Be(0);
    }

    [Fact]
    public void Remove_ShouldCleanUpAllEntries()
    {
        var idx = new ScalarIndex();
        idx.Add(1, new() { ["type"] = "doc" });
        idx.Add(2, new() { ["type"] = "doc" });
        idx.Remove(1);
        idx.Count.Should().Be(1);
        idx.Filter(new EqualFilter("type", "doc")).Should().ContainSingle().Which.Should().Be(2UL);
    }

    [Fact]
    public void Remove_NonExistent_ShouldBeNoOp()
    {
        var idx = new ScalarIndex();
        idx.Remove(999);
        idx.Count.Should().Be(0);
    }

    [Fact]
    public void GetRecordMetadata_ShouldReturnCopy()
    {
        var idx = new ScalarIndex();
        idx.Add(1, new() { ["key"] = "value" });
        var meta = idx.GetRecordMetadata(1);
        meta.Should().NotBeNull();
        meta!["key"].Should().Be("value");
    }

    [Fact]
    public void GetRecordMetadata_NonExistent_ShouldReturnNull()
    {
        var idx = new ScalarIndex();
        idx.GetRecordMetadata(999).Should().BeNull();
    }

    [Fact]
    public void BulkLoad_ShouldRebuildIndex()
    {
        var idx = new ScalarIndex();
        idx.BulkLoad(new Dictionary<ulong, Dictionary<string, object>>
        {
            [1] = new() { ["cat"] = "A" },
            [2] = new() { ["cat"] = "B" },
            [3] = new() { ["cat"] = "A" }
        });
        idx.Count.Should().Be(3);
        idx.Filter(new EqualFilter("cat", "A")).Should().HaveCount(2);
    }

    // ===== EqualFilter =====

    [Fact]
    public void EqualFilter_ShouldMatchExactValue()
    {
        var idx = new ScalarIndex();
        idx.Add(1, new() { ["type"] = "doc" });
        idx.Add(2, new() { ["type"] = "note" });
        idx.Add(3, new() { ["type"] = "doc" });

        idx.Filter(new EqualFilter("type", "doc")).Should().BeEquivalentTo(new[] { 1UL, 3UL });
    }

    [Fact]
    public void EqualFilter_NoMatch_ShouldReturnEmpty()
    {
        var idx = new ScalarIndex();
        idx.Add(1, new() { ["type"] = "doc" });
        idx.Filter(new EqualFilter("type", "unknown")).Should().BeEmpty();
    }

    [Fact]
    public void EqualFilter_NonExistentField_ShouldReturnEmpty()
    {
        var idx = new ScalarIndex();
        idx.Add(1, new() { ["type"] = "doc" });
        idx.Filter(new EqualFilter("missing_field", "x")).Should().BeEmpty();
    }

    // ===== NotEqualFilter =====

    [Fact]
    public void NotEqualFilter_ShouldExcludeMatched()
    {
        var idx = new ScalarIndex();
        idx.Add(1, new() { ["type"] = "doc" });
        idx.Add(2, new() { ["type"] = "note" });
        idx.Add(3, new() { ["type"] = "doc" });

        var result = idx.Filter(new NotEqualFilter("type", "doc"));
        result.Should().ContainSingle().Which.Should().Be(2UL);
    }

    // ===== InFilter =====

    [Fact]
    public void InFilter_ShouldMatchAnyValue()
    {
        var idx = new ScalarIndex();
        idx.Add(1, new() { ["status"] = "active" });
        idx.Add(2, new() { ["status"] = "inactive" });
        idx.Add(3, new() { ["status"] = "pending" });

        var result = idx.Filter(new InFilter("status", new object[] { "active", "pending" }));
        result.Should().BeEquivalentTo(new[] { 1UL, 3UL });
    }

    // ===== RangeFilter =====

    [Fact]
    public void RangeFilter_Inclusive_ShouldMatchBoundaries()
    {
        var idx = new ScalarIndex();
        for (var i = 1; i <= 10; i++)
            idx.Add((ulong)i, new() { ["score"] = (long)i });

        var result = idx.Filter(new RangeFilter("score", 3L, 7L, true, true));
        result.Should().BeEquivalentTo(new ulong[] { 3, 4, 5, 6, 7 });
    }

    [Fact]
    public void RangeFilter_Exclusive_ShouldExcludeBoundaries()
    {
        var idx = new ScalarIndex();
        for (var i = 1; i <= 10; i++)
            idx.Add((ulong)i, new() { ["score"] = (long)i });

        var result = idx.Filter(new RangeFilter("score", 3L, 7L, false, false));
        result.Should().BeEquivalentTo(new ulong[] { 4, 5, 6 });
    }

    [Fact]
    public void RangeFilter_OpenEnded_ShouldWork()
    {
        var idx = new ScalarIndex();
        for (var i = 1; i <= 5; i++)
            idx.Add((ulong)i, new() { ["score"] = (long)i });

        // 仅下界
        var result = idx.Filter(new RangeFilter("score", lowerBound: 3L, lowerInclusive: true));
        result.Should().BeEquivalentTo(new ulong[] { 3, 4, 5 });

        // 仅上界
        result = idx.Filter(new RangeFilter("score", upperBound: 2L, upperInclusive: true));
        result.Should().BeEquivalentTo(new ulong[] { 1, 2 });
    }

    [Fact]
    public void RangeFilter_CrossType_LongVsDouble_ShouldWork()
    {
        var idx = new ScalarIndex();
        idx.Add(1, new() { ["val"] = 10L });
        idx.Add(2, new() { ["val"] = 20L });
        idx.Add(3, new() { ["val"] = 30L });

        // 使用 double 边界查询 long 值
        var result = idx.Filter(new RangeFilter("val", 15.0, 25.0, true, true));
        result.Should().ContainSingle().Which.Should().Be(2UL);
    }

    [Fact]
    public void RangeFilter_NonExistentField_ShouldReturnEmpty()
    {
        var idx = new ScalarIndex();
        idx.Add(1, new() { ["x"] = 10L });
        idx.Filter(new RangeFilter("missing", 0L, 100L, true, true)).Should().BeEmpty();
    }

    // ===== AndFilter =====

    [Fact]
    public void AndFilter_ShouldIntersect()
    {
        var idx = new ScalarIndex();
        idx.Add(1, new() { ["type"] = "doc", ["lang"] = "zh" });
        idx.Add(2, new() { ["type"] = "doc", ["lang"] = "en" });
        idx.Add(3, new() { ["type"] = "note", ["lang"] = "zh" });

        var result = idx.Filter(new AndFilter(
            new EqualFilter("type", "doc"),
            new EqualFilter("lang", "zh")
        ));
        result.Should().ContainSingle().Which.Should().Be(1UL);
    }

    [Fact]
    public void AndFilter_EmptyOperands_ShouldReturnEmpty()
    {
        var idx = new ScalarIndex();
        idx.Add(1, new() { ["x"] = "y" });
        idx.Filter(new AndFilter()).Should().BeEmpty();
    }

    // ===== OrFilter =====

    [Fact]
    public void OrFilter_ShouldUnion()
    {
        var idx = new ScalarIndex();
        idx.Add(1, new() { ["type"] = "doc" });
        idx.Add(2, new() { ["type"] = "note" });
        idx.Add(3, new() { ["type"] = "image" });

        var result = idx.Filter(new OrFilter(
            new EqualFilter("type", "doc"),
            new EqualFilter("type", "note")
        ));
        result.Should().BeEquivalentTo(new[] { 1UL, 2UL });
    }

    // ===== NotFilter =====

    [Fact]
    public void NotFilter_ShouldNegate()
    {
        var idx = new ScalarIndex();
        idx.Add(1, new() { ["type"] = "doc" });
        idx.Add(2, new() { ["type"] = "note" });

        var result = idx.Filter(new NotFilter(new EqualFilter("type", "doc")));
        result.Should().ContainSingle().Which.Should().Be(2UL);
    }

    // ===== 复合表达式 =====

    [Fact]
    public void ComplexFilter_AndOrNot_ShouldWork()
    {
        var idx = new ScalarIndex();
        idx.Add(1, new() { ["type"] = "doc", ["status"] = "active" });
        idx.Add(2, new() { ["type"] = "doc", ["status"] = "archived" });
        idx.Add(3, new() { ["type"] = "note", ["status"] = "active" });
        idx.Add(4, new() { ["type"] = "image", ["status"] = "active" });

        // (type=doc OR type=note) AND NOT status=archived
        var result = idx.Filter(new AndFilter(
            new OrFilter(
                new EqualFilter("type", "doc"),
                new EqualFilter("type", "note")
            ),
            new NotFilter(new EqualFilter("status", "archived"))
        ));
        result.Should().BeEquivalentTo(new[] { 1UL, 3UL });
    }

    // ===== 并发安全 =====

    [Fact]
    public async Task ScalarIndex_ConcurrentAccess_ShouldNotThrow()
    {
        var idx = new ScalarIndex();
        for (var i = 0; i < 100; i++)
            idx.Add((ulong)i, new() { ["group"] = (long)(i % 5) });

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        var writeTask = Task.Run(() =>
        {
            var j = 1000UL;
            while (!cts.IsCancellationRequested)
            {
                idx.Add(j, new() { ["group"] = (long)(j % 5) });
                j++;
                Thread.Sleep(1);
            }
        });

        var readTask = Task.Run(() =>
        {
            while (!cts.IsCancellationRequested)
            {
                idx.Filter(new EqualFilter("group", 0L));
                Thread.Sleep(1);
            }
        });

        var act = () => Task.WhenAll(writeTask, readTask);
        await act.Should().NotThrowAsync();
    }
}
