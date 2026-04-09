# 基准测试与质量门禁详细设计

> 父文档：[详细设计索引](index.md)

## 1. 设计目标

提供一个统一的测试项目 `Mugu.AI.VectorLite.QualityGate`，同时覆盖**功能基线验证**和**性能基准测量**，使得：

- **每次代码变更**只要通过该项目的全部测试，即可认定质量达标。
- 功能基线确保核心行为的**正确性不退化**（准确率、数据完整性、崩溃恢复）。
- 性能基准确保关键路径的**吞吐量和延迟不退化**，并追踪内存分配趋势。
- 所有基准均定义**量化阈值**，可被 CI 流水线自动判定通过/失败。

## 2. 项目定位与运行模式

`Mugu.AI.VectorLite.QualityGate` 既是 **xUnit 测试项目**（被 `dotnet test` 驱动），又是 **BenchmarkDotNet 控制台应用**（被 `dotnet run` 驱动）。

```text
tests/Mugu.AI.VectorLite.QualityGate/
├── Mugu.AI.VectorLite.QualityGate.csproj   # OutputType=Exe, net8.0
├── Program.cs                               # BenchmarkDotNet 入口
├── quality-gate.json                        # 阈值配置文件
│
├── Baselines/                               # 功能基线测试 (xUnit)
│   ├── HNSWAccuracyBaseline.cs
│   ├── WalRecoveryBaseline.cs
│   ├── DataIntegrityBaseline.cs
│   ├── ConcurrencyBaseline.cs
│   ├── ScalarFilterBaseline.cs
│   └── LargeScaleBaseline.cs
│
├── Benchmarks/                              # 性能基准 (BenchmarkDotNet)
│   ├── InsertBenchmark.cs
│   ├── SearchBenchmark.cs
│   ├── HybridQueryBenchmark.cs
│   ├── DistanceBenchmark.cs
│   ├── CheckpointBenchmark.cs
│   └── MemoryBenchmark.cs
│
├── Infrastructure/                          # 测试基础设施
│   ├── QualityGateConfig.cs                 # 阈值配置模型
│   ├── ThresholdValidator.cs                # 基准结果 vs 阈值校验
│   ├── TestDataGenerator.cs                 # 标准测试数据集生成
│   └── BenchmarkResultExporter.cs           # 结果导出（JSON/Markdown）
│
└── Datasets/                                # 预生成的标准数据集
    └── README.md                            # 数据集说明
```

### 2.1 两种运行模式

| 模式 | 命令 | 用途 | 耗时 |
|------|------|------|------|
| 功能基线 | `dotnet test` | CI 每次提交必跑，验证正确性 | < 2 分钟 |
| 性能基准 | `dotnet run -c Release -- --filter *` | 定期/手动运行，检测性能退化 | 5 ~ 15 分钟 |
| 质量门禁全量 | `dotnet run -c Release -- --quality-gate` | CI 里程碑/发布前全量运行 | 15 ~ 30 分钟 |

`--quality-gate` 模式下 `Program.cs` 依次执行：
1. 运行全部 xUnit 功能基线测试（通过 `Microsoft.VisualStudio.TestPlatform` 编程调用）。
2. 运行全部 BenchmarkDotNet 基准测试。
3. 使用 `ThresholdValidator` 对比结果与 `quality-gate.json` 中的阈值。
4. 输出综合报告，任一项不达标则以非零退出码终止。

## 3. 功能基线测试设计

功能基线测试使用 xUnit 编写，每个测试类覆盖一个核心质量维度。测试中使用**标准数据集**（由 `TestDataGenerator` 确定性生成）以确保可重复性。

### 3.1 HNSW 准确率基线 (HNSWAccuracyBaseline)

验证向量检索的**召回率**（Recall@K）不低于阈值。

```csharp
/// <summary>
/// HNSW 索引准确率基线测试。
/// 通过暴力搜索获得真实Top-K，与 HNSW 结果对比计算 Recall@K。
/// </summary>
public sealed class HNSWAccuracyBaseline : IDisposable
{
    // ── 测试参数 ──
    // 数据集大小: 10,000 条
    // 向量维度: 128
    // 查询数: 100
    // K 值: 10, 50, 100
    // 距离度量: Cosine, Euclidean, DotProduct

    [Theory]
    [MemberData(nameof(AccuracyScenarios))]
    public void Recall_AtK_ShouldMeetThreshold(
        int datasetSize, int dimensions, int k,
        DistanceMetric metric, float minRecall)
    {
        // 1. 使用 TestDataGenerator 生成确定性随机向量集
        // 2. 构建 HNSW 索引
        // 3. 对每个查询向量：
        //    a. 暴力搜索获取真实 Top-K (groundTruth)
        //    b. HNSW 搜索获取近似 Top-K (predicted)
        //    c. recall = |groundTruth ∩ predicted| / K
        // 4. 平均 recall >= minRecall
    }
}
```

