# 质量门禁参考

> 本章详述功能基线测试和性能基准的使用方法、阈值配置、扩展方式。

---

## 1. 测试基础设施

### 1.1 TestDataGenerator — 测试数据生成器

```csharp
internal static class TestDataGenerator
{
    /// <summary>生成归一化的随机向量</summary>
    static float[] GenerateRandomVector(int dimensions, Random? random = null);

    /// <summary>生成带元数据的随机记录集合</summary>
    static List<VectorRecord> GenerateRecords(int count, int dimensions,
        Random? random = null, Dictionary<string, Func<Random, object>>? metadataGenerators = null);

    /// <summary>暴力搜索（用于验证 HNSW 结果的正确性）</summary>
    static List<(ulong Id, float Distance)> BruteForceSearch(
        Dictionary<ulong, float[]> vectors, float[] query,
        IDistanceFunction distanceFunction, int topK,
        HashSet<ulong>? candidateIds = null);
}
```

### 1.2 QualityGateConfig — 阈值配置

从 `quality-gate.json` 加载，结构如下：

```json
{
  "baselines": {
    "hnsw_recall_at_10": 0.85,
    "data_integrity_loss_rate": 0.0,
    "scalar_filter_precision": 1.0,
    "concurrent_error_rate": 0.0,
    "wal_recovery_loss_rate": 0.0,
    "large_scale_recall_at_10": 0.80
  },
  "benchmarks": {
    "cosine_distance_128d_ns": 500,
    "insert_1000_vectors_ms": 5000,
    "search_top10_ms": 50,
    "hybrid_query_ms": 100
  }
}
```

### 1.3 ThresholdValidator — 阈值校验器

```csharp
internal static class ThresholdValidator
{
    /// <summary>验证实际值是否满足阈值</summary>
    /// <param name="actual">实际测量值</param>
    /// <param name="threshold">阈值</param>
    /// <param name="mode">LowerIsBetter（如延迟）或 HigherIsBetter（如召回率）</param>
    static bool Validate(double actual, double threshold, ThresholdMode mode);
}
```

---

## 2. 功能基线测试（14 项）

所有基线测试使用 xUnit 框架，通过 `dotnet test` 运行。

### 2.1 HNSWAccuracyBaseline — HNSW 精度基线

| 测试 | 说明 | 通过标准 |
|------|------|----------|
| `Recall_At_10_Should_Meet_Baseline` | 1000 条 128 维向量，Top-10 召回率 | ≥ 0.85（对比暴力搜索） |
| `Search_Should_Return_Correct_TopK` | 验证返回结果数量正确 | 恰好 K 条 |
| `Search_Should_Return_Sorted_By_Distance` | 验证距离升序排列 | 严格有序 |

### 2.2 DataIntegrityBaseline — 数据完整性基线

| 测试 | 说明 | 通过标准 |
|------|------|----------|
| `InsertAndRetrieve_Should_PreserveData` | 插入后按 ID 取回，验证向量/元数据/文本一致 | 零数据丢失 |
| `Delete_Should_Remove_Record` | 删除后 Get 返回 null 且搜索不命中 | 完全删除 |

### 2.3 ScalarFilterBaseline — 标量过滤基线

| 测试 | 说明 | 通过标准 |
|------|------|----------|
| `EqualFilter_Should_Return_Exact_Matches` | 精确匹配过滤 | 精确率 100% |
| `RangeFilter_Should_Filter_Correctly` | 范围过滤 | 精确率 100% |
| `CompositeFilter_Should_Work` | AND/OR 组合过滤 | 精确率 100% |

### 2.4 ConcurrencyBaseline — 并发安全基线

| 测试 | 说明 | 通过标准 |
|------|------|----------|
| `ConcurrentInsert_Should_Not_Lose_Data` | 8 线程并发插入 100 条 | 数据完整，零丢失 |
| `ConcurrentSearchDuringInsert_Should_Not_Throw` | 写入同时搜索 | 无异常 |

### 2.5 WalRecoveryBaseline — WAL 恢复基线

| 测试 | 说明 | 通过标准 |
|------|------|----------|
| `Checkpoint_Should_Not_Lose_Data` | 写入 → 检查点 → 验证数据 | 零丢失 |
| `Wal_Append_And_Replay_Should_Be_Consistent` | WAL 追加 → 重放 → 验证一致性 | 完全一致 |

### 2.6 LargeScaleBaseline — 大规模数据基线

| 测试 | 说明 | 通过标准 |
|------|------|----------|
| `LargeScale_Insert_And_Search` | 5000 条 128 维向量，Top-10 搜索 | 召回率 ≥ 0.80 |
| `LargeScale_With_Filter` | 5000 条 + 过滤查询 | 返回结果均满足过滤条件 |

---

## 3. 性能基准（4 类）

所有基准使用 BenchmarkDotNet，通过 `dotnet run -c Release` 运行。

### 3.1 DistanceBenchmark

