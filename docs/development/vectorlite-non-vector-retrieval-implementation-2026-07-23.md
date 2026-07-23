# VectorLite 非向量检索实施记录

> 日期：2026-07-23  
> 依据：`D:\Work\Mugu.AI\docs\design\vectorlite-non-vector-retrieval-investigation-2026-07-23.md`  
> 依据：`D:\Work\Mugu.AI\docs\development\vectorlite-non-vector-retrieval-implementation-plan-2026-07-23.md`

## 1. 验收分解

1. 纯标量查询 → 验证组合过滤、多字段排序、TopK、投影和取消。
2. 批量读取 → 验证单锁物化、重开读取和文本页批量复用。
3. 显式索引 → 验证有序范围、跨数值类型和定义持久化。
4. 查询规划 → 验证 AND 结果与书写顺序无关。
5. Scalar-only → 验证写入、删除、查询、恢复和模式拒绝。
6. 稳定分页 → 验证重复键下无重复遗漏及排序绑定。
7. 质量门禁 → 验证定向基准入口、基准项目编译和文档索引。

## 2. 完成状态

| 批次 | 状态 | 验证 |
|---|---|---|
| 纯标量查询 | 完成 | `ScalarQueryTests` |
| 批量物化与读取治理 | 完成 | 重开批量读取测试 |
| 有序与复合索引契约 | 完成 | 有序索引持久化测试 |
| 查询规划 | 完成 | 标量索引与过滤定向回归 |
| Scalar-only 集合 | 完成 | `ScalarOnlyCollectionTests` |
| 稳定游标分页 | 完成 | 分页定向测试 |
| 性能门禁与文档 | 完成 | QualityGate 编译及参数入口验证 |

## 3. 自查标准

- 不修改 `HNSWIndex.cs` 的用户工作。
- 不增加模糊或全文检索。
- 不引入外部数据库或网络依赖。
- 新增公开类型和成员使用中文 XML 注释。
- 仅运行新增及直接相关测试，不执行未经许可的全量测试。