**默认阈值**：

| 场景 | K | 最低 Recall |
|------|---|------------|
| 10K 向量, 128维, Cosine | 10 | 0.95 |
| 10K 向量, 128维, Cosine | 50 | 0.90 |
| 10K 向量, 128维, Cosine | 100 | 0.85 |
| 10K 向量, 768维, Cosine | 10 | 0.95 |

### 3.2 WAL 崩溃恢复基线 (WalRecoveryBaseline)

验证 WAL 机制在各种崩溃场景下能正确恢复数据。

```csharp
public sealed class WalRecoveryBaseline : IDisposable
{
    [Fact]
    public void CommittedRecords_ShouldSurvive_CrashBeforeCheckpoint()
    {
        // 1. 打开数据库，插入 N 条记录（每条单独事务提交）
        // 2. 不执行检查点，直接释放 FileStorage（模拟崩溃）
        // 3. 重新打开数据库（触发 WAL Replay）
        // 4. 验证所有 N 条记录均可读取且数据完整
    }

    [Fact]
    public void UncommittedRecords_ShouldBeDiscarded_AfterCrash()
    {
        // 1. 插入 N 条已提交记录
        // 2. 开始新事务，写入 M 条记录但不提交
        // 3. 模拟崩溃
        // 4. 恢复后验证：只有 N 条记录存在，M 条被丢弃
    }

    [Fact]
    public void Checkpoint_ThenCrash_ShouldRecoverCleanly()
    {
        // 1. 插入记录 → 检查点 → 再插入记录 → 崩溃
        // 2. 恢复后验证所有已提交记录均存在
    }

    [Fact]
    public void CorruptedWalTail_ShouldRecoverUpToLastValidRecord()
    {
        // 1. 插入记录，手动截断 WAL 文件尾部若干字节
        // 2. 恢复后验证：只丢失被截断的未完整记录
    }
}
```

### 3.3 数据完整性基线 (DataIntegrityBaseline)

端到端验证完整的 CRUD 生命周期。

```csharp
public sealed class DataIntegrityBaseline : IDisposable
{
    [Fact]
    public void FullLifecycle_InsertQueryUpdateDelete_ShouldBeConsistent()
    {
        // 1. 创建集合
        // 2. 插入 1000 条记录
        // 3. 逐条 Get 验证数据一致（向量值、元数据、文本）
        // 4. 查询验证结果集包含预期记录
        // 5. Upsert 更新 500 条记录的元数据
        // 6. 重新查询验证更新生效
        // 7. 删除 300 条记录
        // 8. 验证已删除记录 Get 返回 null、不出现在搜索结果中
        // 9. Count 验证剩余数量正确
    }

    [Fact]
    public void VectorDimension_Mismatch_ShouldThrow()
    {
        // 集合维度 128，尝试插入 256 维向量
        // 验证抛出 DimensionMismatchException
    }

    [Fact]
    public void MultipleCollections_ShouldBeIsolated()
    {
        // 创建两个集合，各自插入不同数据
        // 验证跨集合无数据泄漏
    }

    [Fact]
    public void DatabaseReopen_ShouldPreserveAllData()
    {
        // 1. 插入数据 → Checkpoint → Dispose
        // 2. 重新 new VectorLiteDB(同一路径)
        // 3. 验证所有数据完整（含 HNSW 索引准确性）
    }
}
```

### 3.4 并发安全基线 (ConcurrencyBaseline)

验证多线程环境下的数据一致性。

```csharp
public sealed class ConcurrencyBaseline : IDisposable
{
    [Fact]
    public void ConcurrentReads_ShouldNotCorruptOrBlock()
    {
        // 1. 预插入 5000 条记录
        // 2. 启动 16 个并发读线程，各执行 100 次查询
        // 3. 验证无异常、结果一致
    }

    [Fact]
    public void ConcurrentWriteAndRead_ShouldMaintainConsistency()
    {
        // 1. 写线程：持续插入记录
        // 2. 读线程：持续查询
        // 3. 运行 5 秒后停止
        // 4. 验证：所有已提交写入均可被后续读取到、无数据损坏
    }

    [Fact]
    public void ConcurrentCheckpoint_ShouldNotLoseData()
    {
        // 1. 写线程持续插入
        // 2. 另一线程每 500ms 执行一次 Checkpoint
        // 3. 运行 5 秒后验证数据完整
    }
}
```

