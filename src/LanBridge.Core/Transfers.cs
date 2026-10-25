using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace LanBridge.Core;

public sealed class ManifestItem
{
    public string Relative { get; set; } = "";
    public string SourcePath { get; set; } = "";
    public bool IsDirectory { get; set; }
    public long Size { get; set; }
    public string Version { get; set; } = "";
}
public sealed class TransferJob
{
    public string Id { get; set; } = Store.NewOperationId();
    public string SourcePeerId { get; set; } = ""; // empty means explicit local path supplied by the user
    public string SourcePath { get; set; } = "";
    public FileEntry? SourceEntry { get; set; }
    public string DestinationPeerId { get; set; } = "";
    public string DestinationPath { get; set; } = "";
    public string DestinationIdentity { get; set; } = "";
    public string Name { get; set; } = "";
    public string State { get; set; } = "等待中";
    public string Error { get; set; } = "";
    public List<ManifestItem>? Manifest { get; set; }
    public Dictionary<string, string> Results { get; set; } = new();
    public Dictionary<string, string> DirectoryIds { get; set; } = new();
}

public sealed class TransferQueue : IDisposable
{
    private readonly Node node;
    private readonly RemoteClient client;
    private readonly string jobsRoot;
    private readonly string cacheRoot;
    private readonly ConcurrentDictionary<string, CancellationTokenSource> running = new();
    private readonly SemaphoreSlim slots = new(2);
    public ConcurrentDictionary<string, TransferJob> Jobs { get; } = new();
    public event Action<TransferJob>? Changed;
    public event Action<TransferProgress>? Progress;
    public TransferQueue(Node node)
    {
        this.node = node; client = new(node.Identity);
        jobsRoot = System.IO.Path.Combine(node.Identity.DataDirectory, "jobs"); Directory.CreateDirectory(jobsRoot);
        cacheRoot = System.IO.Path.Combine(node.Identity.DataDirectory, "cache"); Directory.CreateDirectory(cacheRoot);
        foreach (var path in Directory.EnumerateFiles(jobsRoot, "*.json"))
        {
            var job = JsonFiles.Read<TransferJob>(path);
            if (job.State != "完成") { job.State = "已暂停"; job.Error = "上次运行中断；可用原操作 ID 重试。"; }
            Jobs[job.Id] = job;
        }
        foreach (var p in Directory.EnumerateFiles(cacheRoot)) File.Delete(p);
    }
    private void Save(TransferJob job)
    { JsonFiles.Write(System.IO.Path.Combine(jobsRoot, job.Id + ".json"), job); Changed?.Invoke(job); }
    public TransferJob Enqueue(string sourcePeerId, string path, FileEntry? source, string destinationPeerId,
        string destinationPath, string destinationIdentity)
    {
        if (sourcePeerId == destinationPeerId && source?.IsDirectory == true &&
            (destinationPath.Equals(source.Path, StringComparison.OrdinalIgnoreCase) || destinationPath.StartsWith(source.Path + "/", StringComparison.OrdinalIgnoreCase)))
            throw new IOException("不能把文件夹复制到它自己或它的子目录里。");
        var job = new TransferJob { SourcePeerId = sourcePeerId, SourcePath = path, SourceEntry = source,
            DestinationPeerId = destinationPeerId, DestinationPath = destinationPath, DestinationIdentity = destinationIdentity,
            Name = source?.Name ?? System.IO.Path.GetFileName(path.TrimEnd(System.IO.Path.DirectorySeparatorChar)) };
        Jobs[job.Id] = job; Save(job); Start(job); return job;
    }
    public void Retry(string id)
    {
        if (Jobs.TryGetValue(id, out var job) && job.State != "完成") Start(job);
    }
    public void Cancel(string id) { if (running.TryGetValue(id, out var ct)) ct.Cancel(); }
    private void Start(TransferJob job)
    {
        var ct = new CancellationTokenSource();
        if (!running.TryAdd(job.Id, ct)) { ct.Dispose(); return; }
        _ = Task.Run(async () =>
        {
            bool entered = false;
            try
            {
                job.State = "等待中"; job.Error = ""; Save(job);
                await slots.WaitAsync(ct.Token); entered = true;
                job.State = "正在准备"; Save(job);
                await RunAsync(job, ct.Token);
                job.State = "完成"; job.Error = ""; Save(job);
            }
            catch (OperationCanceledException) { job.State = "已暂停"; job.Error = "传输已停止；已提交的文件保留。若回执未收到，请重试确认。"; Save(job); }
            catch (Exception ex) { job.State = "失败"; job.Error = ex.Message; Save(job); }
            finally { if (entered) slots.Release(); running.TryRemove(job.Id, out _); ct.Dispose(); }
        });
    }
    private async Task RunAsync(TransferJob job, CancellationToken ct)
    {
        if (job.Manifest == null)
        {
            var items = new List<ManifestItem>();
            await WalkAsync(job, job.SourcePath, job.Name, job.SourceEntry, items, ct);
            job.Manifest = items; Save(job);
        }
        var destination = node.GetPeer(job.DestinationPeerId);
        var target = await client.CallAsync(destination, new Request { Kind = "list", Path = job.DestinationPath }, ct);
        if (target.Version != job.DestinationIdentity) throw new IOException("接收目录已被替换，请重新选择目录建立新任务。");
        job.State = "传输中"; Save(job);
        foreach (var item in job.Manifest)
        {
            ct.ThrowIfCancellationRequested();
            if (job.Results.ContainsKey(item.Relative)) continue;
            var slash = item.Relative.LastIndexOf('/');
            var parentKey = slash < 0 ? "" : item.Relative[..slash];
            var parent = parentKey.Length == 0 ? job.DestinationPath : job.Results[parentKey];
            var parentId = parentKey.Length == 0 ? job.DestinationIdentity : job.DirectoryIds[parentKey];
            var name = slash < 0 ? item.Relative : item.Relative[(slash + 1)..];
            var op = DeterministicId(job.Id, item.Relative);
            Reply result;
            if (item.IsDirectory)
            {
                result = await client.CallAsync(node.GetPeer(job.DestinationPeerId), new Request {
                    Kind = "mkdir", OperationId = op, Path = parent, Name = name, ParentIdentity = parentId }, ct);
                job.DirectoryIds[item.Relative] = result.Version;
            }
            else
            {
                string source = item.SourcePath; string? expected = item.Version; string? cache = null;
                try
                {
                    if (job.SourcePeerId.Length > 0 && job.SourcePeerId != node.Identity.Id)
                    {
                        cache = System.IO.Path.Combine(cacheRoot, op + ".cache");
                        var e = new FileEntry(name, item.SourcePath, false, item.Size, item.Version, DateTime.MinValue);
                        Progress?.Invoke(new(job.Id, name, "复制", 0, item.Size, "校验远程源文件"));
                        await client.DownloadAsync(node.GetPeer(job.SourcePeerId), e, cache,
                            (done, total) => Progress?.Invoke(new(job.Id, name, "复制", done, total, "正在获取源文件")), ct);
                        source = cache; expected = null;
                    }
                    else if (job.SourcePeerId == node.Identity.Id) source = node.Store.Resolve(item.SourcePath, false);
                    result = await client.UploadAsync(node.GetPeer(job.DestinationPeerId), source, parent, name, op, expected,
                        (done, total, state) => Progress?.Invoke(new(job.Id, name, "复制", done, total, state)), ct, parentId);
                }
                finally { if (cache != null) try { File.Delete(cache); } catch (IOException) { } }
            }
            job.Results[item.Relative] = result.Path; Save(job);
        }
    }
    private async Task WalkAsync(TransferJob job, string path, string relative, FileEntry? known, List<ManifestItem> items, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested(); Names.Relative(relative);
        if (items.Count >= 20_000 || relative.Count(c => c == '/') > 64) throw new IOException("文件夹过大或嵌套过深，请分批复制。");
        bool local = job.SourcePeerId.Length == 0;
        if (local)
        {
            FileSystem.NoLinks(path);
            var dir = Directory.Exists(path);
            items.Add(new() { Relative = relative, SourcePath = path, IsDirectory = dir,
                Size = dir ? 0 : new FileInfo(path).Length, Version = FileSystem.Version(path) });
            if (dir) foreach (var child in Directory.EnumerateFileSystemEntries(path).Order())
                    await WalkAsync(job, child, Names.Join(relative, System.IO.Path.GetFileName(child)), null, items, ct);
        }
        else
        {
            var e = known ?? throw new IOException("缺少源文件信息。");
            items.Add(new() { Relative = relative, SourcePath = e.Path, IsDirectory = e.IsDirectory, Size = e.Size, Version = e.Version });
            if (e.IsDirectory)
            {
                var reply = await client.CallAsync(node.GetPeer(job.SourcePeerId), new Request { Kind = "list", Path = e.Path }, ct);
                if (e.Identity.Length > 0 && reply.Version != e.Identity) throw new IOException("源文件夹已被替换，请重新选择。");
                foreach (var child in reply.Entries) await WalkAsync(job, child.Path, Names.Join(relative, child.Name), child, items, ct);
            }
        }
    }
    public static string DeterministicId(string job, string relative) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(job + "\n" + relative)))[..32].ToLowerInvariant();
    public void Dispose() { foreach (var ct in running.Values) ct.Cancel(); }
}
