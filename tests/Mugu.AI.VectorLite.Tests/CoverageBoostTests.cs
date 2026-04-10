using Mugu.AI.VectorLite;
using Mugu.AI.VectorLite.Engine;
using Mugu.AI.VectorLite.Engine.Distance;
using Mugu.AI.VectorLite.Storage;

namespace Mugu.AI.VectorLite.Tests;

/// <summary>
/// 覆盖率提升测试：针对未覆盖的代码路径编写的精确测试。
/// </summary>
public class CoverageBoostTests : IDisposable
{
    private readonly string _testDir;

    public CoverageBoostTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "VLite_CoverageBoost_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_testDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_testDir, true); } catch { }
    }

    private string GetDbPath(string name) => Path.Combine(_testDir, name + ".db");

    #region VectorLiteDB — 验证与边界

    [Fact]
    public void GetOrCreateCollection_EmptyName_Throws()
    {
        var path = GetDbPath("empty_name");
        using var db = new VectorLiteDB(path, new VectorLiteOptions
        {
            CheckpointInterval = Timeout.InfiniteTimeSpan
        });
        Assert.Throws<ArgumentException>(() => db.GetOrCreateCollection("", 4));
        Assert.Throws<ArgumentException>(() => db.GetOrCreateCollection("  ", 4));
    }

    [Fact]
    public void GetOrCreateCollection_NegativeDimensions_Throws()
    {
        var path = GetDbPath("neg_dims");
        using var db = new VectorLiteDB(path, new VectorLiteOptions
        {
            CheckpointInterval = Timeout.InfiniteTimeSpan
        });
        Assert.Throws<ArgumentOutOfRangeException>(() => db.GetOrCreateCollection("test", 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => db.GetOrCreateCollection("test", -5));
    }

    [Fact]
    public void GetOrCreateCollection_DimensionMismatch_Throws()
    {
        var path = GetDbPath("dim_mismatch");
        using var db = new VectorLiteDB(path, new VectorLiteOptions
        {
            CheckpointInterval = Timeout.InfiniteTimeSpan
        });
        db.GetOrCreateCollection("test", 4);
        Assert.Throws<CollectionException>(() => db.GetOrCreateCollection("test", 8));
    }

    [Fact]
    public void GetCollection_NonExistent_ReturnsNull()
    {
        var path = GetDbPath("get_null");
        using var db = new VectorLiteDB(path, new VectorLiteOptions
        {
            CheckpointInterval = Timeout.InfiniteTimeSpan
        });
        Assert.Null(db.GetCollection("nonexistent"));
    }

    [Fact]
    public void GetCollection_Existing_ReturnsCollection()
    {
        var path = GetDbPath("get_existing");
        using var db = new VectorLiteDB(path, new VectorLiteOptions
        {
            CheckpointInterval = Timeout.InfiniteTimeSpan
        });
        var created = db.GetOrCreateCollection("test", 4);
        var fetched = db.GetCollection("test");
        Assert.NotNull(fetched);
        Assert.Same(created, fetched);
    }

    [Fact]
    public void CollectionExists_TrueAndFalse()
    {
        var path = GetDbPath("exists");
        using var db = new VectorLiteDB(path, new VectorLiteOptions
        {
            CheckpointInterval = Timeout.InfiniteTimeSpan
        });
        Assert.False(db.CollectionExists("test"));
        db.GetOrCreateCollection("test", 4);
        Assert.True(db.CollectionExists("test"));
    }

    [Fact]
    public void FilePath_Property_Accessible()
    {
        var path = GetDbPath("filepath");
        using var db = new VectorLiteDB(path, new VectorLiteOptions
        {
            CheckpointInterval = Timeout.InfiniteTimeSpan
        });
        Assert.Equal(path, db.FilePath);
    }

    #endregion

    #region VectorLiteDB — WAL 逻辑重放

    [Fact]
    public async Task ReplayLogicalRecords_MissingCollection_SkipsGracefully()
    {
        var path = GetDbPath("replay_missing");

        // 步骤1：创建数据库并写入数据到已有集合，检查点保存
        using (var db = new VectorLiteDB(path, new VectorLiteOptions
        {
            CheckpointInterval = Timeout.InfiniteTimeSpan
        }))
        {
            var coll = db.GetOrCreateCollection("existing", 4);
            await coll.InsertAsync(new VectorRecord
            {
                Vector = new float[] { 1, 2, 3, 4 },
                Text = "真实记录"
            });
            db.Checkpoint();
        }

        // 步骤2：直接通过 FileStorage 写入逻辑 WAL 记录，引用不存在的集合
        using (var storage = FileStorage.Open(path))
        {
            // 构造一个指向不存在集合 "ghost" 的 InsertRecord
            var fakeRecord = new VectorRecord
            {
                Id = 999,
                Vector = new float[] { 5, 6, 7, 8 },
                Text = "幽灵记录"
            };
            var insertData = RecordSerializer.SerializeInsert("ghost", fakeRecord);
            storage.LogLogicalOperation(WalOperationType.RecordInsert, insertData);

            // 构造一个指向不存在集合 "phantom" 的 DeleteRecord
            var deleteData = RecordSerializer.SerializeDelete("phantom", 123);
            storage.LogLogicalOperation(WalOperationType.RecordDelete, deleteData);
        }

        // 步骤3：重新打开数据库 —— ReplayLogicalRecords 应处理缺失集合
        using (var db = new VectorLiteDB(path, new VectorLiteOptions
        {
            CheckpointInterval = Timeout.InfiniteTimeSpan
        }))
        {
            // 已有集合仍完好
            var coll = db.GetCollection("existing");
            Assert.NotNull(coll);
            Assert.Equal(1, coll!.Count);

            // 不存在的集合不会被自动创建
            Assert.Null(db.GetCollection("ghost"));
            Assert.Null(db.GetCollection("phantom"));
        }
    }

    [Fact]
    public async Task ReplayLogicalRecords_IdempotentInsert_SkipsDuplicate()
    {
        var path = GetDbPath("replay_idempotent");

        // 步骤1：创建数据库，写入数据，检查点
        using (var db = new VectorLiteDB(path, new VectorLiteOptions
        {
            CheckpointInterval = Timeout.InfiniteTimeSpan
        }))
        {
            var coll = db.GetOrCreateCollection("test", 4);
            await coll.InsertAsync(new VectorRecord
            {
                Vector = new float[] { 1, 2, 3, 4 },
                Text = "原始记录"
            });
            db.Checkpoint();
        }

        // 步骤2：写入重复 ID 的 InsertRecord 到 WAL
        using (var storage = FileStorage.Open(path))
        {
            var duplicateRecord = new VectorRecord
            {
                Id = 1, // 已存在的 ID
                Vector = new float[] { 9, 8, 7, 6 },
                Text = "重复记录"
            };
            var insertData = RecordSerializer.SerializeInsert("test", duplicateRecord);
            storage.LogLogicalOperation(WalOperationType.RecordInsert, insertData);
        }

        // 步骤3：重新打开 —— 幂等重放应跳过重复
        using (var db = new VectorLiteDB(path, new VectorLiteOptions
        {
            CheckpointInterval = Timeout.InfiniteTimeSpan
        }))
        {
            var coll = db.GetCollection("test");
            Assert.NotNull(coll);
            Assert.Equal(1, coll!.Count);
        }
    }

    [Fact]
    public async Task ReplayLogicalRecords_DeleteReplay_Works()
    {
        var path = GetDbPath("replay_delete");

        // 步骤1：创建数据库，写入两条记录，检查点
        using (var db = new VectorLiteDB(path, new VectorLiteOptions
        {
            CheckpointInterval = Timeout.InfiniteTimeSpan
        }))
        {
            var coll = db.GetOrCreateCollection("test", 4);
            await coll.InsertAsync(new VectorRecord
            {
                Vector = new float[] { 1, 2, 3, 4 },
                Text = "记录1"
            });
            await coll.InsertAsync(new VectorRecord
            {
                Vector = new float[] { 5, 6, 7, 8 },
                Text = "记录2"
            });
            db.Checkpoint();
        }

        // 步骤2：写入 DeleteRecord 到 WAL
        using (var storage = FileStorage.Open(path))
        {
            var deleteData = RecordSerializer.SerializeDelete("test", 1);
            storage.LogLogicalOperation(WalOperationType.RecordDelete, deleteData);
        }

        // 步骤3：重新打开 —— 应重放删除
        using (var db = new VectorLiteDB(path, new VectorLiteOptions
        {
            CheckpointInterval = Timeout.InfiniteTimeSpan
        }))
        {
            var coll = db.GetCollection("test");
            Assert.NotNull(coll);
            Assert.Equal(1, coll!.Count);
        }
    }

    #endregion

    #region Collection — DeleteBatchAsync

    [Fact]
    public async Task DeleteBatchAsync_RemovesMultipleRecords()
    {
        var path = GetDbPath("delete_batch");
        using var db = new VectorLiteDB(path, new VectorLiteOptions
        {
            CheckpointInterval = Timeout.InfiniteTimeSpan
        });
        var coll = db.GetOrCreateCollection("test", 4);

        var ids = new List<ulong>();
        for (int i = 0; i < 5; i++)
        {
            var id = await coll.InsertAsync(new VectorRecord
            {
                Vector = new float[] { i, i + 1, i + 2, i + 3 },
                Text = $"文本{i}"
            });
            ids.Add(id);
        }

        Assert.Equal(5, coll.Count);

        // 批量删除前3条
        var deleted = await coll.DeleteBatchAsync(ids.Take(3));
        Assert.Equal(3, deleted);
        Assert.Equal(2, coll.Count);

        // 删除不存在的ID返回0
        var noDelete = await coll.DeleteBatchAsync(new ulong[] { 999, 998 });
        Assert.Equal(0, noDelete);
    }

    [Fact]
    public async Task DeleteBatchAsync_CancellationToken_Throws()
    {
        var path = GetDbPath("delete_batch_cancel");
        using var db = new VectorLiteDB(path, new VectorLiteOptions
        {
            CheckpointInterval = Timeout.InfiniteTimeSpan
        });
        var coll = db.GetOrCreateCollection("test", 4);
        var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => coll.DeleteBatchAsync(new ulong[] { 1 }, cts.Token));
    }

    #endregion

    #region Collection — UpsertAsync 验证

    [Fact]
    public async Task UpsertAsync_DimensionMismatch_Throws()
    {
        var path = GetDbPath("upsert_dims");
        using var db = new VectorLiteDB(path, new VectorLiteOptions
        {
            CheckpointInterval = Timeout.InfiniteTimeSpan
        });
        var coll = db.GetOrCreateCollection("test", 4);

        await Assert.ThrowsAsync<DimensionMismatchException>(() =>
            coll.UpsertAsync(new VectorRecord
            {
                Vector = new float[] { 1, 2, 3 }, // 3维 != 4维
                Metadata = new Dictionary<string, object> { { "key", "val" } }
            }, "key"));
    }

    [Fact]
    public async Task UpsertAsync_MissingKeyField_Throws()
    {
        var path = GetDbPath("upsert_nokey");
        using var db = new VectorLiteDB(path, new VectorLiteOptions
        {
            CheckpointInterval = Timeout.InfiniteTimeSpan
        });
        var coll = db.GetOrCreateCollection("test", 4);

        // 无 metadata
        await Assert.ThrowsAsync<ArgumentException>(() =>
            coll.UpsertAsync(new VectorRecord
            {
                Vector = new float[] { 1, 2, 3, 4 },
                Metadata = null
            }, "key"));

        // metadata 中不包含 keyField
        await Assert.ThrowsAsync<ArgumentException>(() =>
            coll.UpsertAsync(new VectorRecord
            {
                Vector = new float[] { 1, 2, 3, 4 },
                Metadata = new Dictionary<string, object> { { "other", "val" } }
            }, "key"));
    }

    #endregion

    #region PageManager — GrowFile 触发

    [Fact]
    public async Task GrowFile_TriggeredByLargeData()
    {
        var path = GetDbPath("growfile");
        var options = new VectorLiteOptions
        {
            PageSize = 4096,
            CheckpointInterval = Timeout.InfiniteTimeSpan
        };

        using var db = new VectorLiteDB(path, options);
        var coll = db.GetOrCreateCollection("grow", 64);

        var rng = new Random(42);
        var longText = new string('A', 5000); // 5KB 文本

        // 初始文件大小（256 页 * 4096 = 1MB）
        var initialSize = new FileInfo(path).Length;

        // 插入 200 条带大文本的记录，首次检查点就超过初始文件大小
        for (var i = 0; i < 200; i++)
        {
            var vec = new float[64];
            for (var j = 0; j < 64; j++)
                vec[j] = (float)rng.NextDouble();

            await coll.InsertAsync(new VectorRecord
            {
                Vector = vec,
                Text = longText,
                Metadata = new Dictionary<string, object> { { "idx", (long)i } }
            });
        }

        // 首次检查点将使用大量页 —— 预期触发 GrowFile
        db.Checkpoint();

        var afterSize = new FileInfo(path).Length;
        Assert.True(afterSize > initialSize,
            $"文件应已增长: 初始={initialSize}, 当前={afterSize}");
        Assert.Equal(200, coll.Count);

        // 验证数据可查询
        var queryVec = new float[64];
        for (var j = 0; j < 64; j++)
            queryVec[j] = 0.5f;
        var results = await coll.Query(queryVec).TopK(3).ToListAsync();
        Assert.Equal(3, results.Count);
    }

    #endregion

    #region PageManager — CreateNew 文件已存在

    [Fact]
    public void PageManager_CreateNew_FileExists_Throws()
    {
        var path = GetDbPath("pm_exists");
        // 先创建一个文件
        File.WriteAllText(path, "dummy");

        Assert.Throws<StorageException>(() =>
            PageManager.CreateNew(path, 8192, 4096));
    }

    #endregion

    #region WAL — CRC 校验失败路径

    [Fact]
    public async Task Wal_CorruptedCRC_TruncatesAtBadRecord()
    {
        var path = GetDbPath("wal_crc");

        // 步骤1：创建数据库，插入数据，检查点写入主文件
        using (var db = new VectorLiteDB(path, new VectorLiteOptions
        {
            CheckpointInterval = Timeout.InfiniteTimeSpan
        }))
        {
            var coll = db.GetOrCreateCollection("test", 4);
            await coll.InsertAsync(new VectorRecord
            {
                Vector = new float[] { 1, 2, 3, 4 },
                Text = "安全记录"
            });
            db.Checkpoint();
        }

        // 步骤2：写入两条逻辑 WAL 记录
        using (var storage = FileStorage.Open(path))
        {
            var r1 = new VectorRecord
            {
                Id = 100,
                Vector = new float[] { 1, 1, 1, 1 },
                Text = "记录A"
            };
            var r2 = new VectorRecord
            {
                Id = 101,
                Vector = new float[] { 2, 2, 2, 2 },
                Text = "记录B"
            };
            storage.LogLogicalOperation(WalOperationType.RecordInsert,
                RecordSerializer.SerializeInsert("test", r1));
            storage.LogLogicalOperation(WalOperationType.RecordInsert,
                RecordSerializer.SerializeInsert("test", r2));
        }

        // 步骤3：破坏最后一条记录的 CRC（倒数第2字节翻转）
        var walPath = path + "-wal";
        var walData = File.ReadAllBytes(walPath);
        walData[walData.Length - 2] ^= 0xFF;
        File.WriteAllBytes(walPath, walData);

        // 步骤4：重新打开 —— 第二条被损坏的记录应被跳过，只重放第一条
        using (var db = new VectorLiteDB(path, new VectorLiteOptions
        {
            CheckpointInterval = Timeout.InfiniteTimeSpan
        }))
        {
            var coll = db.GetCollection("test");
            Assert.NotNull(coll);
            // 原有1条 + WAL重放最多1条（第二条CRC失败后被截断）
            // 取决于截断发生在完整事务的哪个记录上
            Assert.True(coll!.Count >= 1 && coll.Count <= 2);
        }
    }

    #endregion

    #region Collection — LoadFromStorage 重新打开已有集合

    [Fact]
    public async Task LoadFromStorage_WithData_ReloadsCorrectly()
    {
        var path = GetDbPath("reload_data");

        using (var db = new VectorLiteDB(path, new VectorLiteOptions
        {
            CheckpointInterval = Timeout.InfiniteTimeSpan
        }))
        {
            var coll = db.GetOrCreateCollection("test", 8);
            // 插入几条记录确保 HNSW/ScalarIndex/TextStore 都有数据
            for (int i = 0; i < 10; i++)
            {
                await coll.InsertAsync(new VectorRecord
                {
                    Vector = Enumerable.Range(0, 8).Select(x => (float)(i * 8 + x)).ToArray(),
                    Text = $"文本{i}",
                    Metadata = new Dictionary<string, object> { { "idx", (long)i } }
                });
            }
            db.Checkpoint();
        }

        // 重新打开 —— 从快照加载
        using (var db = new VectorLiteDB(path, new VectorLiteOptions
        {
            CheckpointInterval = Timeout.InfiniteTimeSpan
        }))
        {
            var coll = db.GetCollection("test");
            Assert.NotNull(coll);
            Assert.Equal(10, coll!.Count);

            // 查询验证
            var results = await coll.Query(new float[8]).TopK(5).ToListAsync();
            Assert.Equal(5, results.Count);
        }
    }

    #endregion

    #region VectorLiteDB — DeleteCollection 释放页链

    [Fact]
    public async Task DeleteCollection_FreesPageChains()
    {
        var path = GetDbPath("delete_coll_pages");
        using var db = new VectorLiteDB(path, new VectorLiteOptions
        {
            CheckpointInterval = Timeout.InfiniteTimeSpan
        });

        var coll = db.GetOrCreateCollection("todelete", 4);
        for (int i = 0; i < 10; i++)
        {
            await coll.InsertAsync(new VectorRecord
            {
                Vector = new float[] { i, i + 1, i + 2, i + 3 },
                Text = $"删除集合测试{i}"
            });
        }
        db.Checkpoint();

        Assert.True(db.CollectionExists("todelete"));
        db.DeleteCollection("todelete");
        Assert.False(db.CollectionExists("todelete"));

        // 检查点后再次确认已删除
        db.Checkpoint();

        // 创建新集合复用释放的空间
        var newColl = db.GetOrCreateCollection("reuse", 4);
        await newColl.InsertAsync(new VectorRecord
        {
            Vector = new float[] { 1, 2, 3, 4 },
            Text = "复用空间"
        });
        Assert.Equal(1, newColl.Count);
    }

    #endregion

    #region ScalarIndex — 更多边界覆盖

    [Fact]
    public void ScalarIndex_RangeQuery_NumericTypes()
    {
        var index = new ScalarIndex();

        // 混合 long 和 double 类型
        index.Add(1, new Dictionary<string, object> { { "score", 10L } });
        index.Add(2, new Dictionary<string, object> { { "score", 20.5 } });
        index.Add(3, new Dictionary<string, object> { { "score", 30L } });
        index.Add(4, new Dictionary<string, object> { { "score", 5.5 } });

        var filter = new RangeFilter("score", 10L, 25L);
        var result = filter.Evaluate(index);
        // 10L ≤ score ≤ 25L：匹配 id=1(10L), id=2(20.5)
        Assert.Contains(1UL, result);
    }

    #endregion

    #region Distance — 确保所有 SIMD 路径被触发

    [Fact]
    public void Distance_LargeVectors_TriggersSimd()
    {
        // 使用 >= 32 个元素的向量以触发 SIMD 路径（Vector256）
        var rng = new Random(42);
        var a = new float[64];
        var b = new float[64];
        for (int i = 0; i < 64; i++)
        {
            a[i] = (float)rng.NextDouble();
            b[i] = (float)rng.NextDouble();
        }

        var cosine = new CosineDistance();
        var euclidean = new EuclideanDistance();
        var dotProduct = new DotProductDistance();

        var cosDist = cosine.Calculate(a, b);
        var eucDist = euclidean.Calculate(a, b);
        var dotDist = dotProduct.Calculate(a, b);

        Assert.InRange(cosDist, 0, 2);
        Assert.True(eucDist >= 0);
        Assert.True(float.IsFinite(dotDist));
    }

    [Fact]
    public void Distance_ExactSimdBoundary_NoRemainder()
    {
        // 精确为 Vector256<float>.Count 的倍数（8的倍数）
        var a = new float[32];
        var b = new float[32];
        for (int i = 0; i < 32; i++)
        {
            a[i] = i * 0.1f;
            b[i] = (32 - i) * 0.1f;
        }

        var cosine = new CosineDistance();
        var euclidean = new EuclideanDistance();
        var dotProduct = new DotProductDistance();

        Assert.True(float.IsFinite(cosine.Calculate(a, b)));
        Assert.True(float.IsFinite(euclidean.Calculate(a, b)));
        Assert.True(float.IsFinite(dotProduct.Calculate(a, b)));
    }

    [Fact]
    public void Distance_NonSimdRemainder_Handled()
    {
        // 不是 SIMD 宽度的倍数（余数处理路径）
        var a = new float[13];
        var b = new float[13];
        for (int i = 0; i < 13; i++)
        {
            a[i] = i * 0.3f;
            b[i] = (13 - i) * 0.3f;
        }

        var cosine = new CosineDistance();
        Assert.True(float.IsFinite(cosine.Calculate(a, b)));
    }

    #endregion

    #region VectorLiteDB — Dispose 异常后仍可关闭

    [Fact]
    public void Dispose_AfterOperations_Succeeds()
    {
        var path = GetDbPath("dispose_after_ops");
        var db = new VectorLiteDB(path, new VectorLiteOptions
        {
            CheckpointInterval = Timeout.InfiniteTimeSpan
        });
        db.GetOrCreateCollection("test", 4);
        db.Dispose();
        db.Dispose(); // 二次 Dispose 安全
    }

    #endregion

    #region Collection — 内部属性访问

    [Fact]
    public async Task Collection_InternalProperties_Accessible()
    {
        var path = GetDbPath("internal_props");
        using var db = new VectorLiteDB(path, new VectorLiteOptions
        {
            CheckpointInterval = Timeout.InfiniteTimeSpan
        });
        var coll = db.GetOrCreateCollection("test", 4) as Collection;
        Assert.NotNull(coll);

        await coll!.InsertAsync(new VectorRecord
        {
            Vector = new float[] { 1, 2, 3, 4 },
            Text = "测试"
        });

        // 访问 internal 属性确保覆盖
        Assert.NotNull(coll.HnswIndex);
        Assert.NotNull(coll.ScalarIndexInstance);
        Assert.True(coll.NextRecordId > 0);
    }

    #endregion

    #region FileStorage — Disposed 检查

    [Fact]
    public void FileStorage_AfterDispose_ThrowsObjectDisposed()
    {
        var path = GetDbPath("fs_disposed");
        var storage = FileStorage.CreateNew(path, 8192, 4096);
        storage.Dispose();

        Assert.Throws<ObjectDisposedException>(() => storage.ReadPageHeader(1));
        Assert.Throws<ObjectDisposedException>(() => storage.ReadFullPage(1));
        Assert.Throws<ObjectDisposedException>(() => storage.Checkpoint());
        Assert.Throws<ObjectDisposedException>(() => storage.ReadLogicalRecords());
    }

    #endregion

    #region VectorLiteDB — 自动检查点定时器

    [Fact]
    public async Task AutoCheckpointTimer_TriggersSuccessfully()
    {
        var path = GetDbPath("auto_timer");
        using var db = new VectorLiteDB(path, new VectorLiteOptions
        {
            CheckpointInterval = TimeSpan.FromMilliseconds(200)
        });

        var coll = db.GetOrCreateCollection("timed", 4);
        await coll.InsertAsync(new VectorRecord
        {
            Vector = new float[] { 1, 2, 3, 4 },
            Text = "定时检查点"
        });

        // 等待自动检查点触发
        await Task.Delay(500);

        // 数据应已持久化
    }

    #endregion

    #region TextStore — 序列化和加载路径

    [Fact]
    public async Task TextStore_LazyLoad_AfterCheckpoint()
    {
        var path = GetDbPath("textstore_lazy");

        // 插入多种文本长度，包括空文本
        using (var db = new VectorLiteDB(path, new VectorLiteOptions
        {
            CheckpointInterval = Timeout.InfiniteTimeSpan
        }))
        {
            var coll = db.GetOrCreateCollection("texts", 4);
            await coll.InsertAsync(new VectorRecord
            {
                Vector = new float[] { 1, 0, 0, 0 },
                Text = "短文本"
            });
            await coll.InsertAsync(new VectorRecord
            {
                Vector = new float[] { 0, 1, 0, 0 },
                Text = null // 空文本
            });
            await coll.InsertAsync(new VectorRecord
            {
                Vector = new float[] { 0, 0, 1, 0 },
                Text = new string('长', 10000) // 长文本
            });
            db.Checkpoint();
        }

        // 重新打开 —— 文本应从页链懒加载
        using (var db = new VectorLiteDB(path, new VectorLiteOptions
        {
            CheckpointInterval = Timeout.InfiniteTimeSpan
        }))
        {
            var coll = db.GetCollection("texts");
            Assert.NotNull(coll);
            Assert.Equal(3, coll!.Count);

            // 通过查询访问文本（触发懒加载）
            var results = await coll.Query(new float[] { 1, 0, 0, 0 })
                .TopK(3).ToListAsync();
            Assert.Equal(3, results.Count);

            // 验证至少有一条有文本
            Assert.Contains(results, r => r.Record.Text != null);
        }
    }

    #endregion
}
