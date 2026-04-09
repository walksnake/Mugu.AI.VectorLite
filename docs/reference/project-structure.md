# 项目结构与构建

> 本章描述解决方案布局、依赖关系、构建与测试命令。

---

## 1. 解决方案布局

```
Mugu.AI.VectorLite/
├── Mugu.AI.VectorLite.slnx              ← 解决方案文件（.slnx 格式）
├── Directory.Build.props                 ← 全局构建属性
│
├── src/
│   ├── Mugu.AI.VectorLite/              ← 主库（核心向量数据库）
│   │   ├── Mugu.AI.VectorLite.csproj
│   │   ├── Common/Exceptions/           ← 异常层次
│   │   ├── Storage/                     ← 存储层（FileStorage/PageManager/Wal）
│   │   ├── Engine/                      ← 核心引擎（HNSWIndex/ScalarIndex/QueryEngine）
│   │   │   └── Distance/               ← SIMD 距离计算
│   │   └── API/                         ← 公共 API（VectorLiteDB/Collection/QueryBuilder）
│   │
│   └── Mugu.AI.VectorLite.SemanticKernel/  ← Semantic Kernel 适配器
│       ├── Mugu.AI.VectorLite.SemanticKernel.csproj
│       ├── VectorLiteMemoryStore.cs
│       └── MemoryRecordMapper.cs
│
├── tests/
│   ├── Mugu.AI.VectorLite.Tests/        ← 单元测试（xUnit，预留）
│   │   └── Mugu.AI.VectorLite.Tests.csproj
│   │
│   └── Mugu.AI.VectorLite.QualityGate/  ← 质量门禁（功能基线 + 性能基准）
│       ├── Mugu.AI.VectorLite.QualityGate.csproj
│       ├── Program.cs                   ← BenchmarkDotNet 入口
│       ├── quality-gate.json            ← 阈值配置
│       ├── Infrastructure/              ← TestDataGenerator / Config / Validator
│       ├── Baselines/                   ← 6 个功能基线测试类
│       └── Benchmarks/                  ← 4 个性能基准类
│
├── examples/
│   └── QuickStart/                      ← 快速入门示例
│       ├── QuickStart.csproj
│       ├── Program.cs
│       └── README.md
│
└── docs/
    ├── README.md                        ← 文档中心索引
    ├── design/                          ← 设计文档（5 篇）
    └── reference/                       ← 开发参考手册（本目录）
```

---

## 2. 全局构建属性

`Directory.Build.props`：

```xml
<Project>
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <LangVersion>12</LangVersion>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
  </PropertyGroup>
</Project>
```

> 所有项目统一使用 .NET 8.0 / C# 12 / Nullable / WarningsAsErrors。

---

## 3. 项目依赖关系

```
QuickStart
  └→ Mugu.AI.VectorLite

Mugu.AI.VectorLite.SemanticKernel
  ├→ Mugu.AI.VectorLite
  └→ Microsoft.SemanticKernel.Abstractions  1.74.0
     Microsoft.SemanticKernel.Core          1.74.0

Mugu.AI.VectorLite
  ├→ Microsoft.Extensions.Logging.Abstractions  8.0.2
  └→ System.IO.Hashing                         8.0.0

Mugu.AI.VectorLite.Tests
  └→ Mugu.AI.VectorLite
     xunit / xunit.runner.visualstudio / Microsoft.NET.Test.Sdk
     FluentAssertions / coverlet.collector

Mugu.AI.VectorLite.QualityGate
  └→ Mugu.AI.VectorLite
     （同上测试依赖 + BenchmarkDotNet 0.14.0 + System.Text.Json）
```

### InternalsVisibleTo 配置

主库 `Mugu.AI.VectorLite.csproj` 声明：

```xml
<ItemGroup>
  <InternalsVisibleTo Include="Mugu.AI.VectorLite.Tests" />
  <InternalsVisibleTo Include="Mugu.AI.VectorLite.QualityGate" />
  <InternalsVisibleTo Include="Mugu.AI.VectorLite.SemanticKernel" />
</ItemGroup>
```

---

## 4. NuGet 依赖版本

| 包 | 版本 | 用途 |
|----|------|------|
| Microsoft.Extensions.Logging.Abstractions | 8.0.2 | 日志接口 |
| System.IO.Hashing | 8.0.0 | CRC32C 校验 |
| Microsoft.SemanticKernel.Abstractions | 1.74.0 | SK 接口定义 |
| Microsoft.SemanticKernel.Core | 1.74.0 | SK 核心（含 IMemoryStore） |
| xunit | 2.9.3 | 测试框架 |
| xunit.runner.visualstudio | 3.1.4 | 测试运行器 |
| Microsoft.NET.Test.Sdk | 17.14.1 | 测试宿主 |
| FluentAssertions | 6.12.2 | 断言库 |
| BenchmarkDotNet | 0.14.0 | 性能基准 |
| coverlet.collector | 6.0.4 | 覆盖率收集 |
| System.Text.Json | 8.0.5 | 阈值配置解析 |

---

## 5. 构建与测试命令

### 构建

```bash
# 构建整个解决方案
dotnet build

# 仅构建主库
dotnet build src/Mugu.AI.VectorLite/Mugu.AI.VectorLite.csproj

# Release 构建
dotnet build -c Release
```

### 运行功能测试

```bash
# 运行所有功能基线测试（14 项）
dotnet test

# 运行指定测试类
dotnet test --filter "FullyQualifiedName~HNSWAccuracyBaseline"

# 查看详细输出
dotnet test --verbosity normal
```

### 运行性能基准

```bash
# 运行所有基准（需要 Release 模式）
cd tests/Mugu.AI.VectorLite.QualityGate
dotnet run -c Release

# 运行指定基准
dotnet run -c Release -- --filter "*DistanceBenchmark*"

# 导出结果
dotnet run -c Release -- --exporters json csv
```

### 运行示例

```bash
cd examples/QuickStart
dotnet run
```

---

## 6. 特殊构建配置说明

| 项目 | 特殊配置 | 原因 |
|------|----------|------|
| Mugu.AI.VectorLite | `AllowUnsafeBlocks=true` | SIMD 距离计算使用 unsafe 指针运算 |
| SemanticKernel | `<NoWarn>SKEXP0001</NoWarn>` | IMemoryStore 是 SK 实验性 API |
| QualityGate | `<GenerateProgramFile>false</GenerateProgramFile>` | 同时包含 Test SDK 和手动 Program.cs（BenchmarkDotNet 入口） |
| QualityGate | `OutputType=Exe` | 用于直接运行 BenchmarkDotNet |

---

## 7. 代码规范速查

| 规范 | 要求 |
|------|------|
| 语言 | 注释、文档、提交信息用简体中文 |
| 编码 | UTF-8 / LF 换行 |
| 方法长度 | ≤ 30 行 |
| 类长度 | ≤ 300 行（超过时拆分） |
| 嵌套深度 | ≤ 3 层 |
| 日志 | 使用 `ILogger`，不用 `Console.WriteLine` |
| 可见性 | 引擎/存储 `internal`，API `public` |
| 命名 | PascalCase（类/方法），camelCase（局部变量），_camelCase（私有字段） |