### 3.5 标量过滤基线 (ScalarFilterBaseline)

验证各种过滤表达式的正确性。

```csharp
public sealed class ScalarFilterBaseline : IDisposable
{
    // 预插入 1000 条记录，元数据含：
    //   category: "A" | "B" | "C"
    //   score: 0.0 ~ 1.0 (double)
    //   active: true | false
    //   tags: "x" | "y" | "z"

    [Fact] public void EqualFilter_ShouldReturnExactMatches();
    [Fact] public void InFilter_ShouldReturnUnionOfMatches();
    [Fact] public void RangeFilter_ShouldRespectBounds();
    [Fact] public void AndFilter_ShouldIntersect();
    [Fact] public void OrFilter_ShouldUnion();
    [Fact] public void NotFilter_ShouldComplement();
    [Fact] public void NestedComposite_ShouldEvaluateCorrectly();

    [Fact]
    public void FilteredSearch_ShouldOnlyReturnMatchingRecords()
    {
        // 向量查询 + 标量过滤
        // 验证结果集中每条记录都满足过滤条件
    }
}
```

### 3.6 大规模数据基线 (LargeScaleBaseline)

验证在较大数据量下系统的稳定性（不是性能测量，而是正确性保证）。

```csharp
public sealed class LargeScaleBaseline : IDisposable
{
    [Fact]
    public void Insert100K_ShouldNotThrowOrCorrupt()
    {
        // 1. 插入 100,000 条 128 维向量
        // 2. 期间每 10,000 条执行一次 Checkpoint
        // 3. 随机抽样 100 条 Get 验证数据完整
        // 4. 执行 100 次查询验证结果合理
        // 5. 验证进程内存 < 阈值（防止内存泄漏）
    }

    [Fact]
    public void HighDimensionVectors_ShouldWorkCorrectly()
    {
        // 1536 维（OpenAI embedding 维度），5000 条
        // 验证插入、查询、准确率均正常
    }
}
```

## 4. 性能基准测试设计

使用 **BenchmarkDotNet** 框架，所有基准类通过 `[MemoryDiagnoser]` 追踪内存分配。

### 4.1 插入吞吐量基准 (InsertBenchmark)

```csharp
[MemoryDiagnoser]
[SimpleJob(RunStrategy.Throughput, warmupCount: 3, iterationCount: 10)]
public class InsertBenchmark
{
    [Params(128, 768, 1536)]
    public int Dimensions { get; set; }

    [Params(1000, 10_000)]
    public int BatchSize { get; set; }

    private VectorLiteDB _db = null!;
    private ICollection _collection = null!;
    private VectorRecord[] _records = null!;

    [GlobalSetup]
    public void Setup()
    {
        // 创建临时数据库，生成确定性测试数据
    }

    /// <summary>测量单条插入的平均耗时</summary>
    [Benchmark(Baseline = true)]
    public async Task InsertSingle()
    {
        foreach (var record in _records)
            await _collection.InsertAsync(record);
    }

    [GlobalCleanup]
    public void Cleanup() { /* 删除临时文件 */ }
}
```

### 4.2 搜索延迟基准 (SearchBenchmark)

```csharp
[MemoryDiagnoser]
[SimpleJob(RunStrategy.Throughput, warmupCount: 3, iterationCount: 10)]
public class SearchBenchmark
{
    [Params(1_000, 10_000, 100_000)]
    public int DatasetSize { get; set; }

    [Params(128, 768)]
    public int Dimensions { get; set; }

    [Params(10, 50)]
    public int TopK { get; set; }

    private VectorLiteDB _db = null!;
    private ICollection _collection = null!;
    private ReadOnlyMemory<float>[] _queryVectors = null!;

    [GlobalSetup]
    public void Setup()
    {
        // 创建数据库，预插入 DatasetSize 条记录
        // 生成 100 个查询向量
    }

    /// <summary>纯向量搜索延迟</summary>
    [Benchmark(Baseline = true)]
    public async Task<IReadOnlyList<SearchResult>> SearchTopK()
    {
        return await _collection
            .Query(_queryVectors[0])
            .TopK(TopK)
            .ToListAsync();
    }

    /// <summary>测量不同 efSearch 对延迟的影响</summary>
    [Benchmark]
    [Arguments(50)]
    [Arguments(200)]
    [Arguments(500)]
    public async Task<IReadOnlyList<SearchResult>> SearchWithEf(int efSearch)
    {
        return await _collection
            .Query(_queryVectors[0])
            .WithEfSearch(efSearch)
            .TopK(TopK)
            .ToListAsync();
    }
}
```

