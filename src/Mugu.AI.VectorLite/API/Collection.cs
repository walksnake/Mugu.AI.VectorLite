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
internal sealed class Collection : ICollection, IDisposable
{
    private HNSWIndex _hnswIndex;
    private ScalarIndex _scalarIndex;
    private QueryEngine _queryEngine;
    private ScalarQueryEngine _scalarQueryEngine;
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
    public CollectionMode Mode { get; }
    public int Count => Mode == CollectionMode.Vector ? _hnswIndex.Count : _scalarIndex.Count;

    internal ulong NextRecordId => _nextRecordId;

    /// <summary>创建新集合</summary>
    internal Collection(string name, int dimensions, VectorLiteOptions options,
        FileStorage? storage = null, ILogger? logger = null,
        CollectionMode mode = CollectionMode.Vector)
    {
        Name = name;
        Dimensions = dimensions;
        Mode = mode;
        _options = options;
        _efSearch = options.HnswEfSearch;
        _storage = storage;
        _logger = logger;

        _distFunc = DistanceFunctionFactory.Get(options.DefaultDistanceMetric);
        _hnswIndex = new HNSWIndex(_distFunc, options.HnswM, options.HnswEfConstruction);
        _scalarIndex = new ScalarIndex();
        _textStore = new TextStore();
        _queryEngine = new QueryEngine(_hnswIndex, _scalarIndex, _distFunc);
        _scalarQueryEngine = new ScalarQueryEngine(_scalarIndex);
    }

