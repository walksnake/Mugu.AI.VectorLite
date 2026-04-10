using Mugu.AI.VectorLite.Engine;
using Mugu.AI.VectorLite.Engine.Distance;
using Mugu.AI.VectorLite.Storage;
using Microsoft.Extensions.Logging;

namespace Mugu.AI.VectorLite;

/// <summary>
/// 集合实现：管理一个命名集合中的所有向量记录。
/// 内部持有 HNSW 索引、标量索引和文本存储。
/// 向量数据常驻内存（HNSW 节点），文本按需懒加载。
/// </summary>
internal sealed class Collection : ICollection
{
    private HNSWIndex _hnswIndex;
    private ScalarIndex _scalarIndex;
    private QueryEngine _queryEngine;
    private TextStore _textStore;
    private readonly IDistanceFunction _distFunc;
    private readonly int _efSearch;
    private readonly VectorLiteOptions _options;
    private readonly ILogger? _logger;
    private readonly ReaderWriterLockSlim _rwLock = new();
    private FileStorage? _storage;

    private ulong _nextRecordId = 1;

    // 页链根页（检查点时设置）
    internal ulong HnswRootPage { get; set; }
    internal ulong ScalarIndexRootPage { get; set; }
    internal ulong TextStoreRootPage { get; set; }

    // 脏标记：有新操作待检查点
    internal bool IsDirty { get; private set; }

    public string Name { get; }
    public int Dimensions { get; }
    public int Count => _hnswIndex.Count;

    internal ulong NextRecordId => _nextRecordId;

    /// <summary>创建新集合</summary>
    internal Collection(string name, int dimensions, VectorLiteOptions options,
        FileStorage? storage = null, ILogger? logger = null)
    {
        Name = name;
        Dimensions = dimensions;
        _options = options;
        _efSearch = options.HnswEfSearch;
        _storage = storage;
        _logger = logger;

        _distFunc = DistanceFunctionFactory.Get(options.DefaultDistanceMetric);
        _hnswIndex = new HNSWIndex(_distFunc, options.HnswM, options.HnswEfConstruction);
        _scalarIndex = new ScalarIndex();
        _textStore = new TextStore();
        _queryEngine = new QueryEngine(_hnswIndex, _scalarIndex, _distFunc);
    }

    /// <summary>从快照加载集合（私有构造）</summary>
    private Collection(
        string name, int dimensions, VectorLiteOptions options, FileStorage storage,
        HNSWIndex hnswIndex, ScalarIndex scalarIndex, TextStore textStore,
        ulong nextRecordId, ulong hnswRootPage, ulong scalarIndexRootPage, ulong textStoreRootPage,
        ILogger? logger)
    {
        Name = name;
        Dimensions = dimensions;
        _options = options;
        _efSearch = options.HnswEfSearch;
        _storage = storage;
        _logger = logger;

        _distFunc = DistanceFunctionFactory.Get(options.DefaultDistanceMetric);
        _hnswIndex = hnswIndex;
        _scalarIndex = scalarIndex;
        _textStore = textStore;
        _queryEngine = new QueryEngine(_hnswIndex, _scalarIndex, _distFunc);
        _nextRecordId = nextRecordId;
        HnswRootPage = hnswRootPage;
        ScalarIndexRootPage = scalarIndexRootPage;
        TextStoreRootPage = textStoreRootPage;
    }

    /// <summary>设置存储引用（延迟绑定）</summary>
    internal void BindStorage(FileStorage storage) => _storage = storage;

