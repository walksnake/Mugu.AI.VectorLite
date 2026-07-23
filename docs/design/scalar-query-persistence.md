# 纯标量检索持久化补充

> 父文档：[详细设计索引](index.md)

## 1. 文档拆分说明

既有 `persistence.md` 已超过 800 行，因此本次新增持久化设计独立成文。

## 2. 集合目录兼容

集合目录格式升级为 v2，并在条目末尾增加 `CollectionMode`：

- `Vector`：保持旧集合语义，读取、写入和持久化 HNSW。
- `ScalarOnly`：不写入 HNSW 页链，`HNSWRootPage` 固定为 0。

读取 v1 目录时默认使用 `Vector`，保证旧数据库兼容。

## 3. 标量索引兼容

标量索引格式升级为 v2，在原有记录元数据之后追加显式索引定义。读取 v1 时
定义列表为空；读取 v2 后先恢复元数据，再按定义重建内存有序索引。

## 4. 文本读取

`PageManager.ReadPageRange` 直接把 mmap 中目标范围复制到调用方缓冲区。
`PageChainIO.ReadAt` 按跨页片段调用该接口，不再租用整页缓冲，也不在
`PageManager` 内创建同尺寸临时数组。
