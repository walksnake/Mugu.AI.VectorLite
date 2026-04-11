using System.Text;

namespace Mugu.AI.VectorLite.Storage;

/// <summary>
/// 文本存储：负责文本内容的缓冲管理和页链懒加载。
/// - 新写入的文本暂存在内存缓冲区（_pendingTexts）
/// - 检查点时序列化到页链
/// - 启动时仅加载文本索引，实际文本按需从页链读取
/// </summary>
internal sealed class TextStore
{
    // 内存缓冲区：尚未检查点的文本
    private readonly Dictionary<ulong, string?> _pendingTexts = new();

    // 页链懒加载索引：recordId → 在数据段中的字节偏移
    private Dictionary<ulong, long>? _textOffsets;

    // 页链信息（从磁盘加载时设置）
    private List<ulong>? _chainPageIds;
    private long _dataSectionOffset;
    private int _usablePageSize;
    private FileStorage? _storage;

    /// <summary>设置待写入文本（内存缓冲）</summary>
    internal void SetPending(ulong recordId, string? text)
    {
        _pendingTexts[recordId] = text;
    }

    /// <summary>移除记录文本</summary>
    internal void Remove(ulong recordId)
    {
        _pendingTexts.Remove(recordId);
        _textOffsets?.Remove(recordId);
    }

    /// <summary>获取指定记录的文本（优先内存缓冲，再从页链按需读取）</summary>
    internal string? GetText(ulong recordId)
    {
        // 优先从内存缓冲读取
        if (_pendingTexts.TryGetValue(recordId, out var text))
            return text;

        // 从页链懒加载
        if (_textOffsets == null || _storage == null || _chainPageIds == null)
            return null;

        if (!_textOffsets.TryGetValue(recordId, out var offset))
            return null;

        var absoluteOffset = _dataSectionOffset + offset;
        return ReadTextFromChain(absoluteOffset);
    }

    /// <summary>检查是否含有指定记录（内存或索引）</summary>
    internal bool Contains(ulong recordId)
    {
        return _pendingTexts.ContainsKey(recordId)
               || (_textOffsets?.ContainsKey(recordId) ?? false);
    }

    /// <summary>
    /// 序列化所有文本到字节数组（检查点时调用）。
    /// activeRecordIds 为当前活跃记录ID集合，确保只序列化有效记录。
    /// </summary>
    internal byte[] Serialize(IEnumerable<ulong> activeRecordIds)
    {
        // 合并 pending + 持久化文本
        var allTexts = new Dictionary<ulong, string?>();

        // 先加载已持久化的文本
        if (_textOffsets != null)
        {
            foreach (var (recordId, _) in _textOffsets)
            {
                if (!_pendingTexts.ContainsKey(recordId))
                {
                    allTexts[recordId] = GetText(recordId);
                }
            }
        }

        // 覆盖以 pending 数据
        foreach (var (recordId, text) in _pendingTexts)
        {
            allTexts[recordId] = text;
        }

        // 仅保留活跃记录
        var activeSet = new HashSet<ulong>(activeRecordIds);
        var entries = new List<(ulong RecordId, byte[] TextBytes)>();
        foreach (var recordId in activeSet)
        {
            if (allTexts.TryGetValue(recordId, out var text) && text != null)
            {
                entries.Add((recordId, Encoding.UTF8.GetBytes(text)));
            }
            else
            {
                entries.Add((recordId, []));
            }
        }

        // 构建二进制布局：
        // [Version:1][EntryCount:8][DataSectionOffset:8]
        // [TextIndex: recordId(8) + textOffset(8) per entry]
        // [TextData: textLength(4) + textBytes per entry]
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);

        // 版本号
        bw.Write((byte)1);

        bw.Write((ulong)entries.Count);
        // DataSectionOffset 占位（稍后回填）
        var dataSectionPos = ms.Position;
        bw.Write(0UL);

        // 写入索引区
        var textDataOffsets = new long[entries.Count];
        for (var i = 0; i < entries.Count; i++)
        {
            bw.Write(entries[i].RecordId);
            bw.Write(0L); // textOffset 占位
        }

        bw.Flush();
        var dataSectionOffset = ms.Position;

        // 回填 DataSectionOffset
        ms.Position = dataSectionPos;
        bw.Write((ulong)dataSectionOffset);
        ms.Position = dataSectionOffset;

        // 写入文本数据区，同时记录偏移
        for (var i = 0; i < entries.Count; i++)
        {
            textDataOffsets[i] = ms.Position - dataSectionOffset;
            bw.Write((uint)entries[i].TextBytes.Length);
            if (entries[i].TextBytes.Length > 0)
                bw.Write(entries[i].TextBytes);
        }

