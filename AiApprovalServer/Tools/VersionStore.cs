using System.Text;
using AiApproval.Core;
using AiApproval.Security;

namespace AiApproval.Tools;

//版本快照与回退
public sealed class VersionStore
{
    private readonly JsonStore _store;
    private readonly AppConfig _cfg;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public VersionStore(JsonStore store, AppConfig cfg)
    {
        _store = store;
        _cfg = cfg;
    }

    //记录一次可回退的执行
    public async Task<VersionRecord> RecordAsync(string taskId, string userId, string userName, string kind,
        string operation, string target, byte[]? previousBytes, byte[]? newBytes,
        string undoLogJson, string compensateAction = "")
    {
        var record = new VersionRecord
        {
            Id = "v_" + Crypto.RandomHex(8),
            TaskId = taskId,
            UserId = userId,
            UserName = userName,
            Kind = kind,
            Operation = operation,
            Target = target,
            PreviousExisted = previousBytes is not null,
            PreviousSize = previousBytes?.Length ?? 0,
            NewSize = newBytes?.Length ?? 0,
            UndoLogJson = undoLogJson,
            CompensateAction = compensateAction,
            CreatedAt = DateTime.UtcNow,
        };

        if (previousBytes is not null)
        {
            record.PreviousHash = Crypto.Sha256Hex(previousBytes);
            record.PreviousBlob = record.PreviousHash + ".bin";
            await WriteBlobIfMissingAsync(record.PreviousBlob, previousBytes).ConfigureAwait(false);
        }

        if (newBytes is not null)
            record.NewHash = Crypto.Sha256Hex(newBytes);

        //生成差异文本
        var oldText = AsText(previousBytes);
        var newText = AsText(newBytes);
        if (oldText is null || newText is null)
        {
            record.DiffText = previousBytes is null && newBytes is null
                ? "（无内容变化）"
                : $"二进制或非 UTF-8 内容，未生成逐行对比。旧 {record.PreviousSize} 字节 → 新 {record.NewSize} 字节。";
        }
        else
        {
            var (diff, added, removed) = LineDiff.Compute(oldText, newText);
            record.DiffText = diff;
            record.AddedLines = added;
            record.RemovedLines = removed;
        }

        await _store.MutateAsync<VersionRecord, bool>(StoreNames.Versions, list =>
        {
            list.Add(record);
            return true;
        }).ConfigureAwait(false);

        return record;
    }

    //内容寻址写快照
    private async Task WriteBlobIfMissingAsync(string blobName, byte[] content)
    {
        var relative = StoreNames.Blobs + "/" + blobName;
        var existing = await _store.ReadBlobAsync(relative).ConfigureAwait(false);
        if (existing is not null) return;

        await _lock.WaitAsync().ConfigureAwait(false);
        try
        {
            existing = await _store.ReadBlobAsync(relative).ConfigureAwait(false);
            if (existing is null)
                await _store.WriteBlobAsync(relative, content).ConfigureAwait(false);
        }
        finally { _lock.Release(); }
    }

    //读快照文件
    public async Task<byte[]?> ReadBlobAsync(string? blobName)
    {
        //校验文件名
        if (string.IsNullOrWhiteSpace(blobName)) return null;
        if (blobName.Contains("..") || blobName.Contains('/') || blobName.Contains('\\')) return null;
        return await _store.ReadBlobAsync(StoreNames.Blobs + "/" + blobName).ConfigureAwait(false);
    }

    //列版本记录
    public async Task<List<VersionRecord>> ListAsync(string? userId = null, int limit = 100)
    {
        var all = await _store.ReadListAsync<VersionRecord>(StoreNames.Versions).ConfigureAwait(false);
        IEnumerable<VersionRecord> query = all.OrderByDescending(v => v.CreatedAt);
        if (!string.IsNullOrWhiteSpace(userId))
            query = query.Where(v => v.UserId == userId);
        return query.Take(Math.Clamp(limit, 1, 500)).ToList();
    }

    //按 id 取版本
    public async Task<VersionRecord?> GetAsync(string id)
    {
        var all = await _store.ReadListAsync<VersionRecord>(StoreNames.Versions).ConfigureAwait(false);
        return all.FirstOrDefault(v => v.Id == id);
    }

    //更新版本记录
    public async Task<bool> UpdateAsync(string id, Action<VersionRecord> mutate)
        => await _store.MutateAsync<VersionRecord, bool>(StoreNames.Versions, list =>
        {
            var target = list.FirstOrDefault(v => v.Id == id);
            if (target is null) return false;
            mutate(target);
            return true;
        }).ConfigureAwait(false);

    //统计版本条数
    public async Task<int> CountAsync()
        => (await _store.ReadListAsync<VersionRecord>(StoreNames.Versions).ConfigureAwait(false)).Count;

    //按 UTF-8 解码文本
    private static string? AsText(byte[]? bytes)
    {
        if (bytes is null) return "";
        if (bytes.Length == 0) return "";
        if (bytes.Contains((byte)0)) return null;

        try
        {
            return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes);
        }
        catch
        {
            return null;
        }
    }
}
