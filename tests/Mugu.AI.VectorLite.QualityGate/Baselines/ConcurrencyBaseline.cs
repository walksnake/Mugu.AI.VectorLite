using FluentAssertions;
using Mugu.AI.VectorLite.QualityGate.Infrastructure;

namespace Mugu.AI.VectorLite.QualityGate.Baselines;

/// <summary>
/// 并发安全基线测试。
/// 验证多线程同时读写不会导致崩溃或数据损坏。
/// </summary>
public class ConcurrencyBaseline : IDisposable
{
    private readonly string _dbPath;

    public ConcurrencyBaseline()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"vlite_concurrency_{Guid.NewGuid():N}.vldb");
    }

    [Fact]
    public async Task Concurrent_Inserts_Should_Not_Lose_Data()
    {
        const int dimensions = 32;
        const int taskCount = 10;
        const int recordsPerTask = 50;

        using var db = new VectorLiteDB(_dbPath);
        var collection = db.GetOrCreateCollection("concurrent", dimensions);

        var tasks = Enumerable.Range(0, taskCount).Select(taskId => Task.Run(async () =>
        {
            for (var i = 0; i < recordsPerTask; i++)
            {
                await collection.InsertAsync(new VectorRecord
                {
                    Vector = TestDataGenerator.GenerateRandomVector(dimensions),
                    Metadata = new() { ["task"] = (long)taskId },
                    Text = $"task_{taskId}_record_{i}"
                });
            }
        })).ToArray();

        await Task.WhenAll(tasks);

        collection.Count.Should().Be(taskCount * recordsPerTask);
    }

    [Fact]
    public async Task Concurrent_Read_Write_Should_Not_Throw()
    {
        const int dimensions = 32;

        using var db = new VectorLiteDB(_dbPath + ".rw");
        var collection = db.GetOrCreateCollection("rw_test", dimensions);

        // 预填充数据
        var records = TestDataGenerator.GenerateRecords(100, dimensions);
        await collection.InsertBatchAsync(records);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        // 启动写任务
        var writeTask = Task.Run(async () =>
        {
            while (!cts.IsCancellationRequested)
            {
                try
                {
                    await collection.InsertAsync(new VectorRecord
                    {
                        Vector = TestDataGenerator.GenerateRandomVector(dimensions),
                        Text = "concurrent_write"
                    });
                    await Task.Delay(10);
                }
                catch (OperationCanceledException) { break; }
            }
        });

        // 启动读任务
        var readTask = Task.Run(async () =>
        {
            while (!cts.IsCancellationRequested)
            {
                try
                {
                    var query = TestDataGenerator.GenerateRandomVector(dimensions);
                    await collection.Query(query).TopK(5).ToListAsync();
                    await Task.Delay(10);
                }
                catch (OperationCanceledException) { break; }
            }
        });

        var act = () => Task.WhenAll(writeTask, readTask);
        await act.Should().NotThrowAsync();
    }

    public void Dispose()
    {
        foreach (var suffix in new[] { "", ".rw" })
        {
            TryDeleteFile(_dbPath + suffix);
            TryDeleteFile(_dbPath + suffix + "-wal");
        }
    }

    private static void TryDeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}
