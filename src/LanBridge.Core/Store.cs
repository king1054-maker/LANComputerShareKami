using System.Security.Cryptography;

namespace LanBridge.Core;

public sealed class Store : IDisposable
{
    public string Root { get; }
    public string StateRoot { get; }
    public string Trash => System.IO.Path.Combine(StateRoot, "trash");
    private readonly string journal;
    private readonly SemaphoreSlim gate = new(1);
    private readonly Identity identity;
    private readonly FileStream storeLock;
    public event Action<TransferProgress>? Progress;
    // Test-only crash injection at the filesystem/journal boundary.
    public Action? AfterFilesystemCommit { get; set; }

    public Store(string root, Identity identity)
    {
        this.identity = identity;
        Root = System.IO.Path.GetFullPath(root).TrimEnd(System.IO.Path.DirectorySeparatorChar);
        if (System.IO.Path.GetPathRoot(Root) == Root || System.IO.Path.GetDirectoryName(Root) == null)
            throw new IOException("请共享一个文件夹，不要共享整个磁盘。");
        Directory.CreateDirectory(Root);
        FileSystem.NoLinks(Root);
        // Staging is a SIBLING of the shared directory, ensuring same-volume atomic rename.
        StateRoot = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(Root)!, ".LanBridge-" + JsonFiles.Digest(OperatingSystem.IsWindows() ? Root.ToUpperInvariant() : Root)[..12]);
        Directory.CreateDirectory(StateRoot);
        FileSystem.NoLinks(StateRoot);
        storeLock = new FileStream(System.IO.Path.Combine(StateRoot, "store.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        if (OperatingSystem.IsWindows()) File.SetAttributes(StateRoot, FileAttributes.Directory | FileAttributes.Hidden);
        journal = System.IO.Path.Combine(StateRoot, "journal");
        Directory.CreateDirectory(journal); Directory.CreateDirectory(Trash);
        foreach (var p in Directory.EnumerateFiles(StateRoot, "*.part")) File.Delete(p);
        foreach (var p in Directory.EnumerateFiles(StateRoot, "*.download")) File.Delete(p);
        foreach (var p in Directory.EnumerateFiles(journal, "*.json"))
        {
            var r = JsonFiles.Read<OperationRecord>(p);
            if (r.State == "prepared") Recover(r, p);
        }
    }
    public string Resolve(string relative, bool allowRoot = true)
    {
        Names.Relative(relative);
        if (!allowRoot && relative.Length == 0) throw new IOException("不能操作共享根目录。");
        var path = System.IO.Path.GetFullPath(System.IO.Path.Combine(Root, relative.Replace('/', System.IO.Path.DirectorySeparatorChar)));
        if (path != Root && !path.StartsWith(Root + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new IOException("路径超出共享范围。");
        FileSystem.NoLinks(path);
        return path;
    }
    public async Task<FileEntry[]> ListAsync(string relative, CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try
        {
            var full = Resolve(relative);
            using var guard = new PathGuard(full);
            var entries = new List<FileEntry>();
            foreach (var path in Directory.EnumerateFileSystemEntries(full))
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) continue;
                    var name = System.IO.Path.GetFileName(path);
                    try { Names.Leaf(name); } catch (IOException ex) { throw new IOException("目录含当前版本不支持的名称：" + name, ex); }
                    var dir = Directory.Exists(path);
                    entries.Add(new(name, Names.Join(relative, name), dir, dir ? 0 : new FileInfo(path).Length,
                        FileSystem.Version(path), File.GetLastWriteTimeUtc(path), FileSystem.StableId(path)));
                    if (entries.Count > 20_000) throw new IOException("单个目录超过 20000 项，请拆分子目录。");
                }
                catch (FileNotFoundException) { }
                catch (DirectoryNotFoundException) { }
            }
            return entries.OrderByDescending(x => x.IsDirectory).ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToArray();
        }
        finally { gate.Release(); }
    }
    public async Task<Reply> ListWithIdentityAsync(string relative, CancellationToken ct)
    {
        var full = Resolve(relative);
        using var guard = new PathGuard(full);
        return new Reply { Entries = await ListAsync(relative, ct), Version = FileSystem.StableId(full) };
    }
    public static string NewOperationId() => Guid.NewGuid().ToString("N");
    private string RecordPath(string actor, string id)
    {
        if (!Guid.TryParseExact(id, "N", out _)) throw new IOException("操作 ID 无效。");
        return System.IO.Path.Combine(journal, JsonFiles.Digest(actor)[..16] + "-" + id + ".json");
    }
    private Reply? Existing(string path, Request request)
    {
        if (!File.Exists(path)) return null;
        var record = JsonFiles.Read<OperationRecord>(path);
        if (record.RequestHash != JsonFiles.Digest(request)) throw new IOException("同一操作 ID 不能用于不同请求。");
        if (record.State == "prepared") Recover(record, path);
        if (record.State == "ambiguous") throw new IOException("上次操作的完成状态无法安全确认，请刷新目录核对；不会自动再次执行。");
        return record.Result;
    }
    private void Recover(OperationRecord record, string recordPath)
    {
        if (FileSystem.Exists(record.Target) && FileSystem.StableId(record.Target) == record.Identity)
            record.State = "done";
        else record.State = "ambiguous";
        JsonFiles.Write(recordPath, record);
    }
    private string FreeName(string parent, string name)
    {
        Names.Leaf(name);
        var stem = System.IO.Path.GetFileNameWithoutExtension(name);
        var ext = System.IO.Path.GetExtension(name);
        for (var i = 0; i < 100_000; i++)
        {
            var candidate = i == 0 ? name : $"{stem} ({i + 1}){ext}";
            if (!FileSystem.Exists(System.IO.Path.Combine(parent, candidate))) return candidate;
        }
        throw new IOException("同名文件过多。");
    }
    public async Task<Reply> MutateAsync(Request request, string actor, CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try
        {
            var rp = RecordPath(actor, request.OperationId);
            var existing = Existing(rp, request); if (existing != null) return existing;
            if (request.Kind == "mkdir")
            {
                var parent = Resolve(request.Path); using var pg = new PathGuard(parent);
                CheckParent(parent, request.ParentIdentity);
                var name = FreeName(parent, request.Name);
                // Prepare a private directory, then atomically rename; crash recovery is identical to upload.
                var stage = System.IO.Path.Combine(StateRoot, request.OperationId + ".dir");
                if (Directory.Exists(stage)) Directory.Delete(stage, true);
                Directory.CreateDirectory(stage);
                var rel = Names.Join(request.Path, name);
                return Commit(request, rp, stage, Resolve(rel), new Reply { Path = rel, Version = FileSystem.StableId(stage) });
            }
            var source = Resolve(request.Path, false);
            using var guard = new PathGuard(System.IO.Path.GetDirectoryName(source)!);
            using var handle = OperatingSystem.IsWindows() ? FileSystem.OpenHandle(source, true) : null;
            if (string.IsNullOrEmpty(request.Version) || FileSystem.Version(source, handle) != request.Version)
                throw new IOException("文件已变化，请刷新后重新操作。旧请求不会作用于新文件。");
            string target; string targetRel;
            if (request.Kind == "delete")
            {
                target = System.IO.Path.Combine(Trash, System.IO.Path.GetFileNameWithoutExtension(rp) + "__" + System.IO.Path.GetFileName(source));
                targetRel = request.Path;
            }
            else if (request.Kind == "rename")
            {
                Names.Leaf(request.Name);
                var parent = request.Path.Contains('/') ? request.Path[..request.Path.LastIndexOf('/')] : "";
                targetRel = Names.Join(parent, request.Name); target = Resolve(targetRel);
                if (source.Equals(target, StringComparison.OrdinalIgnoreCase)) throw new IOException("新名称与旧名称相同（仅大小写变化暂不支持）。");
                if (FileSystem.Exists(target)) throw new IOException("目标名称已存在，不会覆盖。");
            }
            else throw new IOException("未知操作。");
            return Commit(request, rp, source, target, new Reply { Path = targetRel }, handle);
        }
        finally { gate.Release(); }
    }
    private Reply Commit(Request request, string recordPath, string source, string target, Reply result,
        Microsoft.Win32.SafeHandles.SafeFileHandle? handle = null)
    {
        var record = new OperationRecord { Id = request.OperationId, RequestHash = JsonFiles.Digest(request), Kind = request.Kind,
            Source = source, Target = target, Identity = FileSystem.StableId(source, handle), Result = result };
        JsonFiles.Write(recordPath, record);
        FileSystem.Move(source, target, handle);
        AfterFilesystemCommit?.Invoke();
        record.State = "done"; JsonFiles.Write(recordPath, record);
        identity.Log("commit", new { request.OperationId, request.Kind, request.Path, target = result.Path });
        return result;
    }
    private static void CheckParent(string path, string expected)
    {
        if (expected.Length == 0 || FileSystem.StableId(path) != expected) throw new IOException("目标目录已变化，请重新选择接收位置。");
    }
    public TrashItem[] ListTrash()
    {
        var items = new List<TrashItem>();
        foreach (var file in Directory.EnumerateFiles(journal, "*.json"))
        {
            var record = JsonFiles.Read<OperationRecord>(file);
            if (record.Kind == "delete" && FileSystem.Exists(record.Target) && FileSystem.StableId(record.Target) == record.Identity)
                items.Add(new(System.IO.Path.GetFileName(file), record.Result.Path, File.GetLastWriteTimeUtc(file)));
        }
        return items.OrderByDescending(x => x.DeletedUtc).ToArray();
    }
    public async Task<string> RestoreAsync(TrashItem item, CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try
        {
            var name = Names.Leaf(item.RecordName);
            var record = JsonFiles.Read<OperationRecord>(System.IO.Path.Combine(journal, name));
            if (record.Kind != "delete" || !record.Target.StartsWith(Trash + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new IOException("回收记录无效。");
            FileSystem.NoLinks(record.Target);
            using var handle = OperatingSystem.IsWindows() ? FileSystem.OpenHandle(record.Target, true) : null;
            if (FileSystem.StableId(record.Target, handle) != record.Identity) throw new IOException("回收内容已变化，不能自动恢复。");
            var original = Resolve(record.Result.Path, false);
            var parent = System.IO.Path.GetDirectoryName(original)!;
            using var guard = new PathGuard(parent);
            var restoredName = FreeName(parent, System.IO.Path.GetFileName(original));
            var target = System.IO.Path.Combine(parent, restoredName);
            var relative = System.IO.Path.GetRelativePath(Root, target).Replace('\\', '/');
            var request = new Request { Kind = "restore", Path = record.Result.Path, Name = restoredName, OperationId = NewOperationId() };
            Commit(request, RecordPath(identity.Id, request.OperationId), record.Target, target, new Reply { Path = relative }, handle);
            return relative;
        }
        finally { gate.Release(); }
    }

    public async Task ReceiveAsync(Request request, string actor, Stream wire, CancellationToken ct, Func<bool>? commitAllowed = null)
    {
        // One incoming commit at a time. Stream payload is received outside the metadata gate.
        var rp = RecordPath(actor, request.OperationId);
        if (request.Size < 0 || request.Size > 8L * 1024 * 1024 * 1024 * 1024 || request.Hash.Length != 64 || !request.Hash.All(Uri.IsHexDigit))
            throw new IOException("文件长度或校验值无效。");
        Names.Leaf(request.Name); Resolve(request.Path);
        await gate.WaitAsync(ct);
        try { var done = Existing(rp, request); if (done != null) { await Wire.WriteAsync(wire, done, ct); return; } }
        finally { gate.Release(); }
        var stage = System.IO.Path.Combine(StateRoot, System.IO.Path.GetFileNameWithoutExtension(rp) + ".part");
        // FileShare.None prevents simultaneous duplicate streams for the same operation.
        try
        {
            await using (var output = new FileStream(stage, FileMode.Create, FileAccess.Write, FileShare.None, Wire.BufferSize, true))
            {
                await Wire.WriteAsync(wire, new Reply { Status = "ready" }, ct);
                Progress?.Invoke(new(request.OperationId, request.Name, "接收", 0, request.Size, "正在接收"));
                var actual = await Wire.CopyHashAsync(wire, output, request.Size,
                    n => Progress?.Invoke(new(request.OperationId, request.Name, "接收", n, request.Size, "正在接收")), ct);
                if (!actual.Equals(request.Hash, StringComparison.OrdinalIgnoreCase)) throw new IOException("SHA-256 校验失败，文件未提交。");
                await output.FlushAsync(ct); output.Flush(true);
            }
            await gate.WaitAsync(ct);
            Reply result;
            try
            {
                var parent = Resolve(request.Path); using var guard = new PathGuard(parent);
                CheckParent(parent, request.ParentIdentity);
                if (commitAllowed != null && !commitAllowed()) throw new IOException("设备授权已撤销，文件未提交。");
                var name = FreeName(parent, request.Name); var rel = Names.Join(request.Path, name);
                result = Commit(request, rp, stage, Resolve(rel), new Reply { Path = rel, Hash = request.Hash, Size = request.Size });
            }
            finally { gate.Release(); }
            Progress?.Invoke(new(request.OperationId, request.Name, "接收", request.Size, request.Size, "完成"));
            await Wire.WriteAsync(wire, result, ct);
        }
        catch
        {
            Progress?.Invoke(new(request.OperationId, request.Name, "接收", 0, request.Size, "中断 / 失败"));
            throw;
        }
        finally { try { File.Delete(stage); } catch (IOException) { } }
    }

    public async Task SendAsync(Request request, Stream wire, CancellationToken ct, Action? payloadStarting = null)
    {
        var full = Resolve(request.Path, false);
        using var guard = new PathGuard(System.IO.Path.GetDirectoryName(full)!);
        await using var source = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read, Wire.BufferSize, true);
        var version = FileSystem.Version(full, source.SafeFileHandle);
        if (request.Version != version) throw new IOException("源文件已变化，请刷新后重试。");
        var hash = await FileSystem.HashAsync(source, ct); source.Position = 0;
        if (version != FileSystem.Version(full, source.SafeFileHandle)) throw new IOException("源文件正在变化。");
        await Wire.WriteAsync(wire, new Reply { Hash = hash, Size = source.Length, Version = version }, ct);
        payloadStarting?.Invoke();
        await Wire.CopyHashAsync(source, wire, source.Length, null, ct);
    }
    public void Dispose() => storeLock.Dispose();
}