### 4.3 混合查询基准 (HybridQueryBenchmark)

```csharp
[MemoryDiagnoser]
public class HybridQueryBenchmark
{
    [Params(10_000, 100_000)]
    public int DatasetSize { get; set; }

    /// <summary>过滤后剩余的数据比例</summary>
    [Params(0.01, 0.1, 0.5)]
    public double FilterSelectivity { get; set; }

    [Benchmark(Baseline = true)]
    public async Task<IReadOnlyList<SearchResult>> VectorOnlySearch()
    {
        // 无过滤，纯向量搜索
    }

    [Benchmark]
    public async Task<IReadOnlyList<SearchResult>> HybridSearch()
    {
        // 标量过滤 + 向量搜索
        // FilterSelectivity 控制过滤条件的选择性
    }
}
```

### 4.4 距离计算微基准 (DistanceBenchmark)

```csharp
[MemoryDiagnoser]
[DisassemblyDiagnoser(printSource: true)] // 输出反汇编验证SIMD指令
public class DistanceBenchmark
{
    [Params(128, 768, 1536, 4096)]
    public int Dimensions { get; set; }

    [Params(DistanceMetric.Cosine, DistanceMetric.Euclidean, DistanceMetric.DotProduct)]
    public DistanceMetric Metric { get; set; }

    private IDistanceFunction _distFunc = null!;
    private float[] _vectorA = null!;
    private float[] _vectorB = null!;

    [GlobalSetup]
    public void Setup()
    {
        _distFunc = DistanceFunctionFactory.Create(Metric);
        // 生成随机向量
    }

    /// <summary>单次距离计算耗时（纳秒级）</summary>
    [Benchmark]
    public float CalculateDistance()
        => _distFunc.Calculate(_vectorA, _vectorB);
}
```

### 4.5 检查点基准 (CheckpointBenchmark)

```csharp
[MemoryDiagnoser]
public class CheckpointBenchmark
{
    [Params(1_000, 10_000, 50_000)]
    public int DirtyRecordCount { get; set; }

    [Benchmark]
    public void Checkpoint()
    {
        // 插入 DirtyRecordCount 条记录（不检查点）
        // 测量单次 Checkpoint() 耗时
    }
}
```

### 4.6 内存占用基准 (MemoryBenchmark)

BenchmarkDotNet 的 `[MemoryDiagnoser]` 追踪 GC 分配。此基准额外通过 `GC.GetTotalMemory` 测量峰值驻留内存。

```csharp
public class MemoryBenchmark
{
    [Params(10_000, 50_000, 100_000)]
    public int RecordCount { get; set; }

    [Params(128, 768)]
    public int Dimensions { get; set; }

    [Benchmark]
    public long MeasurePeakMemory()
    {
        // 1. GC.Collect() 获取基线
        // 2. 创建数据库，插入 RecordCount 条记录
        // 3. GC.Collect()，测量驻留内存增量
        // 4. 返回增量值
    }
}
```

## 5. 阈值配置 (quality-gate.json)

所有质量门禁阈值集中定义在一个 JSON 配置文件中，CI 和本地均读取同一份配置：

```json
{
  "version": 1,
  "description": "Mugu.AI.VectorLite 质量门禁阈值",

  "functional": {
    "hnsw_recall": {
      "10k_128d_cosine_k10": 0.95,
      "10k_128d_cosine_k50": 0.90,
      "10k_128d_cosine_k100": 0.85,
      "10k_768d_cosine_k10": 0.95
    }
  },

  "performance": {
    "insert_ops_per_sec": {
      "128d_min": 50000,
      "768d_min": 20000,
      "1536d_min": 10000
    },
    "search_p95_ms": {
      "10k_128d_k10_max": 1.0,
      "10k_768d_k10_max": 5.0,
      "100k_128d_k10_max": 10.0,
      "100k_768d_k10_max": 50.0
    },
    "distance_ns_per_op": {
      "128d_cosine_max": 50,
      "768d_cosine_max": 200,
      "1536d_cosine_max": 500,
      "4096d_cosine_max": 1500
    },
    "checkpoint_ms": {
      "10k_records_max": 500,
      "50k_records_max": 2000
    },
    "memory_mb": {
      "10k_128d_max": 50,
      "100k_128d_max": 400,
      "50k_768d_max": 500
    }
  },

  "tolerance": {
    "description": "允许的性能波动百分比，超过此比例才判定为退化",
    "percent": 10
  }
}
```

