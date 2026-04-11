using System.Runtime.CompilerServices;
using Microsoft.SemanticKernel.Memory;

namespace Mugu.AI.VectorLite.SemanticKernel;

/// <summary>
/// 将 VectorLiteDB 适配为 Semantic Kernel 的 IMemoryStore。
/// </summary>
public sealed class VectorLiteMemoryStore : IMemoryStore, IDisposable
{
    private readonly VectorLiteDB _db;
    private readonly bool _ownsDb;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> _collectionDimensions = new();

    /// <summary>创建新实例，内部创建并管理 VectorLiteDB</summary>
    public VectorLiteMemoryStore(string filePath, VectorLiteOptions? options = null)
    {
        _db = new VectorLiteDB(filePath, options);
        _ownsDb = true;
    }

    /// <summary>包装已有的 VectorLiteDB 实例（不负责释放）</summary>
    public VectorLiteMemoryStore(VectorLiteDB db)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _ownsDb = false;
    }

    public Task CreateCollectionAsync(string collectionName, CancellationToken ct = default)
    {
        // SK 的 CreateCollectionAsync 不传维度信息，延迟到第一次 Upsert 时创建
        return Task.CompletedTask;
    }

    public async IAsyncEnumerable<string> GetCollectionsAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        foreach (var name in _db.GetCollectionNames())
        {
            ct.ThrowIfCancellationRequested();
            yield return name;
        }
    }

    public Task<bool> DoesCollectionExistAsync(string collectionName, CancellationToken ct = default)
    {
        return Task.FromResult(_db.CollectionExists(collectionName));
    }

    public Task DeleteCollectionAsync(string collectionName, CancellationToken ct = default)
    {
        _db.DeleteCollection(collectionName);
        _collectionDimensions.TryRemove(collectionName, out _);
        return Task.CompletedTask;
    }

    public async Task<string> UpsertAsync(string collectionName, MemoryRecord record,
        CancellationToken ct = default)
    {
        var collection = GetOrCreateCollectionFromRecord(collectionName, record);
        var vectorRecord = MemoryRecordMapper.ToVectorRecord(record);

        // 尝试查找已存在的相同 key 记录并删除
        var existingId = await FindBySkKeyAsync(collection, record.Metadata.Id, ct);
        if (existingId.HasValue)
        {
            await collection.DeleteAsync(existingId.Value, ct);
        }

        await collection.InsertAsync(vectorRecord, ct);
        return record.Metadata.Id;
    }

    public async IAsyncEnumerable<string> UpsertBatchAsync(string collectionName,
        IEnumerable<MemoryRecord> records,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        foreach (var record in records)
        {
            ct.ThrowIfCancellationRequested();
            yield return await UpsertAsync(collectionName, record, ct);
        }
    }

    public async Task<MemoryRecord?> GetAsync(string collectionName, string key,
        bool withEmbedding = false, CancellationToken ct = default)
    {
        var collection = _db.GetCollection(collectionName);
        if (collection == null) return null;

        var id = await FindBySkKeyAsync(collection, key, ct);
        if (!id.HasValue) return null;

        var record = await collection.GetAsync(id.Value, ct);
        if (record == null) return null;

        return MemoryRecordMapper.ToMemoryRecord(record, withEmbedding);
    }

    public async IAsyncEnumerable<MemoryRecord> GetBatchAsync(string collectionName,
        IEnumerable<string> keys, bool withEmbedding = false,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        foreach (var key in keys)
        {
            ct.ThrowIfCancellationRequested();
            var result = await GetAsync(collectionName, key, withEmbedding, ct);
            if (result != null) yield return result;
        }
    }

    public async Task RemoveAsync(string collectionName, string key, CancellationToken ct = default)
    {
        var collection = _db.GetCollection(collectionName);
        if (collection == null) return;

        var id = await FindBySkKeyAsync(collection, key, ct);
        if (id.HasValue)
            await collection.DeleteAsync(id.Value, ct);
    }

    public async Task RemoveBatchAsync(string collectionName, IEnumerable<string> keys,
        CancellationToken ct = default)
    {
        foreach (var key in keys)
        {
            ct.ThrowIfCancellationRequested();
            await RemoveAsync(collectionName, key, ct);
        }
    }

    public async Task<(MemoryRecord, double)?> GetNearestMatchAsync(string collectionName,
        ReadOnlyMemory<float> embedding, double minRelevanceScore = 0, bool withEmbedding = false,
        CancellationToken ct = default)
    {
        var results = GetNearestMatchesAsync(collectionName, embedding, 1, minRelevanceScore,
            withEmbedding, ct);

        await foreach (var result in results.WithCancellation(ct))
        {
            return result;
        }

        return null;
    }

    public async IAsyncEnumerable<(MemoryRecord, double)> GetNearestMatchesAsync(
        string collectionName, ReadOnlyMemory<float> embedding, int limit,
        double minRelevanceScore = 0, bool withEmbedding = false,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var collection = _db.GetCollection(collectionName);
        if (collection == null) yield break;

        var results = await collection
            .Query(embedding.ToArray())
            .TopK(limit)
            .WithMinScore((float)minRelevanceScore)
            .ToListAsync(ct);

        foreach (var result in results)
        {
            ct.ThrowIfCancellationRequested();
            var memoryRecord = MemoryRecordMapper.ToMemoryRecord(result.Record, withEmbedding);
            yield return (memoryRecord, result.Score);
        }
    }

    public void Dispose()
    {
        if (_ownsDb)
            _db.Dispose();
    }

    /// <summary>获取或从记录推断维度创建集合</summary>
    private ICollection GetOrCreateCollectionFromRecord(string collectionName, MemoryRecord record)
    {
        var existing = _db.GetCollection(collectionName);
        if (existing != null) return existing;

        var dimensions = MemoryRecordMapper.GetDimensions(record);
        _collectionDimensions[collectionName] = dimensions;
        return _db.GetOrCreateCollection(collectionName, dimensions);
    }

    /// <summary>通过 SK key 查找记录ID（直接使用元数据索引，避免零向量查询）</summary>
    private static async Task<ulong?> FindBySkKeyAsync(ICollection collection, string skKey,
        CancellationToken ct)
    {
        var ids = await collection.FindIdsByMetadataAsync(MemoryRecordMapper.SkKeyField, skKey, ct);
        return ids.Count > 0 ? ids[0] : null;
    }
}