    /// <summary>从检查点快照加载集合</summary>
    internal static Collection LoadFromStorage(
        CollectionCatalogEntry entry,
        VectorLiteOptions options,
        FileStorage storage,
        ILogger? logger = null)
    {
        var distFunc = DistanceFunctionFactory.Get(entry.DistanceMetric);

        // 加载 HNSW 索引
        HNSWIndex hnswIndex;
        if (entry.HNSWRootPage != 0)
        {
            var hnswData = PageChainIO.ReadChain(storage, entry.HNSWRootPage);
            hnswIndex = HNSWIndex.Deserialize(hnswData, distFunc);
        }
        else
        {
            hnswIndex = new HNSWIndex(distFunc, entry.HnswM, entry.HnswEfConstruction);
        }

        // 加载标量索引
        ScalarIndex scalarIndex;
        if (entry.ScalarIndexRootPage != 0)
        {
            var scalarData = PageChainIO.ReadChain(storage, entry.ScalarIndexRootPage);
            scalarIndex = ScalarIndexSerializer.Deserialize(scalarData);
        }
        else
        {
            scalarIndex = new ScalarIndex();
        }

        // 加载文本存储索引（仅索引，不加载文本内容）
        var textStore = entry.TextStoreRootPage != 0
            ? TextStore.LoadIndex(storage, entry.TextStoreRootPage)
            : new TextStore();

        return new Collection(
            entry.Name, entry.Dimensions, options, storage,
            hnswIndex, scalarIndex, textStore,
            entry.NextRecordId, entry.HNSWRootPage, entry.ScalarIndexRootPage, entry.TextStoreRootPage,
            logger);
    }

    public Task<ulong> InsertAsync(VectorRecord record, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        if (record.Vector.Length != Dimensions)
            throw new DimensionMismatchException(Dimensions, record.Vector.Length);

        _rwLock.EnterWriteLock();
        try
        {
            return Task.FromResult(InsertCore(record));
        }
        finally
        {
            _rwLock.ExitWriteLock();
        }
    }

    /// <summary>插入核心逻辑（调用者必须持有写锁）</summary>
    private ulong InsertCore(VectorRecord record)
    {
        var id = _nextRecordId++;
        record.Id = id;

        // 先写逻辑 WAL（确保零数据丢失）
        if (_storage != null)
        {
            var walData = RecordSerializer.SerializeInsert(Name, record);
            _storage.LogLogicalOperation(WalOperationType.RecordInsert, walData);
        }

        // 再更新内存（若部分失败则回滚已更新的部分）
        var hnswInserted = false;
        var scalarAdded = false;
        try
        {
            _hnswIndex.Insert(id, record.Vector);
            hnswInserted = true;

            _scalarIndex.Add(id, record.Metadata);
            scalarAdded = true;

            _textStore.SetPending(id, record.Text);
            IsDirty = true;
        }
        catch
        {
            // 回滚已完成的内存操作
            if (scalarAdded)
                _scalarIndex.Remove(id);
            if (hnswInserted)
                _hnswIndex.MarkDeleted(id);
            throw;
        }

        _logger?.LogDebug("集合 '{Name}' 插入记录 {Id}", Name, id);
        return id;
    }

    public Task<IReadOnlyList<ulong>> InsertBatchAsync(IEnumerable<VectorRecord> records,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var recordList = records.ToList();
        foreach (var record in recordList)
        {
            if (record.Vector.Length != Dimensions)
                throw new DimensionMismatchException(Dimensions, record.Vector.Length);
        }

        _rwLock.EnterWriteLock();
        try
        {
            var ids = new List<ulong>(recordList.Count);
            foreach (var record in recordList)
            {
                ct.ThrowIfCancellationRequested();
                ids.Add(InsertCore(record));
            }
            return Task.FromResult<IReadOnlyList<ulong>>(ids);
        }
        finally
        {
            _rwLock.ExitWriteLock();
        }
    }

    public Task<VectorRecord?> GetAsync(ulong id, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        _rwLock.EnterReadLock();
        try
        {
            var record = AssembleRecord(id);
            return Task.FromResult(record);
        }
        finally
        {
            _rwLock.ExitReadLock();
        }
    }

    public Task<bool> DeleteAsync(ulong id, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        _rwLock.EnterWriteLock();
        try
        {
            return Task.FromResult(DeleteCore(id));
        }
        finally
        {
            _rwLock.ExitWriteLock();
        }
    }

