# Mugu.AI.VectorLite 开发参考手册

> 版本 1.0 · 目标读者：应用开发者 / Code Agent
>
> 本手册是对 VectorLite API、架构、项目结构的权威参考。
> 设计文档请见 [`docs/design/`](../design/index.md)。

---

## 目录

| 章节 | 文件 | 说明 |
|------|------|------|
| 1 | [快速入门](quick-start.md) | 5 分钟上手：安装 → 插入 → 搜索 → 过滤 |
| 2 | [公共 API 参考](api-reference.md) | VectorLiteDB / ICollection / IQueryBuilder / Models 全量签名 |
| 3 | [过滤器与混合查询](filter-guide.md) | 7 种过滤表达式详解 + 组合策略 + 性能建议 |
| 4 | [Semantic Kernel 集成](semantic-kernel.md) | VectorLiteMemoryStore 用法、映射规则、注意事项 |
| 5 | [内部架构参考](architecture.md) | 存储层 / HNSW / 标量索引 / 查询引擎 / SIMD（供 Code Agent 使用） |
| 6 | [项目结构与构建](project-structure.md) | 解决方案布局、依赖版本、构建 / 测试 / 基准命令 |
| 7 | [质量门禁参考](quality-gate.md) | 功能基线 + 性能基准 + 阈值配置 |

---

## 约定说明

| 约定 | 规则 |
|------|------|
| 语言 | 注释、文档、提交信息使用**简体中文** |
| 编码 | UTF-8 / LF 换行 |
| 框架 | .NET 8.0+, C# 12 |
| 日志 | 使用 `ILogger`，禁止 `Console.WriteLine` |
| 可见性 | 引擎/存储层类型为 `internal`；仅 API 层类型为 `public` |
| 距离语义 | 所有距离函数统一为"越小越相似"，`Score = 1 - Distance` |

---

## 异常体系速查

```
VectorLiteException                  ← 所有异常的基类
├── StorageException                 ← 存储层错误
│   ├── CorruptedFileException       ← 数据库文件损坏
│   ├── WalCorruptedException        ← WAL 日志损坏
│   └── PageException                ← 页分配/读写错误
├── IndexException                   ← 索引层错误
│   ├── DimensionMismatchException   ← 向量维度不匹配（含 Expected / Actual）
│   └── IndexFullException           ← 索引已满
└── CollectionException              ← 集合层错误
    ├── CollectionNotFoundException  ← 集合不存在（含 CollectionName）
    └── CollectionAlreadyExistsException ← 集合已存在（含 CollectionName）
```

所有异常均提供 `()`, `(string)`, `(string, Exception)` 三种构造形式。
`DimensionMismatchException` 额外提供 `(int expected, int actual)` 构造，
自动生成消息 `"向量维度不匹配：期望 {expected}，实际 {actual}"`。