        bw.Flush();

        // 回填索引区的偏移值
        var indexStart = 17L; // 1(Version) + 8(EntryCount) + 8(DataSectionOffset)
        for (var i = 0; i < entries.Count; i++)
        {
            ms.Position = indexStart + i * 16 + 8; // 跳过 recordId(8)
            bw.Write(textDataOffsets[i]);
        }

        bw.Flush();
        return ms.ToArray();
    }

    /// <summary>
    /// 从存储加载文本索引（仅索引，不加载文本内容）。
    /// 启动时调用。
    /// </summary>
    internal static TextStore LoadIndex(FileStorage storage, ulong textStoreRootPage)
    {
        var store = new TextStore
        {
            _storage = storage,
            _usablePageSize = storage.UsablePageSize
        };

        if (textStoreRootPage == 0)
            return store;

        store._chainPageIds = PageChainIO.WalkChain(storage, textStoreRootPage);
        if (store._chainPageIds.Count == 0)
            return store;

        store.LoadIndexState(store._chainPageIds);
        return store;
    }

    /// <summary>检查点后清除内存缓冲（文本已写入页链）</summary>
    internal void ClearPending()
    {
        _pendingTexts.Clear();
    }

    /// <summary>重置页链状态（检查点后页链已变更，需重新建立映射）</summary>
    internal void ResetChainState(FileStorage storage, ulong newRootPage)
    {
        _storage = storage;
        _usablePageSize = storage.UsablePageSize;

        if (newRootPage == 0)
        {
            _chainPageIds = null;
            _textOffsets = null;
            _dataSectionOffset = 0;
            return;
        }

        _chainPageIds = PageChainIO.WalkChain(storage, newRootPage);
        LoadIndexState(_chainPageIds);
    }

    /// <summary>从已知页链列表中加载文本索引状态（版本号验证 + 头部 + 索引区）</summary>
    private void LoadIndexState(List<ulong> chainPageIds)
    {
        // 读取版本号
        var versionBuf = new byte[1];
        PageChainIO.ReadAt(_storage!, chainPageIds, 0, versionBuf);
        if (versionBuf[0] != 1)
            throw new StorageException($"不支持的 TextStore 序列化版本: {versionBuf[0]}（仅支持 v1）");

        // 读取头部：EntryCount + DataSectionOffset
        var headerBuf = new byte[16];
        PageChainIO.ReadAt(_storage!, chainPageIds, 1, headerBuf);

        var entryCount = BitConverter.ToUInt64(headerBuf, 0);
        const ulong MaxEntryCount = 100_000_000;
        if (entryCount > MaxEntryCount)
            throw new StorageException($"TextStore 索引条目数超出上限: {entryCount} > {MaxEntryCount}");
        _dataSectionOffset = (long)BitConverter.ToUInt64(headerBuf, 8);

        // 读取索引区（每条 16 字节：recordId + textOffset）
        _textOffsets = new Dictionary<ulong, long>((int)Math.Min(entryCount, 1_000_000));
        var indexBuf = new byte[16];
        for (ulong i = 0; i < entryCount; i++)
        {
            PageChainIO.ReadAt(_storage!, chainPageIds, 17 + (long)i * 16, indexBuf);
            var recordId = BitConverter.ToUInt64(indexBuf, 0);
            var textOffset = BitConverter.ToInt64(indexBuf, 8);
            _textOffsets[recordId] = textOffset;
        }
    }

    /// <summary>从页链读取指定偏移处的文本</summary>
    private string? ReadTextFromChain(long absoluteOffset)
    {
        if (_chainPageIds == null || _storage == null)
            return null;

        // 先读取 TextLength (4 字节)
        var lengthBuf = new byte[4];
        PageChainIO.ReadAt(_storage, _chainPageIds, absoluteOffset, lengthBuf);
        var textLength = BitConverter.ToUInt32(lengthBuf, 0);

        if (textLength == 0)
            return null;

        const uint MaxTextLength = 100 * 1024 * 1024; // 100MB 上限
        if (textLength > MaxTextLength)
            throw new StorageException($"文本长度超出上限: {textLength} > {MaxTextLength}");

        // 读取文本内容
        var textBuf = new byte[textLength];
        PageChainIO.ReadAt(_storage, _chainPageIds, absoluteOffset + 4, textBuf);
        return Encoding.UTF8.GetString(textBuf);
    }
}
