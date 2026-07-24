# NuGet 0.1.0 发布计划

> 日期：2026-07-24
> 状态：已完成
> 发布源：NuGet.org

## 1. 发布范围

- `Mugu.AI.VectorLite`
- `Mugu.AI.VectorLite.SemanticKernel`
- 两个包统一使用版本 `0.1.0`。
- API Key 仅从环境变量 `NUGET_CENTER_API_KEY` 读取，不写入文件或日志。

## 2. 执行步骤与验收

1. 更新版本和发布文档 → 验证 MSBuild 解析版本为 `0.1.0`。
2. 修复 HNSW 空图反序列化边界 → 验证异常层数被拒绝、合法哨兵值可加载。
3. 执行 Release 全量测试 → 验证全部测试通过且无跳过。
4. 使用现有发布脚本打包 → 验证两个 `.nupkg` 和符号包版本、依赖正确。
5. 提交并推送发布准备变更 → 验证本地与远程提交一致。
6. 推送 NuGet.org → 验证两个包均发布成功并可从官方源查询。

## 3. 发布边界

- 不跳过测试。
- 不覆盖已存在的包版本。
- 不在控制台输出 API Key。
- 不创建未经要求的 Git 标签或 GitHub Release。

## 4. 发布结果

- 发布脚本：`scripts/publish-nuget.ps1 -Version 0.1.0 -SkipConfirm`
- 构建：Release，0 警告、0 错误。
- 测试：291/291 通过，0 失败、0 跳过。
- NuGet 推送：2 个包成功、0 个失败；两个符号包同步推送成功。
- `Mugu.AI.VectorLite.0.1.0.nupkg`
  - SHA256：`9DDD6D809B41017219DC939E28B7951EFC031283F7897AA816D41B1B6A78A919`
- `Mugu.AI.VectorLite.SemanticKernel.0.1.0.nupkg`
  - SHA256：`C1D7AC1D005EB5AAB54213A8495943F0673D8D8C842E101843FE379CAD91DBFB`
- NuGet.org 已接受上传；核心包与 Semantic Kernel 包的 `0.1.0`
  均已在官方索引可见。