    /// <summary>从快照加载集合（私有构造）</summary>
    private Collection(
        string name, int dimensions, VectorLiteOptions options, FileStorage storage,
        HNSWIndex hnswIndex, ScalarIndex scalarIndex, TextStore textStore,
        ulong nextRecordId, ulong hnswRootPage, ulong scalarIndexRootPage, ulong textStoreRootPage,
        ILogger? logger, CollectionMode mode)
    {
        Name = name;
        Dimensions = dimensions;
        Mode = mode;
        _options = options;
        _efSearch = options.HnswEfSearch;
        _storage = storage;
        _logger = logger;

        _distFunc = DistanceFunctionFactory.Get(options.DefaultDistanceMetric);
        _hnswIndex = hnswIndex;
        _scalarIndex = scalarIndex;
        _textStore = textStore;
        _queryEngine = new QueryEngine(_hnswIndex, _scalarIndex, _distFunc);
        _scalarQueryEngine = new ScalarQueryEngine(_scalarIndex);
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
        if (entry.Mode == CollectionMode.Vector && entry.HNSWRootPage != 0)
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
            logger, entry.Mode);
    }

    public Task<ulong> InsertAsync(VectorRecord record, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        EnsureMode(CollectionMode.Vector, "向量写入");

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

        // 不修改调用方传入的 record 对象，id 仅在内部使用
        if (_storage != null)
        {
            var walData = RecordSerializer.SerializeInsert(Name, id, record);
            _storage.LogLogicalOperation(WalOperationType.RecordInsert, walData);
        }

        // 再更新内存（若部分失败则回滚已更新的部分）
        var hnswInserted = false;
        var scalarAdded = false;
        try
        {
            _hnswIndex.Insert(id, record.Vector);
            hnswInserted = true;

            _scalarIndex.AddRecord(id, record.Metadata);
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
        EnsureMode(CollectionMode.Vector, "向量批量写入");

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
        EnsureMode(CollectionMode.Vector, "向量记录读取");
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

    public Task<IReadOnlyList<RecordView>> GetBatchAsync(
        IEnumerable<ulong> ids,
        RecordProjection projection = RecordProjection.All,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(ids);
        ct.ThrowIfCancellationRequested();
        _rwLock.EnterReadLock();
        try
        {
            var idList = ids.ToList();
            var texts = projection.HasFlag(RecordProjection.Text)
                ? _textStore.GetTexts(idList)
                : null;
            var results = new List<RecordView>();
            foreach (var id in idList)
            {
                ct.ThrowIfCancellationRequested();
                var record = AssembleRecordView(id, projection, texts);
                if (record is not null)
                    results.Add(record);
            }
            return Task.FromResult<IReadOnlyList<RecordView>>(results);
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
        if (Mode == CollectionMode.Vector && !_hnswIndex.ContainsActiveNode(id))
            return false;
        if (Mode == CollectionMode.ScalarOnly
            && _scalarIndex.GetRecordMetadataView(id) is null)
            return false;

        // 先写逻辑 WAL
        if (_storage != null)
        {
            var walData = RecordSerializer.SerializeDelete(Name, id);
            _storage.LogLogicalOperation(WalOperationType.RecordDelete, walData);
        }

        if (Mode == CollectionMode.Vector)
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
            var ids = _scalarIndex.GetRecordIdsView(field, value);
            return Task.FromResult<IReadOnlyList<ulong>>(ids.ToArray());
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
        EnsureMode(CollectionMode.Vector, "向量更新");

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
        EnsureMode(CollectionMode.Vector, "向量查询");
        if (queryVector.Length != Dimensions)
            throw new DimensionMismatchException(Dimensions, queryVector.Length);

        return new QueryBuilder(this, queryVector);
    }

    public IScalarQueryBuilder Filter() => new ScalarQueryBuilder(this);

    public Task<ulong> InsertScalarAsync(ScalarRecord record, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        ct.ThrowIfCancellationRequested();
        EnsureMode(CollectionMode.ScalarOnly, "纯标量写入");
        _rwLock.EnterWriteLock();
        try
        {
            return Task.FromResult(InsertScalarCore(record));
        }
        finally
        {
            _rwLock.ExitWriteLock();
        }
    }

    public Task<IReadOnlyList<ulong>> InsertScalarBatchAsync(
        IEnumerable<ScalarRecord> records,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(records);
        ct.ThrowIfCancellationRequested();
        EnsureMode(CollectionMode.ScalarOnly, "纯标量批量写入");
        var items = records.ToList();
        _rwLock.EnterWriteLock();
        try
        {
            var ids = new List<ulong>(items.Count);
            foreach (var record in items)
            {
                ct.ThrowIfCancellationRequested();
                ids.Add(InsertScalarCore(record));
            }
            return Task.FromResult<IReadOnlyList<ulong>>(ids);
        }
        finally
        {
            _rwLock.ExitWriteLock();
        }
    }

    public Task CreateScalarIndexAsync(
        ScalarIndexDefinition definition,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        _rwLock.EnterWriteLock();
        try
        {
            _scalarIndex.CreateIndex(definition);
            IsDirty = true;
            return Task.CompletedTask;
        }
        finally
        {
            _rwLock.ExitWriteLock();
        }
    }

    public Task<IReadOnlyList<ScalarIndexDefinition>> ListScalarIndexesAsync(
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        _rwLock.EnterReadLock();
        try
        {
            return Task.FromResult(_scalarIndex.Definitions);
        }
        finally
        {
            _rwLock.ExitReadLock();
        }
    }

    internal Task<ScalarQueryPage> ExecuteScalarQueryAsync(
        ScalarQueryRequest request,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        _rwLock.EnterReadLock();
        try
        {
            var result = _scalarQueryEngine.Execute(request, ct);
            var texts = request.Projection.HasFlag(RecordProjection.Text)
                ? _textStore.GetTexts(result.RecordIds)
                : null;
            var records = result.RecordIds
                .Select(id => AssembleRecordView(id, request.Projection, texts))
                .Where(record => record is not null)
                .Cast<RecordView>()
                .ToArray();
            return Task.FromResult(new ScalarQueryPage
            {
                Records = records,
                NextCursor = result.NextCursor
            });
        }
        finally
        {
            _rwLock.ExitReadLock();
        }
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

    private RecordView? AssembleRecordView(
        ulong id,
        RecordProjection projection,
        IReadOnlyDictionary<ulong, string?>? texts = null)
    {
        var metadata = _scalarIndex.GetRecordMetadataView(id);
        if (metadata is null)
            return null;
        var vector = ReadProjectedVector(id, projection);
        if (Mode == CollectionMode.Vector && vector is null
            && projection.HasFlag(RecordProjection.Vector))
        {
            return null;
        }
        return new RecordView
        {
            Id = id,
            Vector = vector,
            Metadata = projection.HasFlag(RecordProjection.Metadata)
                ? new Dictionary<string, object>(metadata)
                : null,
            Text = ReadProjectedText(id, projection, texts)
        };
    }

    private string? ReadProjectedText(
        ulong id,
        RecordProjection projection,
        IReadOnlyDictionary<ulong, string?>? texts)
    {
        if (!projection.HasFlag(RecordProjection.Text))
            return null;
        return texts is not null && texts.TryGetValue(id, out var text)
            ? text
            : _textStore.GetText(id);
    }

    private float[]? ReadProjectedVector(ulong id, RecordProjection projection)
    {
        if (Mode != CollectionMode.Vector || !projection.HasFlag(RecordProjection.Vector))
            return null;
        var node = _hnswIndex.GetNode(id);
        return node is null || node.IsDeleted ? null : node.Vector.ToArray();
    }

    private ulong InsertScalarCore(ScalarRecord record)
    {
        var id = _nextRecordId++;
        if (_storage != null)
        {
            var persisted = new VectorRecord
            {
                Vector = [],
                Metadata = record.Metadata,
                Text = record.Text
            };
            var walData = RecordSerializer.SerializeInsert(Name, id, persisted);
            _storage.LogLogicalOperation(WalOperationType.RecordInsert, walData);
        }
        _scalarIndex.AddRecord(id, record.Metadata);
        _textStore.SetPending(id, record.Text);
        IsDirty = true;
        return id;
    }

    private void EnsureMode(CollectionMode expected, string operation)
    {
        if (Mode != expected)
            throw new CollectionException(
                $"集合 '{Name}' 的模式为 {Mode}，不支持{operation}");
    }

    /// <summary>幂等地重放 WAL 插入记录</summary>
    internal void ReplayInsert(VectorRecord record)
    {
        _rwLock.EnterWriteLock();
        try
        {
            // 幂等：如果节点已存在且活跃，跳过（使用 ContainsActiveNode 避免重复插入已删除节点）
            if (Mode == CollectionMode.Vector && _hnswIndex.ContainsActiveNode(record.Id))
                return;
            if (Mode == CollectionMode.ScalarOnly
                && _scalarIndex.GetRecordMetadataView(record.Id) is not null)
                return;

            // 确保 nextRecordId 不回退
            if (record.Id >= _nextRecordId)
                _nextRecordId = record.Id + 1;

            if (Mode == CollectionMode.Vector)
                _hnswIndex.Insert(record.Id, record.Vector);
            _scalarIndex.AddRecord(record.Id, record.Metadata);
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
            if (Mode == CollectionMode.Vector && !_hnswIndex.ContainsActiveNode(recordId))
                return;
            if (Mode == CollectionMode.ScalarOnly
                && _scalarIndex.GetRecordMetadataView(recordId) is null)
                return;

            if (Mode == CollectionMode.Vector)
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
            if (!IsDirty && (Mode == CollectionMode.ScalarOnly || HnswRootPage != 0))
                return;

            // 检查是否需要对 HNSW 图进行压缩（已删除节点超过20%时重建索引）
            if (Mode == CollectionMode.Vector && _hnswIndex.NeedsCompaction())
            {
                _logger?.LogInformation("集合 '{Name}' 触发 HNSW 图压缩，当前删除比例超过20%", Name);
                CompactHnswIndex();
            }

            // 保存旧根页引用，异常时恢复
            var oldHnswRoot = HnswRootPage;
            var oldScalarRoot = ScalarIndexRootPage;
            var oldTextRoot = TextStoreRootPage;

            try
            {
                // 先序列化所有数据（在释放旧页链之前，TextStore 可能需要从旧页链读取）
                var hnswData = Mode == CollectionMode.Vector ? _hnswIndex.Serialize() : [];
                var scalarData = ScalarIndexSerializer.Serialize(_scalarIndex);
                var activeIds = Mode == CollectionMode.Vector
                    ? _hnswIndex.GetActiveNodeIds()
                    : _scalarIndex.GetAllRecordIds();
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
                    HnswRootPage = Mode == CollectionMode.Vector
                        ? PageChainIO.WriteChain(ctx, storage, PageType.HNSWGraph, hnswData)
                        : 0;
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

    /// <summary>释放内部的 ReaderWriterLockSlim 等非托管资源</summary>
    public void Dispose()
    {
        _rwLock.Dispose();
    }

    /// <summary>
    /// 重建 HNSW 索引：移除所有已删除节点，减少内存占用并提升搜索精度。
    /// 调用方必须持有写锁。
    /// </summary>
    private void CompactHnswIndex()
    {
        var activeNodes = _hnswIndex.GetActiveNodes().ToList();
        var newIndex = new HNSWIndex(_distFunc, _options.HnswM, _options.HnswEfConstruction);
        foreach (var (recordId, vector) in activeNodes)
        {
            newIndex.Insert(recordId, vector);
        }
        _hnswIndex = newIndex;
        _queryEngine = new QueryEngine(_hnswIndex, _scalarIndex, _distFunc);
        _logger?.LogDebug("集合 '{Name}' HNSW 图压缩完成，活跃节点数={Count}", Name, activeNodes.Count);
    }
}
