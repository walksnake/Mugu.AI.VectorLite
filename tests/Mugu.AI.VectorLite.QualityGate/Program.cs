using BenchmarkDotNet.Running;
using Mugu.AI.VectorLite.QualityGate.Benchmarks;

// BenchmarkDotNet 入口：运行所有性能基准
// 用法：
//   dotnet run -c Release                          -- 运行所有基准
//   dotnet run -c Release -- --filter "*Distance*"  -- 仅运行距离计算基准
//   dotnet test                                     -- 运行所有功能基线测试

if (args.Length == 0 || args.Contains("--benchmark"))
{
    BenchmarkSwitcher.FromAssembly(typeof(DistanceBenchmark).Assembly).Run(args);
}
else
{
    Console.WriteLine("用法:");
    Console.WriteLine("  dotnet run -c Release               运行所有性能基准");
    Console.WriteLine("  dotnet test                         运行所有功能基线测试");
}