    public Task<int> DeleteBatchAsync(IEnumerable<ulong> ids, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        _rwLock.EnterWriteLock();
        try
        {
            var count = 0;
            foreach (var id in ids)
            {
                ct.ThrowIfCancellationRequested();
                if (DeleteCore(id))
                    count++;
            }
            return Task.FromResult(count);
        }
        finally
        {
            _rwLock.ExitWriteLock();
        }
    }

    /// <summary>删除核心逻辑（调用者必须持有写锁）</summary>
    private bool DeleteCore(ulong id)
    {
        if (!_hnswIndex.ContainsActiveNode(id))
            return false;

        // 先写逻辑 WAL
        if (_storage != null)
        {
            var walData = RecordSerializer.SerializeDelete(Name, id);
            _storage.LogLogicalOperation(WalOperationType.RecordDelete, walData);
        }

        _hnswIndex.MarkDeleted(id);
        _scalarIndex.Remove(id);
        _textStore.Remove(id);
        IsDirty = true;
        return true;
    }

    public Task<IReadOnlyList<ulong>> FindIdsByMetadataAsync(string field, object value,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        _rwLock.EnterReadLock();
        try
        {
            var filter = new EqualFilter(field, value);
            var ids = _scalarIndex.Filter(filter);
            return Task.FromResult<IReadOnlyList<ulong>>(ids.ToList());
        }
        finally
        {
            _rwLock.ExitReadLock();
        }
    }

    public Task<ulong> UpsertAsync(VectorRecord record, string keyField,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        if (record.Vector.Length != Dimensions)
            throw new DimensionMismatchException(Dimensions, record.Vector.Length);

        if (record.Metadata == null || !record.Metadata.TryGetValue(keyField, out var keyValue))
            throw new ArgumentException($"记录的 Metadata 中不包含键 '{keyField}'", nameof(keyField));

        // 整个 Upsert 在写锁下原子执行：先插入新记录，成功后删除旧记录
        _rwLock.EnterWriteLock();
        try
        {
            var filter = new EqualFilter(keyField, keyValue);
            var existingIds = _scalarIndex.Filter(filter).ToList();

            // 先插入新记录（WAL 先记录 Insert）
            var newId = InsertCore(record);

            // 再删除旧记录（WAL 再记录 Delete），即使此处失败新记录已持久化
            foreach (var existingId in existingIds)
                DeleteCore(existingId);

            return Task.FromResult(newId);
        }
        finally
        {
            _rwLock.ExitWriteLock();
        }
    }

    public IQueryBuilder Query(float[] queryVector)
    {
        if (queryVector.Length != Dimensions)
            throw new DimensionMismatchException(Dimensions, queryVector.Length);

        return new QueryBuilder(this, queryVector);
    }

    /// <summary>由 QueryBuilder 调用，执行实际的混合查询</summary>
    internal IReadOnlyList<SearchResult> ExecuteQuery(
        float[] queryVector, int topK, int? efSearch,
        FilterExpression? filter, float minScore)
    {
        _rwLock.EnterReadLock();
        try
        {
            var ef = efSearch ?? _efSearch;
            var rawResults = _queryEngine.Search(queryVector, topK, ef, filter);

            var results = new List<SearchResult>();
            foreach (var (recordId, distance) in rawResults)
            {
                var record = AssembleRecord(recordId);
                if (record == null)
                    continue;

                var result = new SearchResult
                {
                    Record = record,
                    Distance = distance
                };

                if (minScore > 0f && result.Score < minScore)
                    continue;

                results.Add(result);
            }

            return results;
        }
        finally
        {
            _rwLock.ExitReadLock();
        }
    }

    /// <summary>
    /// 按需组装记录：从 HNSW 取向量，ScalarIndex 取元数据，TextStore 取文本。
    /// 调用者必须持有 _rwLock 的读锁或写锁。
    /// </summary>
    internal VectorRecord? AssembleRecord(ulong id)
    {
        var node = _hnswIndex.GetNode(id);
        if (node == null || node.IsDeleted)
            return null;

        return new VectorRecord
        {
            Id = id,
            Vector = node.Vector.ToArray(),
            Metadata = _scalarIndex.GetRecordMetadata(id),
            Text = _textStore.GetText(id)
        };
    }

