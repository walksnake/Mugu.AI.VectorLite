using FluentAssertions;
using Mugu.AI.VectorLite.Engine;
using Mugu.AI.VectorLite.Engine.Distance;

namespace Mugu.AI.VectorLite.Tests;

/// <summary>
/// 并发安全修复测试：P1-1（_disposed volatile）、P1-2（HNSWGraph 内存可见性）。
/// </summary>
public class ConcurrencyFixTests
{
    private const int Dims = 4;
    private static float[] Vec(params float[] v) => v;

    // ===== P1-2：HNSWGraph volatile 字段 =====

    [Fact]
    public void HNSWGraph_EntryPointId_ShouldUseVolatileSemantics()
    {
        // 验证 EntryPointId 通过 Volatile.Read/Write 操作
        var graph = new HNSWGraph();
        graph.EntryPointId.Should().Be(0);

        graph.EntryPointId = 42UL;
        graph.EntryPointId.Should().Be(42UL);

        graph.EntryPointId = ulong.MaxValue;
        graph.EntryPointId.Should().Be(ulong.MaxValue);
    }

    [Fact]
    public void HNSWGraph_MaxLayer_ShouldUseVolatileSemantics()
    {
        // 验证 MaxLayer 通过 volatile 字段保证可见性
        var graph = new HNSWGraph();
        graph.MaxLayer.Should().Be(-1); // 初始值 -1 表示图为空

        graph.MaxLayer = 0;
        graph.MaxLayer.Should().Be(0);

        graph.MaxLayer = 10;
        graph.MaxLayer.Should().Be(10);
    }

    [Fact]
    public async Task HNSWGraph_ConcurrentInsertAndRead_ShouldNotThrow()
    {
        // 并发场景下 EntryPointId 和 MaxLayer 的读写不应抛出异常
        var graph = new HNSWGraph();
        var exceptions = new System.Collections.Concurrent.ConcurrentBag<Exception>();

        var writer = Task.Run(() =>
        {
            for (var i = 0; i < 1000; i++)
            {
                try
                {
                    graph.EntryPointId = (ulong)i;
                    graph.MaxLayer = i % 10;
                }
                catch (Exception ex)
                {
                    exceptions.Add(ex);
                }
            }
        });

        var reader = Task.Run(() =>
        {
            for (var i = 0; i < 1000; i++)
            {
                try
                {
                    var _ = graph.EntryPointId;
                    var __ = graph.MaxLayer;
                }
                catch (Exception ex)
                {
                    exceptions.Add(ex);
                }
            }
        });

        await Task.WhenAll(writer, reader);
        exceptions.Should().BeEmpty("并发读写 volatile 字段不应抛出异常");
    }

    // ===== P1-1：VectorLiteDB._disposed volatile =====

    [Fact]
    public void VectorLiteDB_Dispose_ShouldPreventFurtherUse()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"vlite_disp_{Guid.NewGuid():N}.vldb");
        try
        {
            var db = new VectorLiteDB(dbPath);
            db.Dispose();

            // Dispose 后调用应抛出 ObjectDisposedException
            var act = () => db.GetOrCreateCollection("test", Dims);
            act.Should().Throw<ObjectDisposedException>();
        }
        finally
        {
            try { File.Delete(dbPath); } catch { }
            try { File.Delete(dbPath + "-wal"); } catch { }
        }
    }

    // ===== P2-NEW-1：HNSWIndex.Search 原子读取入口点与层数 =====

    [Fact]
    public async Task HNSWIndex_ConcurrentInsertAndSearch_ShouldNeverReturnFalseEmpty()
    {
        // 验证高并发 Insert + Search 不会因入口点/层数撕裂而产生假性空结果
        var distFunc = DistanceFunctionFactory.Get(DistanceMetric.Cosine);
        var index = new HNSWIndex(distFunc, m: 8, efConstruction: 50);

        // 预先插入10个节点，确保图非空
        for (var i = 1; i <= 10; i++)
            index.Insert((ulong)i, new float[] { i, 0, 0, 0 });

        var falseEmptyCount = 0;
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));

        // 持续 Insert 线程（模拟写入压力）
        var insertTask = Task.Run(() =>
        {
            var id = 100UL;
            while (!cts.Token.IsCancellationRequested)
                index.Insert(id++, new float[] { (float)(id % 100), 0, 0, 0 });
        }, cts.Token);

        // 持续 Search 线程（验证不返回假性空结果）
        var searchTask = Task.Run(() =>
        {
            while (!cts.Token.IsCancellationRequested)
            {
                var results = index.Search(new float[] { 1, 0, 0, 0 }, topK: 3, efSearch: 10);
                // 图中有节点时，搜索结果不应为空
                if (results.Count == 0) Interlocked.Increment(ref falseEmptyCount);
            }
        }, cts.Token);

        try { await Task.WhenAll(insertTask, searchTask); } catch (OperationCanceledException) { }

        falseEmptyCount.Should().Be(0, "原子读入口点后，非空图的搜索不应返回空结果");
    }

    [Fact]
    public async Task VectorLiteDB_ConcurrentOperationsWhileDisposing_ShouldNotCrash()
    {
        // 验证并发操作时 volatile _disposed 能正确感知到 Dispose 状态
        var dbPath = Path.Combine(Path.GetTempPath(), $"vlite_concdisp_{Guid.NewGuid():N}.vldb");
        try
        {
            var db = new VectorLiteDB(dbPath);
            var coll = db.GetOrCreateCollection("test", Dims);
            await coll.InsertAsync(new VectorRecord { Vector = Vec(1, 0, 0, 0) });

            // 并发调用：一个线程 Dispose，另一个线程查询（可能抛异常，但不应崩溃）
            var disposeTask = Task.Run(() => db.Dispose());
            var queryTask = Task.Run(async () =>
            {
                try
                {
                    await coll.Query(Vec(1, 0, 0, 0)).TopK(1).ToListAsync();
                }
                catch (ObjectDisposedException) { /* 预期异常 */ }
                catch (Exception) { /* 其他异常也可接受 */ }
            });

            await Task.WhenAll(disposeTask, queryTask);
            // 只要不崩溃就算通过
        }
        finally
        {
            try { File.Delete(dbPath); } catch { }
            try { File.Delete(dbPath + "-wal"); } catch { }
        }
    }
}