### 5.1 配置模型

```csharp
namespace Mugu.AI.VectorLite.QualityGate.Infrastructure;

/// <summary>quality-gate.json 的强类型映射</summary>
internal sealed class QualityGateConfig
{
    public int Version { get; set; }
    public FunctionalThresholds Functional { get; set; } = new();
    public PerformanceThresholds Performance { get; set; } = new();
    public ToleranceConfig Tolerance { get; set; } = new();
}

internal sealed class FunctionalThresholds
{
    /// <summary>HNSW 召回率阈值：场景名 → 最低 Recall</summary>
    public Dictionary<string, double> HnswRecall { get; set; } = new();
}

internal sealed class PerformanceThresholds
{
    public Dictionary<string, double> InsertOpsPerSec { get; set; } = new();
    public Dictionary<string, double> SearchP95Ms { get; set; } = new();
    public Dictionary<string, double> DistanceNsPerOp { get; set; } = new();
    public Dictionary<string, double> CheckpointMs { get; set; } = new();
    public Dictionary<string, double> MemoryMb { get; set; } = new();
}

internal sealed class ToleranceConfig
{
    /// <summary>允许的性能波动百分比</summary>
    public int Percent { get; set; } = 10;
}
```

## 6. 阈值校验器 (ThresholdValidator)

```csharp
namespace Mugu.AI.VectorLite.QualityGate.Infrastructure;

/// <summary>
/// 将 BenchmarkDotNet 和 xUnit 的测试结果与 quality-gate.json 阈值对比。
/// </summary>
internal sealed class ThresholdValidator
{
    private readonly QualityGateConfig _config;

    internal ThresholdValidator(QualityGateConfig config);

    /// <summary>
    /// 校验性能基准结果。
    /// 返回 (通过/失败, 详情报告列表)。
    /// </summary>
    internal (bool Passed, IReadOnlyList<ValidationResult> Details) Validate(
        BenchmarkRunResult benchmarkResult);
}

internal sealed class ValidationResult
{
    /// <summary>指标名称（如 "search_p95_ms.10k_128d_k10"）</summary>
    public string MetricName { get; init; } = "";

    /// <summary>实测值</summary>
    public double ActualValue { get; init; }

    /// <summary>阈值</summary>
    public double ThresholdValue { get; init; }

    /// <summary>允许的容差（ThresholdValue × tolerance%）</summary>
    public double ToleranceValue { get; init; }

    /// <summary>是否通过</summary>
    public bool Passed { get; init; }

    /// <summary>判定说明</summary>
    public string Message { get; init; } = "";
}
```

### 6.1 判定逻辑

- **"越大越好"指标**（如 `insert_ops_per_sec`）：`actual >= threshold × (1 - tolerance%)` 则通过。
- **"越小越好"指标**（如 `search_p95_ms`、`memory_mb`）：`actual <= threshold × (1 + tolerance%)` 则通过。
- 配置中以 `_min` 结尾的 key 自动识别为"越大越好"，以 `_max` 结尾为"越小越好"。

## 7. 标准测试数据集生成器

```csharp
namespace Mugu.AI.VectorLite.QualityGate.Infrastructure;

/// <summary>
/// 生成确定性的标准测试数据集。
/// 使用固定随机种子，确保不同机器、不同次运行产生完全相同的数据。
/// </summary>
internal static class TestDataGenerator
{
    /// <summary>固定随机种子</summary>
    private const int Seed = 42;

    /// <summary>
    /// 生成随机单位向量数据集。
    /// 向量归一化为单位长度，确保余弦距离有意义。
    /// </summary>
    internal static float[][] GenerateVectors(int count, int dimensions)
    {
        var rng = new Random(Seed);
        // 对每个向量：生成随机高斯分量，然后 L2 归一化
    }

    /// <summary>生成带结构化元数据的完整记录集</summary>
    internal static VectorRecord[] GenerateRecords(
        int count, int dimensions, bool withMetadata = true)
    {
        // 元数据字段：
        //   category: 按 count/3 均分为 "A", "B", "C"
        //   score: 均匀分布 [0.0, 1.0]
        //   active: 80% true, 20% false
        //   source: "email" | "chat" | "doc" | "note"
    }

    /// <summary>
    /// 计算暴力搜索的 Ground Truth Top-K。
    /// 用于准确率基线中与 HNSW 结果对比。
    /// </summary>
    internal static ulong[][] ComputeGroundTruth(
        float[][] dataset, float[][] queries,
        int k, IDistanceFunction distFunc)
    {
        // 对每个查询向量，遍历全部数据集计算距离，取最近 K 个
    }
}
```

