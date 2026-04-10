# 变更日志

本项目的所有重要变更将记录在此文件中。

格式基于 [Keep a Changelog](https://keepachangelog.com/zh-CN/1.1.0/)，
版本号遵循 [语义化版本](https://semver.org/lang/zh-CN/)。

## [0.0.2] - 2025-01-15

### 新增
- 完整的持久化系统（逻辑 WAL + 检查点快照 + 三阶段恢复）
- 文本存储懒加载机制（TextStore 索引启动加载，文本按需读取）
- 线程安全重构（Collection 使用 ReaderWriterLockSlim）
- 反序列化边界校验（HNSW / RecordSerializer / Wal / TextStore）
- 检查点原子性保护（PageManager.GrowFile 异常恢复）
- 单元测试套件（31 个测试覆盖核心功能）
- CI/CD 流水线（GitHub Actions 多平台构建 + NuGet 发布）
- QuickStart 示例项目
- 开发参考手册（docs/reference-manual.md）
- NuGet 打包发布脚本

### 修复
- LICENSE.txt 占位符替换为实际版权信息

## [0.0.1] - 2025-01-10

### 新增
- HNSW 索引实现（分层可导航小世界图）
- 三级 SIMD 加速（AVX-512 / AVX2 / Vector\<T\>）
- 三种距离度量（余弦 / 欧几里得 / 点积）
- 标量索引（元数据倒排索引）
- 混合查询引擎（标量过滤 + 向量搜索）
- Fluent API（链式查询构建器）
- 单文件存储引擎（自定义二进制格式 + mmap）
- Semantic Kernel 集成（IMemoryStore 适配器）
- 丰富的过滤表达式（Equal / NotEqual / In / Range / And / Or / Not）
