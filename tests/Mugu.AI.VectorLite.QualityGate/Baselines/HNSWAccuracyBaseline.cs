using FluentAssertions;
using Mugu.AI.VectorLite.QualityGate.Infrastructure;

namespace Mugu.AI.VectorLite.QualityGate.Baselines;

/// <summary>
/// HNSW 检索准确率基线测试。
/// 验证 Recall@K 不低于设定阈值。
/// </summary>
public class HNSWAccuracyBaseline : IDisposable
{
    private readonly string _dbPath;
    private readonly QualityGateConfig _config;

    public HNSWAccuracyBaseline()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"vlite_accuracy_{Guid.NewGuid():N}.vldb");
        _config = QualityGateConfig.Load();
    }

    [Fact]
    public async Task Recall_At10_Should_Meet_Threshold()
    {
        const int dimensions = 128;
        const int corpusSize = 1000;
        const int queryCount = 50;
        const int topK = 10;

        var corpus = TestDataGenerator.GenerateRandomVectors(corpusSize, dimensions, seed: 42);
        var queries = TestDataGenerator.GenerateRandomVectors(queryCount, dimensions, seed: 99);

        using var db = new VectorLiteDB(_dbPath);
        var collection = db.GetOrCreateCollection("accuracy_test", dimensions);

        // 插入所有向量
        for (var i = 0; i < corpus.Length; i++)
        {
            await collection.InsertAsync(new VectorRecord
            {
                Vector = corpus[i],
                Text = $"record_{i}"
            });
        }

        // 计算 Recall@10
        var totalRecall = 0.0;
        for (var q = 0; q < queries.Length; q++)
        {
            var bruteForceResults = TestDataGenerator.BruteForceKnn(queries[q], corpus, topK);
            var bruteForceIds = bruteForceResults.Select(r => (ulong)(r.Index + 1)).ToHashSet();

            var hnswResults = await collection.Query(queries[q]).TopK(topK).ToListAsync();
            var hnswIds = hnswResults.Select(r => r.Record.Id).ToHashSet();

            var overlap = hnswIds.Intersect(bruteForceIds).Count();
            totalRecall += (double)overlap / topK;
        }

        var avgRecall = totalRecall / queryCount;
        avgRecall.Should().BeGreaterOrEqualTo(_config.MinRecallAt10,
            $"Recall@{topK} = {avgRecall:F3} 低于阈值 {_config.MinRecallAt10}");
    }

    [Fact]
    public async Task Search_Returns_Correct_Top1()
    {
        const int dimensions = 64;

        using var db = new VectorLiteDB(_dbPath + ".top1");
        var collection = db.GetOrCreateCollection("top1_test", dimensions);

        // 插入一个已知向量
        var targetVector = new float[dimensions];
        Array.Fill(targetVector, 1.0f / MathF.Sqrt(dimensions));

        await collection.InsertAsync(new VectorRecord { Vector = targetVector, Text = "target" });

        // 插入一些噪声
        var noise = TestDataGenerator.GenerateRandomVectors(100, dimensions, seed: 7);
        foreach (var n in noise)
            await collection.InsertAsync(new VectorRecord { Vector = n });

        // 用目标向量本身查询，应返回完全匹配
        var results = await collection.Query(targetVector).TopK(1).ToListAsync();

        results.Should().HaveCount(1);
        results[0].Record.Text.Should().Be("target");
        results[0].Distance.Should().BeLessThan(0.01f);
    }

    public void Dispose()
    {
        TryDeleteFile(_dbPath);
        TryDeleteFile(_dbPath + "-wal");
        TryDeleteFile(_dbPath + ".top1");
        TryDeleteFile(_dbPath + ".top1-wal");
    }

    private static void TryDeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}
