using BenchmarkDotNet.Running;
using Mugu.AI.VectorLite.QualityGate.Benchmarks;

// BenchmarkDotNet 入口：运行所有性能基准
// 用法：
//   dotnet run -c Release                          -- 运行所有基准
//   dotnet run -c Release -- --filter "*Distance*"  -- 仅运行距离计算基准
//   dotnet test                                     -- 运行所有功能基线测试

var benchmarkArgs = args
    .Where(argument => !string.Equals(
        argument,
        "--benchmark",
        StringComparison.OrdinalIgnoreCase))
    .ToArray();
BenchmarkSwitcher.FromAssembly(typeof(DistanceBenchmark).Assembly).Run(benchmarkArgs);