    /// <summary>幂等地重放 WAL 插入记录</summary>
    internal void ReplayInsert(VectorRecord record)
    {
        _rwLock.EnterWriteLock();
        try
        {
            // 幂等：如果节点已存在，跳过
            if (_hnswIndex.ContainsNode(record.Id))
                return;

            // 确保 nextRecordId 不回退
            if (record.Id >= _nextRecordId)
                _nextRecordId = record.Id + 1;

            _hnswIndex.Insert(record.Id, record.Vector);
            _scalarIndex.Add(record.Id, record.Metadata);
            _textStore.SetPending(record.Id, record.Text);
            IsDirty = true;
        }
        finally
        {
            _rwLock.ExitWriteLock();
        }
    }

    /// <summary>幂等地重放 WAL 删除记录</summary>
    internal void ReplayDelete(ulong recordId)
    {
        _rwLock.EnterWriteLock();
        try
        {
            if (!_hnswIndex.ContainsActiveNode(recordId))
                return;

            _hnswIndex.MarkDeleted(recordId);
            _scalarIndex.Remove(recordId);
            _textStore.Remove(recordId);
            IsDirty = true;
        }
        finally
        {
            _rwLock.ExitWriteLock();
        }
    }

    /// <summary>
    /// 将集合数据刷入存储页（检查点时调用）。
    /// 释放旧页链，写入新页链，更新根页引用。
    /// </summary>
    internal void FlushToStorage(FileStorage storage)
    {
        _rwLock.EnterWriteLock();
        try
        {
            if (!IsDirty && HnswRootPage != 0)
                return;

            // 保存旧根页引用，异常时恢复
            var oldHnswRoot = HnswRootPage;
            var oldScalarRoot = ScalarIndexRootPage;
            var oldTextRoot = TextStoreRootPage;

            try
            {
                // 先序列化所有数据（在释放旧页链之前，TextStore 可能需要从旧页链读取）
                var hnswData = _hnswIndex.Serialize();
                var scalarData = ScalarIndexSerializer.Serialize(_scalarIndex);
                var activeIds = _hnswIndex.GetActiveNodeIds();
                var textData = _textStore.Serialize(activeIds);

                storage.WriteTransaction(ctx =>
                {
                    // 释放旧页链
                    if (HnswRootPage != 0)
                        PageChainIO.FreeChain(ctx, storage, HnswRootPage);
                    if (ScalarIndexRootPage != 0)
                        PageChainIO.FreeChain(ctx, storage, ScalarIndexRootPage);
                    if (TextStoreRootPage != 0)
                        PageChainIO.FreeChain(ctx, storage, TextStoreRootPage);

                    // 写入新页链
                    HnswRootPage = PageChainIO.WriteChain(ctx, storage, PageType.HNSWGraph, hnswData);
                    ScalarIndexRootPage = PageChainIO.WriteChain(ctx, storage, PageType.ScalarIndex, scalarData);
                    TextStoreRootPage = PageChainIO.WriteChain(ctx, storage, PageType.TextData, textData);
                });
            }
            catch
            {
                // 事务失败（已回滚），恢复旧根页引用
                HnswRootPage = oldHnswRoot;
                ScalarIndexRootPage = oldScalarRoot;
                TextStoreRootPage = oldTextRoot;
                throw;
            }

            // 检查点成功后重置文本存储状态
            _textStore.ClearPending();
            _textStore.ResetChainState(storage, TextStoreRootPage);
            IsDirty = false;

            _logger?.LogDebug("集合 '{Name}' 已刷入存储", Name);
        }
        finally
        {
            _rwLock.ExitWriteLock();
        }
    }

    /// <summary>获取 HNSW 索引</summary>
    internal HNSWIndex HnswIndex => _hnswIndex;

    /// <summary>获取标量索引</summary>
    internal ScalarIndex ScalarIndexInstance => _scalarIndex;
}