| 方法 | 测试内容 |
|------|----------|
| `CosineDistance_128D` | 128 维余弦距离计算 |
| `CosineDistance_1536D` | 1536 维余弦距离计算 |
| `EuclideanDistance_128D` | 128 维欧几里得距离计算 |
| `DotProductDistance_128D` | 128 维点积距离计算 |

### 3.2 InsertBenchmark

| 方法 | 测试内容 |
|------|----------|
| `Insert_1000_Vectors_128D` | 插入 1000 条 128 维向量 |
| `Insert_1000_Vectors_1536D` | 插入 1000 条 1536 维向量 |

### 3.3 SearchBenchmark

| 方法 | 测试内容 |
|------|----------|
| `Search_Top10_In_10000` | 10000 条中搜索 Top-10 |
| `Search_Top10_In_1000` | 1000 条中搜索 Top-10 |
| `Search_Top50_In_10000` | 10000 条中搜索 Top-50 |

### 3.4 HybridQueryBenchmark

| 方法 | 测试内容 |
|------|----------|
| `HybridQuery_Equal_Filter` | 精确过滤 + 向量搜索 |
| `HybridQuery_Range_Filter` | 范围过滤 + 向量搜索 |
| `HybridQuery_Composite_Filter` | 组合过滤 + 向量搜索 |

---

## 4. 阈值配置详解

`quality-gate.json` 中的每个阈值含义：

### 功能基线阈值

| 键 | 含义 | 阈值模式 | 默认值 |
|----|------|----------|--------|
| `hnsw_recall_at_10` | HNSW Top-10 召回率下限 | HigherIsBetter | 0.85 |
| `data_integrity_loss_rate` | 数据丢失率上限 | LowerIsBetter | 0.0 |
| `scalar_filter_precision` | 标量过滤精确率下限 | HigherIsBetter | 1.0 |
| `concurrent_error_rate` | 并发错误率上限 | LowerIsBetter | 0.0 |
| `wal_recovery_loss_rate` | WAL 恢复丢失率上限 | LowerIsBetter | 0.0 |
| `large_scale_recall_at_10` | 大规模 Top-10 召回率下限 | HigherIsBetter | 0.80 |

### 性能基准阈值

| 键 | 含义 | 单位 | 默认值 |
|----|------|------|--------|
| `cosine_distance_128d_ns` | 128 维余弦距离延迟上限 | 纳秒 | 500 |
| `insert_1000_vectors_ms` | 插入 1000 条延迟上限 | 毫秒 | 5000 |
| `search_top10_ms` | Top-10 搜索延迟上限 | 毫秒 | 50 |
| `hybrid_query_ms` | 混合查询延迟上限 | 毫秒 | 100 |

---

## 5. 自定义与扩展

### 5.1 调整阈值

编辑 `tests/Mugu.AI.VectorLite.QualityGate/quality-gate.json`：

```json
{
  "baselines": {
    "hnsw_recall_at_10": 0.90   // 提高召回率要求
  }
}
```

### 5.2 添加功能基线测试

1. 在 `Baselines/` 目录下创建新类
2. 使用 `[Fact]` 或 `[Theory]` 标注测试方法
3. 使用 `FluentAssertions` 编写断言
4. 运行 `dotnet test` 验证

```csharp
public class MyNewBaseline
{
    [Fact]
    public async Task MyTest_Should_Pass()
    {
        using var db = new VectorLiteDB(Path.GetTempFileName() + ".vldb");
        var collection = db.GetOrCreateCollection("test", 128);
        // ... 测试逻辑 ...
        result.Should().BeTrue();
    }
}
```

### 5.3 添加性能基准

1. 在 `Benchmarks/` 目录下创建新类
2. 使用 `[Benchmark]` 标注基准方法
3. 使用 `[GlobalSetup]` / `[GlobalCleanup]` 进行初始化/清理
4. 运行 `dotnet run -c Release` 验证

```csharp
[MemoryDiagnoser]
public class MyNewBenchmark
{
    private VectorLiteDB _db = null!;

    [GlobalSetup]
    public void Setup()
    {
        _db = new VectorLiteDB(Path.GetTempFileName() + ".vldb");
        // ... 准备数据 ...
    }

    [Benchmark]
    public async Task MyOperation()
    {
        // ... 被测操作 ...
    }

    [GlobalCleanup]
    public void Cleanup() => _db.Dispose();
}
```

---

## 6. CI 集成建议

```yaml
# GitHub Actions 示例
jobs:
  quality-gate:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-dotnet@v4
        with:
          dotnet-version: '8.0.x'

      # 功能基线（必须全部通过）
      - name: Run baselines
        run: dotnet test --configuration Release --logger "trx"

      # 性能基准（可选，用于追踪趋势）
      - name: Run benchmarks
        run: |
          cd tests/Mugu.AI.VectorLite.QualityGate
          dotnet run -c Release -- --exporters json
      
      - name: Upload benchmark results
        uses: actions/upload-artifact@v4
        with:
          name: benchmark-results
          path: tests/Mugu.AI.VectorLite.QualityGate/BenchmarkDotNet.Artifacts/
```
