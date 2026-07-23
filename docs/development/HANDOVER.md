# 工作交接

## 本轮完成内容

- 新增绕开 HNSW 的纯标量查询、组合过滤、多字段稳定排序、TopK、投影和游标分页。
- 新增单锁批量读取；查询和批量读取只物化最终结果字段。
- 新增显式索引定义、有序范围索引及向后兼容持久化。
- AND 条件按 Posting 基数估算重排；等值 ID API 消除中间 Posting 副本。
- 新增 Scalar-only 集合、标量写入、删除、恢复和模式错误边界。
- 页链随机读取改为 mmap 范围直读；批量文本读取按页复用缓冲。
- 修复定向基准参数入口，新增纯标量查询与物化基准，并更新中文文档。

## 本轮遗留问题和工作

- 未执行解决方案全量测试，遵循“无明确要求禁止全量测试”的约束。
- BenchmarkDotNet Dry 烟测在 120 秒上限内未完成并已终止；新增基准已通过编译
  和列表发现，但未形成可采信的性能数值。
- 复合索引持久化契约已建立，查询结果正确；执行器尚未用复合索引直接遍历，
  当前使用有界 TopK 堆。
- 既有 `docs/design/persistence.md` 已有 1299 行；新内容已拆到独立文档。

## 下一轮计划

1. 经用户许可执行项目级或解决方案级全量测试。
2. 在固定 Release 环境运行新增短基准并记录相对性能与分配结果。
3. 如复合索引成为热点，增加左侧等值前缀到排序游标的直接执行路径。
4. 单独安排既有超长设计文档拆分。

## 参考文档

- `docs/development/vectorlite-non-vector-retrieval-implementation-2026-07-23.md`
- `docs/design/scalar-query-persistence.md`
- `docs/reference/api-reference.md`
- `docs/reference/filter-guide.md`
- `docs/reference/quality-gate.md`

## 基线进度

- Release 定向测试：73 个通过，0 个失败、0 个跳过。
- QualityGate：Release 编译通过，0 警告、0 错误；4 个新增标量基准可被发现。
- 2026-07-23 全量测试：共 290 项，289 个通过、1 个失败、0 个跳过。
  - `Mugu.AI.VectorLite.Tests`：272/273 通过。
  - `Mugu.AI.VectorLite.QualityGate`：17/17 通过。
  - 唯一失败为 `HNSWDeserialize_ExcessiveMaxLayer_ShouldThrow`。当前未提交的
    `HNSWIndex.cs` 在空图分支中不再校验 `MaxLayer` 上限，使测试构造的
    `MaxLayer = 999、nodeCount = 0` 未抛出 `StorageException`。
- Git：保留用户原有 `src/Mugu.AI.VectorLite/Engine/HNSWIndex.cs` 修改；未创建提交。
