using Mugu.AI.VectorLite.Storage;
using Microsoft.Extensions.Logging;

namespace Mugu.AI.VectorLite;

/// <summary>
/// VectorLiteDB：嵌入式向量数据库的主入口类。
/// 管理数据库文件、集合以及后台检查点定时器。
/// 启动时执行三阶段恢复：物理WAL重放 → 快照加载 → 逻辑WAL重放。
/// </summary>
public sealed class VectorLiteDB : IDisposable
{
    private readonly VectorLiteOptions _options;
    private readonly FileStorage _storage;
    private readonly ILogger? _logger;
    private readonly Dictionary<string, Collection> _collections = new();
    private readonly ReaderWriterLockSlim _rwLock = new();
    private readonly Timer? _checkpointTimer;
    private bool _disposed;

    /// <summary>数据库文件路径</summary>
    public string FilePath { get; }

    /// <summary>创建或打开数据库</summary>
    public VectorLiteDB(string filePath, VectorLiteOptions? options = null)
    {
        options ??= new VectorLiteOptions();
        options.Validate();

        _options = options;
        FilePath = filePath;
        _logger = options.LoggerFactory?.CreateLogger<VectorLiteDB>();

        // 阶段 1：创建或打开文件存储（内含物理 WAL 重放）
        _storage = File.Exists(filePath)
            ? FileStorage.Open(filePath, options.LoggerFactory)
            : FileStorage.CreateNew(filePath, options.PageSize, options.MaxDimensions, options.LoggerFactory);

        // 阶段 2：从快照加载集合
        LoadCollectionsFromStorage();

        // 阶段 3：重放逻辑 WAL 记录
        ReplayLogicalRecords();

        // 启动自动检查点定时器
        if (options.CheckpointInterval != Timeout.InfiniteTimeSpan)
        {
            _checkpointTimer = new Timer(
                _ => TryCheckpoint(),
                null,
                options.CheckpointInterval,
                options.CheckpointInterval);
        }

        _logger?.LogInformation("数据库已打开: {Path}", filePath);
    }

    /// <summary>获取或创建集合</summary>
    public ICollection GetOrCreateCollection(string name, int dimensions)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("集合名不能为空", nameof(name));
        if (dimensions < 1)
            throw new ArgumentOutOfRangeException(nameof(dimensions), "向量维度必须 >= 1");

