# Mugu.AI.VectorLite 详细设计文档

## 文档索引

| 文档 | 说明 |
|------|------|
| [存储层详细设计](storage-layer.md) | 单文件容器二进制格式、页管理器、WAL、内存映射 |
| [核心引擎层详细设计](core-engine.md) | HNSW索引、标量索引、查询处理器、内存管理、SIMD距离计算 |
| [API层详细设计](api-layer.md) | Fluent API、Semantic Kernel集成、异常体系、并发模型 |
| [基准测试与质量门禁](quality-gate.md) | 功能基线、性能基准、阈值配置、CI集成 |

## 整体架构纵览

```text
应用层 (Application)
  │
  ▼
API层 ─── VectorLiteDB / Collection / IQueryBuilder / VectorLiteMemoryStore
  │
  ▼
核心引擎层 ─── QueryEngine / HNSWIndex / ScalarIndex / MemoryManager
  │
  ▼
存储层 ─── FileStorage / PageManager / WAL
  │
  ▼
单文件 (.vldb) + WAL日志 (.vldb-wal)
```

## 规划项目结构

```text
Mugu.AI.VectorLite/
├── src/
│   ├── Mugu.AI.VectorLite/                    # 主库 (class library, net8.0)
│   │   ├── Storage/                            # 存储层
│   │   │   ├── FileHeader.cs                   # 文件头定义与序列化
│   │   │   ├── Page.cs                         # 页面结构与页面类型枚举
│   │   │   ├── PageManager.cs                  # 页分配、读写、空闲链表
│   │   │   ├── Wal.cs                          # 预写日志实现
│   │   │   └── FileStorage.cs                  # mmap映射、文件生命周期
│   │   │
│   │   ├── Engine/                             # 核心引擎层
│   │   │   ├── Distance/                       # 向量距离计算
│   │   │   │   ├── IDistanceFunction.cs
│   │   │   │   ├── CosineDistance.cs
│   │   │   │   ├── EuclideanDistance.cs
│   │   │   │   └── DotProductDistance.cs
│   │   │   ├── HNSWIndex.cs                    # HNSW索引核心实现
│   │   │   ├── HNSWNode.cs                     # HNSW节点数据结构
│   │   │   ├── ScalarIndex.cs                  # 元数据倒排索引
│   │   │   ├── FilterExpression.cs             # 过滤表达式抽象语法树
│   │   │   ├── QueryEngine.cs                  # 查询协调器
│   │   │   └── MemoryManager.cs                # 对象池与内存预算
│   │   │
│   │   ├── API/                                # 对外API层
│   │   │   ├── VectorLiteDB.cs                 # 数据库入口
│   │   │   ├── Collection.cs                   # 集合操作
│   │   │   ├── QueryBuilder.cs                 # Fluent查询构建器
│   │   │   ├── VectorRecord.cs                 # 向量记录模型
│   │   │   ├── SearchResult.cs                 # 搜索结果模型
│   │   │   └── VectorLiteOptions.cs            # 配置项
│   │   │
│   │   └── Common/                             # 通用组件
│   │       ├── Exceptions/                     # 自定义异常层次
│   │       │   ├── VectorLiteException.cs
│   │       │   ├── StorageException.cs
│   │       │   ├── IndexException.cs
│   │       │   └── CollectionException.cs
│   │       └── Extensions/                     # 扩展方法
│   │
│   └── Mugu.AI.VectorLite.SemanticKernel/      # Semantic Kernel 适配 (独立项目)
│       ├── VectorLiteMemoryStore.cs
│       └── MemoryRecordMapper.cs               # MemoryRecord <-> VectorRecord 映射
│
├── tests/
│   ├── Mugu.AI.VectorLite.Tests/               # 单元测试 (xUnit)
│   ├── Mugu.AI.VectorLite.IntegrationTests/    # 集成测试
│   └── Mugu.AI.VectorLite.QualityGate/         # 基准测试与质量门禁 (xUnit + BenchmarkDotNet)
│
├── examples/
│   └── QuickStart/                             # 快速入门示例项目
│
└── docs/
    └── design/                                 # 设计文档 (本目录)
```

## 全局技术约束

| 约束项 | 值 |
|--------|-----|
| 目标框架 | `net8.0` |
| 语言版本 | C# 12 |
| 数据库文件扩展名 | `.vldb` |
| WAL文件扩展名 | `.vldb-wal` |
| 默认页大小 | 8192 字节 (8 KB) |
| 最大向量维度（默认） | 4096 |
| 字节序 | 小端序 (Little-Endian) |
| 字符编码 | UTF-8 |
| 日志框架 | `Microsoft.Extensions.Logging` |
| 测试框架 | xUnit + FluentAssertions |
| 基准测试框架 | BenchmarkDotNet |

## 命名空间映射

| 命名空间 | 对应目录 |
|-----------|----------|
| `Mugu.AI.VectorLite` | API/ (公开类型) |
| `Mugu.AI.VectorLite.Storage` | Storage/ (internal) |
| `Mugu.AI.VectorLite.Engine` | Engine/ (internal) |
| `Mugu.AI.VectorLite.Engine.Distance` | Engine/Distance/ (公开枚举 + internal实现) |
| `Mugu.AI.VectorLite.Common` | Common/ (internal) |
| `Mugu.AI.VectorLite.SemanticKernel` | SemanticKernel项目 (公开) |