## 8. 结果导出与历史追踪

### 8.1 BenchmarkResultExporter

```csharp
internal static class BenchmarkResultExporter
{
    /// <summary>
    /// 将本次运行的基准结果导出为 JSON 文件。
    /// 文件名包含时间戳和 Git commit hash（如可获取）。
    /// 路径: artifacts/benchmarks/{timestamp}-{commit}.json
    /// </summary>
    internal static void ExportJson(BenchmarkRunResult result, string outputDir);

    /// <summary>
    /// 导出人类可读的 Markdown 报告。
    /// 包含：环境信息、各基准结果表格、阈值对比、通过/失败状态。
    /// </summary>
    internal static void ExportMarkdown(
        BenchmarkRunResult result,
        IReadOnlyList<ValidationResult> validation,
        string outputPath);
}
```

### 8.2 导出的 JSON 结构

```json
{
  "timestamp": "2026-04-09T05:00:00Z",
  "commit": "abc1234",
  "environment": {
    "os": "Windows 11",
    "cpu": "AMD Ryzen 9 7950X",
    "dotnet": "8.0.x",
    "simd": "AVX2"
  },
  "results": {
    "insert_ops_per_sec": { "128d": 65000, "768d": 28000, "1536d": 14000 },
    "search_p95_ms": { "10k_128d_k10": 0.6, "100k_768d_k10": 35.2 },
    "distance_ns_per_op": { "128d_cosine": 28, "768d_cosine": 145 },
    "memory_mb": { "10k_128d": 32, "100k_128d": 280 }
  },
  "validation": {
    "passed": true,
    "details": [ ]
  }
}
```

历史 JSON 文件可被外部脚本或 CI 收集，用于绘制性能趋势图（本项目不内置趋势图工具，仅保证数据可导出）。

## 9. CI 集成建议

```yaml
# .github/workflows/quality-gate.yml 概念示意
name: Quality Gate
on: [push, pull_request]

jobs:
  functional-baseline:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-dotnet@v4
        with: { dotnet-version: '8.0.x' }
      - run: >
          dotnet test tests/Mugu.AI.VectorLite.QualityGate/
          --configuration Release
          --logger "trx;LogFileName=baseline.trx"
      - uses: actions/upload-artifact@v4
        with: { name: baseline-results, path: '**/*.trx' }

  performance-benchmark:
    runs-on: ubuntu-latest
    if: github.event_name == 'push' && github.ref == 'refs/heads/main'
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-dotnet@v4
        with: { dotnet-version: '8.0.x' }
      - run: >
          dotnet run --project tests/Mugu.AI.VectorLite.QualityGate/
          --configuration Release
          -- --quality-gate
      - uses: actions/upload-artifact@v4
        with: { name: benchmark-results, path: 'artifacts/benchmarks/**' }
```

**策略**：
- **每次提交**：仅运行 `dotnet test`（功能基线），快速反馈（< 2分钟）。
- **合入 main**：运行完整 `--quality-gate`（功能 + 性能），确保主分支质量。
- **发布前**：在与目标部署环境一致的硬件上运行，获取可比较的性能数据。

## 10. 项目依赖

```xml
<!-- Mugu.AI.VectorLite.QualityGate.csproj 关键依赖 -->
<ItemGroup>
  <ProjectReference Include="..\..\src\Mugu.AI.VectorLite\Mugu.AI.VectorLite.csproj" />
</ItemGroup>
<ItemGroup>
  <PackageReference Include="BenchmarkDotNet" Version="0.14.*" />
  <PackageReference Include="xunit" Version="2.*" />
  <PackageReference Include="xunit.runner.visualstudio" Version="2.*" />
  <PackageReference Include="FluentAssertions" Version="7.*" />
  <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.*" />
</ItemGroup>
```
