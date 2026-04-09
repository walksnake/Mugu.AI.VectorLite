# Mugu.AI.VectorLite 文档中心

## 设计文档

| 文档 | 说明 |
|------|------|
| [详细设计索引](design/index.md) | 总览：架构、项目结构、技术约束、命名空间 |
| [存储层详细设计](design/storage-layer.md) | 文件格式、页管理、WAL、mmap、崩溃恢复 |
| [核心引擎层详细设计](design/core-engine.md) | HNSW索引、标量索引、查询引擎、SIMD距离计算、内存管理 |
| [API层详细设计](design/api-layer.md) | Fluent API、Semantic Kernel集成、异常体系、并发模型 |
| [基准测试与质量门禁](design/quality-gate.md) | 功能基线、性能基准、阈值配置、CI集成 |

## 开发参考手册

| 文档 | 说明 |
|------|------|
| [参考手册索引](reference/index.md) | 总览：目录、约定、异常体系速查 |
| [快速入门](reference/quick-start.md) | 5 分钟上手：安装 → 插入 → 搜索 → 过滤 |
| [公共 API 参考](reference/api-reference.md) | VectorLiteDB / ICollection / IQueryBuilder / Models 全量签名 |
| [过滤器与混合查询](reference/filter-guide.md) | 7 种过滤表达式详解 + 组合策略 + 性能建议 |
| [Semantic Kernel 集成](reference/semantic-kernel.md) | VectorLiteMemoryStore 用法与映射规则 |
| [内部架构参考](reference/architecture.md) | 存储层 / HNSW / 标量索引 / SIMD / Code Agent 指南 |
| [项目结构与构建](reference/project-structure.md) | 解决方案布局、依赖版本、构建 / 测试命令 |
| [质量门禁参考](reference/quality-gate.md) | 功能基线 + 性能基准 + 阈值配置 + CI 集成 |