        _rwLock.EnterWriteLock();
        try
        {
            EnsureNotDisposed();
            if (_collections.TryGetValue(name, out var existing))
            {
                if (existing.Dimensions != dimensions)
                    throw new CollectionException(
                        $"集合 '{name}' 已存在但维度不匹配: 期望 {existing.Dimensions}，请求 {dimensions}");
                return existing;
            }

            var collection = new Collection(name, dimensions, _options,
                _storage, _options.LoggerFactory?.CreateLogger<Collection>());
            _collections[name] = collection;

            _logger?.LogInformation("创建集合: {Name}, 维度={Dimensions}", name, dimensions);
            return collection;
        }
        finally
        {
            _rwLock.ExitWriteLock();
        }
    }

    /// <summary>获取已有集合（不存在时返回 null）</summary>
    public ICollection? GetCollection(string name)
    {
        _rwLock.EnterReadLock();
        try
        {
            EnsureNotDisposed();
            return _collections.GetValueOrDefault(name);
        }
        finally
        {
            _rwLock.ExitReadLock();
        }
    }

    /// <summary>获取所有集合名称</summary>
    public IReadOnlyList<string> GetCollectionNames()
    {
        _rwLock.EnterReadLock();
        try
        {
            EnsureNotDisposed();
            return _collections.Keys.ToList();
        }
        finally
        {
            _rwLock.ExitReadLock();
        }
    }

    /// <summary>删除集合（释放所有页链）</summary>
    public bool DeleteCollection(string name)
    {
        _rwLock.EnterWriteLock();
        try
        {
            EnsureNotDisposed();
            if (!_collections.TryGetValue(name, out var collection))
                return false;

            // 释放集合的所有页链
            FreeCollectionPages(collection);
            _collections.Remove(name);

            _logger?.LogInformation("删除集合: {Name}", name);
            return true;
        }
        finally
        {
            _rwLock.ExitWriteLock();
        }
    }

    /// <summary>检查集合是否存在</summary>
    public bool CollectionExists(string name)
    {
        _rwLock.EnterReadLock();
        try
        {
            EnsureNotDisposed();
            return _collections.ContainsKey(name);
        }
        finally
        {
            _rwLock.ExitReadLock();
        }
    }

    /// <summary>手动触发检查点</summary>
    public void Checkpoint()
    {
        _rwLock.EnterWriteLock();
        try
        {
            EnsureNotDisposed();
            FlushAndCheckpoint();
            _logger?.LogInformation("手动检查点已完成");
        }
        finally
        {
            _rwLock.ExitWriteLock();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _checkpointTimer?.Dispose();

        try { FlushAndCheckpoint(); }
        catch { /* 尽力而为 */ }

        _storage.Dispose();
        _rwLock.Dispose();

        _logger?.LogInformation("数据库已关闭: {Path}", FilePath);
    }

    /// <summary>从快照加载所有集合</summary>
    private void LoadCollectionsFromStorage()
    {
        var catalogRootPage = _storage.Header.CollectionRootPage;
        if (catalogRootPage == 0)
            return;

        var catalogData = PageChainIO.ReadChain(_storage, catalogRootPage);
        if (catalogData.Length == 0)
            return;

        var entries = CollectionCatalog.Deserialize(catalogData);
        foreach (var entry in entries)
        {
            var collection = Collection.LoadFromStorage(
                entry, _options, _storage,
                _options.LoggerFactory?.CreateLogger<Collection>());
            _collections[entry.Name] = collection;

            _logger?.LogDebug("从快照加载集合: {Name}, 维度={Dimensions}", entry.Name, entry.Dimensions);
        }

        _logger?.LogInformation("从快照加载了 {Count} 个集合", entries.Count);
    }

    /// <summary>重放逻辑 WAL 记录（阶段3恢复）</summary>
    private void ReplayLogicalRecords()
    {
        var logicalRecords = _storage.ReadLogicalRecords();
        if (logicalRecords.Count == 0)
            return;

        _logger?.LogInformation("开始逻辑 WAL 恢复: {Count} 条记录", logicalRecords.Count);

        foreach (var record in logicalRecords)
        {
            switch (record.OperationType)
            {
                case WalOperationType.RecordInsert:
                {
                    var (collectionName, vectorRecord) =
                        RecordSerializer.DeserializeInsert(record.Data);
                    if (_collections.TryGetValue(collectionName, out var collection))
                    {
                        collection.ReplayInsert(vectorRecord);
                    }
                    else
                    {
                        _logger?.LogWarning(
                            "逻辑恢复：集合 '{Name}' 不存在，跳过 RecordInsert",
                            collectionName);
                    }
                    break;
                }
                case WalOperationType.RecordDelete:
                {
                    var (collectionName, recordId) =
                        RecordSerializer.DeserializeDelete(record.Data);
                    if (_collections.TryGetValue(collectionName, out var collection))
                    {
                        collection.ReplayDelete(recordId);
                    }
                    else
                    {
                        _logger?.LogWarning(
                            "逻辑恢复：集合 '{Name}' 不存在，跳过 RecordDelete",
                            collectionName);
                    }
                    break;
                }
            }
        }

        _logger?.LogInformation("逻辑 WAL 恢复完成");
    }

    /// <summary>
    /// 刷入所有脏集合并执行检查点。
    /// 流程：集合快照 → 写目录 → 更新文件头 → WAL 检查点。
    /// </summary>
    private void FlushAndCheckpoint()
    {
        // 刷入所有脏集合到页
        foreach (var (_, collection) in _collections)
        {
            collection.FlushToStorage(_storage);
        }

        // 序列化并写入集合目录
        WriteCatalog();

        // 执行 WAL 检查点（截断 WAL）
        _storage.Checkpoint();
    }

    /// <summary>序列化集合目录并写入页链</summary>
    private void WriteCatalog()
    {
        var entries = new List<CollectionCatalogEntry>();
        foreach (var (_, collection) in _collections)
        {
            entries.Add(new CollectionCatalogEntry
            {
                Name = collection.Name,
                Dimensions = collection.Dimensions,
                DistanceMetric = _options.DefaultDistanceMetric,
                HnswM = _options.HnswM,
                HnswEfConstruction = _options.HnswEfConstruction,
                NextRecordId = collection.NextRecordId,
                HNSWRootPage = collection.HnswRootPage,
                ScalarIndexRootPage = collection.ScalarIndexRootPage,
                TextStoreRootPage = collection.TextStoreRootPage
            });
        }

        var catalogData = CollectionCatalog.Serialize(entries);

        _storage.WriteTransaction(ctx =>
        {
            // 释放旧目录页链
            var oldCatalogRoot = _storage.Header.CollectionRootPage;
            if (oldCatalogRoot != 0)
                PageChainIO.FreeChain(ctx, _storage, oldCatalogRoot);

            // 写入新目录页链
            var newCatalogRoot = PageChainIO.WriteChain(
                ctx, _storage, PageType.CollectionMeta, catalogData);
            _storage.Header.CollectionRootPage = newCatalogRoot;
        });
    }

    /// <summary>释放集合所占的所有页链</summary>
    private void FreeCollectionPages(Collection collection)
    {
        _storage.WriteTransaction(ctx =>
        {
            if (collection.HnswRootPage != 0)
                PageChainIO.FreeChain(ctx, _storage, collection.HnswRootPage);
            if (collection.ScalarIndexRootPage != 0)
                PageChainIO.FreeChain(ctx, _storage, collection.ScalarIndexRootPage);
            if (collection.TextStoreRootPage != 0)
                PageChainIO.FreeChain(ctx, _storage, collection.TextStoreRootPage);
        });
    }

    private void TryCheckpoint()
    {
        try
        {
            if (!_disposed)
                Checkpoint();
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "自动检查点失败");
        }
    }

    private void EnsureNotDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
