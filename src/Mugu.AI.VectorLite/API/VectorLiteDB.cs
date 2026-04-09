using Mugu.AI.VectorLite.Storage;
using Microsoft.Extensions.Logging;

namespace Mugu.AI.VectorLite;

/// <summary>
/// VectorLiteDB：嵌入式向量数据库的主入口类。
/// 管理数据库文件、集合以及后台检查点定时器。
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

        // 创建或打开文件存储
        _storage = File.Exists(filePath)
            ? FileStorage.Open(filePath, options.LoggerFactory)
            : FileStorage.CreateNew(filePath, options.PageSize, options.MaxDimensions, options.LoggerFactory);

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
                _options.LoggerFactory?.CreateLogger<Collection>());
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

    /// <summary>删除集合</summary>
    public bool DeleteCollection(string name)
    {
        _rwLock.EnterWriteLock();
        try
        {
            EnsureNotDisposed();
            if (_collections.Remove(name))
            {
                _logger?.LogInformation("删除集合: {Name}", name);
                return true;
            }
            return false;
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
            _storage.Checkpoint();
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

        try { _storage.Checkpoint(); }
        catch { /* 尽力而为 */ }

        _storage.Dispose();
        _rwLock.Dispose();

        _logger?.LogInformation("数据库已关闭: {Path}", FilePath);
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
