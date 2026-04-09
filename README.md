# Mugu.AI.VectorLite

> **单文件 · 零配置 · 高性能** — 为 .NET 平台打造的极致轻量嵌入式向量数据库。

[![.NET 8.0+](https://img.shields.io/badge/.NET-8.0%2B-512BD4?logo=dotnet)](https://dotnet.microsoft.com/)
[![C# 12](https://img.shields.io/badge/C%23-12-239120?logo=csharp)](https://learn.microsoft.com/dotnet/csharp/)
[![License](https://img.shields.io/badge/license-MIT-blue.svg)](#许可证)

---

## ✨ 特性

- **🗄️ 单文件存储** — 所有数据保存在一个 `.vldb` 文件中，无需外部数据库
- **⚡ SIMD 加速** — 向量距离计算自动适配 AVX-512 / AVX2 / Vector\<T\> 硬件指令集
- **🔍 混合查询** — 元数据过滤 + 向量语义搜索一站完成
- **🧠 HNSW 索引** — 分层可导航小世界图，实现近似最近邻的亚线性搜索
- **🔒 WAL 机制** — 预写日志保证数据持久性与崩溃恢复
- **🧩 Semantic Kernel 集成** — 开箱即用的 `IMemoryStore` 适配器
- **🪶 零依赖** — 核心库仅依赖 `Microsoft.Extensions.Logging.Abstractions` 和 `System.IO.Hashing`

## 🎯 目标场景

| 场景 | 说明 |
|------|------|
| 桌面端 RAG 应用 | 本地知识库检索增强生成 |
| 个人 AI 助手 | 对话记忆与上下文管理 |
| 游戏 NPC 记忆系统 | 角色长期记忆存储与语义回忆 |
| 边缘设备语义检索 | IoT / 嵌入式设备上的轻量向量搜索 |

---

## 🚀 快速开始

### 安装

```xml
<!-- 项目引用 -->
<ProjectReference Include="src\Mugu.AI.VectorLite\Mugu.AI.VectorLite.csproj" />

<!-- 或 NuGet（发布后可用） -->
<!-- <PackageReference Include="Mugu.AI.VectorLite" Version="x.y.z" /> -->
```

### 最小示例

```csharp
using Mugu.AI.VectorLite;

// 打开或创建数据库（单文件，零配置）
using var db = new VectorLiteDB("my_memory.vldb");

// 创建集合（名称 + 向量维度）
var notes = db.GetOrCreateCollection("notes", 1536);

// 插入一条记录
var id = await notes.InsertAsync(new VectorRecord
{
    Vector   = embedding,           // float[]，由你的 Embedding 模型生成
    Metadata = new() { ["tag"] = "工作", ["priority"] = 5L },
    Text     = "今天的会议纪要…",
});

// 语义搜索 Top-5
var results = await notes.Query(queryEmbedding)
    .TopK(5)
    .ToListAsync();

foreach (var r in results)
    Console.WriteLine($"[{r.Score:F4}] {r.Record.Text}");
```

### 混合查询（过滤 + 向量搜索）

```csharp
using Mugu.AI.VectorLite.Engine;

// 精确匹配 + 范围过滤（链式 .Where 自动 AND 组合）
var results = await notes.Query(queryEmbedding)
    .Where("tag", "工作")
    .Where(new RangeFilter("priority", lowerBound: 8L))
    .TopK(10)
    .WithMinScore(0.7f)
    .ToListAsync();
```

> 📖 更多示例请参见 [`examples/QuickStart/`](examples/QuickStart/)

---

## 🏗️ 架构概览

```
┌──────────────────────────────────────────────────┐
│  API 层 (public)                                 │
│  VectorLiteDB · Collection · QueryBuilder        │
├──────────────────────────────────────────────────┤
│  核心引擎层 (internal)                           │
│  HNSWIndex · ScalarIndex · QueryEngine           │
│  SIMD Distance (Cosine/Euclidean/DotProduct)     │
├──────────────────────────────────────────────────┤
│  存储层 (internal)                               │
│  FileStorage · PageManager (mmap) · WAL          │
└──────────────────────────────────────────────────┘
```

**数据流**：

- **写入**：`InsertAsync` → WAL 追加 → HNSW 索引更新 → 异步检查点合并到主文件
- **查询**：`Query().Where().TopK()` → 标量索引预过滤 → HNSW 向量搜索 → 按距离排序返回

---

## 🧩 Semantic Kernel 集成

```csharp
using Mugu.AI.VectorLite.SemanticKernel;

using var db = new VectorLiteDB("sk_memory.vldb");
var memoryStore = new VectorLiteMemoryStore(db);

var memory = new MemoryBuilder()
    .WithMemoryStore(memoryStore)
    .WithTextEmbeddingGeneration(embeddingService)
    .Build();

await memory.SaveInformationAsync("notes", "会议内容…", "meeting-001");
```

---

## ⚙️ 配置

```csharp
using var db = new VectorLiteDB("my.vldb", new VectorLiteOptions
{
    PageSize              = 8192,                        // 页大小（字节）
    MaxDimensions         = 4096,                        // 最大向量维度
    HnswM                 = 16,                          // HNSW 邻居数
    HnswEfConstruction    = 200,                         // 构建时候选集大小
    HnswEfSearch          = 50,                          // 搜索默认 efSearch
    DefaultDistanceMetric = DistanceMetric.Cosine,       // 距离度量
    CheckpointInterval    = TimeSpan.FromMinutes(5),     // 自动检查点间隔
    LoggerFactory         = loggerFactory,               // ILoggerFactory（可选）
});
```

| 参数 | 默认值 | 调优建议 |
|------|--------|----------|
| `HnswM` | 16 | 增大→召回率↑内存↑；通用场景 16 足够 |
| `HnswEfConstruction` | 200 | 100~300，一次构建多次查询用较大值 |
| `HnswEfSearch` | 50 | 精度要求高可设 100~200，可被查询级覆盖 |

---

## 🧪 构建与测试

```bash
# 构建
dotnet build

# 运行功能基线测试（14 项）
dotnet test

# 运行性能基准（BenchmarkDotNet，需 Release 模式）
cd tests/Mugu.AI.VectorLite.QualityGate
dotnet run -c Release

# 运行快速入门示例
cd examples/QuickStart
dotnet run
```

---

## 📁 项目结构

```
Mugu.AI.VectorLite/
├── src/
│   ├── Mugu.AI.VectorLite/              # 核心库
│   │   ├── API/                         # 公共 API（VectorLiteDB/Collection/QueryBuilder）
│   │   ├── Engine/                      # HNSW 索引 / 标量索引 / 查询引擎 / SIMD 距离
│   │   ├── Storage/                     # 文件存储 / 页管理 / WAL
│   │   └── Common/Exceptions/           # 异常层次
│   └── Mugu.AI.VectorLite.SemanticKernel/  # SK IMemoryStore 适配器
├── tests/
│   ├── Mugu.AI.VectorLite.Tests/        # 单元测试
│   └── Mugu.AI.VectorLite.QualityGate/  # 质量门禁（6 功能基线 + 4 性能基准）
├── examples/
│   └── QuickStart/                      # 快速入门示例
└── docs/
    ├── design/                          # 详细设计文档（5 篇）
    └── reference/                       # 开发参考手册（8 篇）
```

---

## 📚 文档

| 文档 | 说明 |
|------|------|
| [文档中心](docs/README.md) | 所有文档的总索引 |
| [快速入门](docs/reference/quick-start.md) | 5 分钟上手教程 |
| [API 参考](docs/reference/api-reference.md) | 公共 API 全量签名 |
| [过滤器指南](docs/reference/filter-guide.md) | 7 种过滤表达式详解 |
| [SK 集成](docs/reference/semantic-kernel.md) | Semantic Kernel 适配指南 |
| [内部架构](docs/reference/architecture.md) | 存储/索引/引擎实现细节 |
| [项目构建](docs/reference/project-structure.md) | 构建命令与依赖版本 |
| [质量门禁](docs/reference/quality-gate.md) | 基线测试与性能基准 |

---

## 📋 技术栈

| 组件 | 版本 |
|------|------|
| .NET | 8.0+ |
| C# | 12 |
| Microsoft.Extensions.Logging.Abstractions | 8.0.2 |
| System.IO.Hashing | 8.0.0 |
| Microsoft.SemanticKernel | 1.74.0（SK 集成包） |
| xUnit | 2.9.3（测试） |
| BenchmarkDotNet | 0.14.0（基准） |
| FluentAssertions | 6.12.2（断言） |

---

## 🤝 贡献

1. Fork 本仓库
2. 创建特性分支：`git checkout -b feature/my-feature`
3. 提交更改（使用简体中文提交信息）
4. 确保所有测试通过：`dotnet test`
5. 提交 Pull Request

### 编码规范

- 注释、文档、提交信息使用**简体中文**
- 文件编码 UTF-8，换行符 LF
- 方法 ≤ 30 行，类 ≤ 300 行，嵌套 ≤ 3 层
- 使用 `ILogger` 记录日志，禁止 `Console.WriteLine`

---

## 📄 许可证

[MIT License](LICENSE)

---

<p align="center">
  <sub>Made with ❤️ for the .NET AI ecosystem</sub>
</p>
