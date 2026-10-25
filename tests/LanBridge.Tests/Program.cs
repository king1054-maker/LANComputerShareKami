using System.Security.Cryptography;
using LanBridge.Core;

var home = Path.Combine(Path.GetTempPath(), "LanBridge-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(home);
int passed = 0;
async Task Test(string name, Func<Task> test)
{
    try { await test(); passed++; Console.WriteLine("PASS " + name); }
    catch (Exception ex) { Console.Error.WriteLine("FAIL " + name + "\n" + ex); Environment.ExitCode = 1; throw; }
}
void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
async Task Reject(Func<Task> action)
{
    bool rejected = false;
    try { await action(); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.Authentication.AuthenticationException) { rejected = true; }
    Check(rejected, "Expected rejection");
}

using var a = new Identity(Path.Combine(home, "a-config"), "A");
using var b = new Identity(Path.Combine(home, "b-config"), "B");
using var c = new Identity(Path.Combine(home, "c-config"), "C");
using var sa = new Store(Path.Combine(home, "A"), a);
using var sb = new Store(Path.Combine(home, "B"), b);
using var sc = new Store(Path.Combine(home, "C"), c);
using var na = new Node(a, sa); using var nb = new Node(b, sb); using var nc = new Node(c, sc);
na.Start(0, false); nb.Start(0, false); nc.Start(0, false);
var ca = new RemoteClient(a); var cb = new RemoteClient(b); var cc = new RemoteClient(c);
nb.PairApproval = (_, _) => Task.FromResult(true); na.PairApproval = (_, _) => Task.FromResult(true); nc.PairApproval = (_, _) => Task.FromResult(true);
na.Peers[b.Id] = nb.Self; na.Peers[c.Id] = nc.Self; nb.Peers[a.Id] = na.Self; nb.Peers[c.Id] = nc.Self; nc.Peers[a.Id] = na.Self;
string rootB = FileSystem.StableId(sb.Root); string rootA = FileSystem.StableId(sa.Root); string rootC = FileSystem.StableId(sc.Root);
var source = Path.Combine(sa.Root, "movie.bin");
var payload = RandomNumberGenerator.GetBytes(1_200_000); await File.WriteAllBytesAsync(source, payload);
string uploadId = Store.NewOperationId();

await Test("unpaired devices cannot browse", () => Reject(async () => await ca.CallAsync(nb.Self, new Request { Kind = "list" })));
await Test("pairing persists mutual certificate identity", async () =>
{
    await ca.PairAsync(nb.Self, default); Check(a.IsTrusted(b.Id) && b.IsTrusted(a.Id), "mutual trust");
    Check(JsonFiles.Read<Dictionary<string, string>>(Path.Combine(a.DataDirectory, "trusted.json")).ContainsKey(b.Id), "saved trust");
});
await Test("listing has no filesystem side effects", async () =>
{
    var before = Directory.GetFiles(sb.Root, "*", SearchOption.AllDirectories);
    for (int i = 0; i < 5; i++) await ca.CallAsync(nb.Self, new Request { Kind = "list" });
    Check(before.SequenceEqual(Directory.GetFiles(sb.Root, "*", SearchOption.AllDirectories)), "list modified files");
});
await Test("TLS file upload checks hash and commits exact bytes", async () =>
{
    var reply = await ca.UploadAsync(nb.Self, source, "", "movie.bin", uploadId, FileSystem.Version(source), null, default, rootB);
    Check(reply.Path == "movie.bin", "unexpected name"); Check(File.ReadAllBytes(Path.Combine(sb.Root, reply.Path)).SequenceEqual(payload), "content mismatch");
});
await Test("lost upload acknowledgement retry does not duplicate", async () =>
{
    var result = await ca.UploadAsync(nb.Self, source, "", "movie.bin", uploadId, FileSystem.Version(source), null, default, rootB);
    Check(result.Path == "movie.bin" && Directory.GetFiles(sb.Root).Length == 1, "duplicate upload");
});
await Test("reused operation ID with different content is rejected", async () =>
{
    var other = Path.Combine(sa.Root, "other.bin"); File.WriteAllText(other, "different");
    await Reject(async () => await ca.UploadAsync(nb.Self, other, "", "movie.bin", uploadId, null, null, default, rootB));
});
await Test("new same-name copy preserves both versions", async () =>
{
    var result = await ca.UploadAsync(nb.Self, source, "", "movie.bin", Store.NewOperationId(), null, null, default, rootB);
    Check(result.Path == "movie (2).bin" && Directory.GetFiles(sb.Root).Length == 2, "keep both failed");
});
await Test("remote download validates exact hash", async () =>
{
    var entry = (await ca.CallAsync(nb.Self, new Request { Kind = "list" })).Entries.Single(e => e.Name == "movie.bin");
    var local = Path.Combine(home, "download.bin"); await ca.DownloadAsync(nb.Self, entry, local, null, default);
    Check(File.ReadAllBytes(local).SequenceEqual(payload), "download mismatch");
});
Request? deletion = null;
await Test("deletion moves original to owner trash", async () =>
{
    var e = (await sb.ListAsync("")).Single(e => e.Name == "movie.bin");
    deletion = new Request { Kind = "delete", OperationId = Store.NewOperationId(), Path = e.Path, Version = e.Version };
    await ca.CallAsync(nb.Self, deletion);
    Check(!File.Exists(Path.Combine(sb.Root, "movie.bin")) && Directory.GetFiles(sb.Trash).Length == 1, "delete failed");
});
await Test("immediate same-name recreation survives old delete replay", async () =>
{
    var path = Path.Combine(sb.Root, "movie.bin"); File.WriteAllText(path, "NEW");
    await ca.CallAsync(nb.Self, deletion!);
    Check(File.ReadAllText(path) == "NEW", "old delete removed recreation");
    var stale = new Request { Kind = "delete", OperationId = Store.NewOperationId(), Path = deletion!.Path, Version = deletion.Version };
    await Reject(async () => await ca.CallAsync(nb.Self, stale));
    Check(File.ReadAllText(path) == "NEW", "stale operation removed recreation");
});
await Test("old upload replay cannot overwrite recreated file", async () =>
{
    await ca.UploadAsync(nb.Self, source, "", "movie.bin", uploadId, null, null, default, rootB);
    Check(File.ReadAllText(Path.Combine(sb.Root, "movie.bin")) == "NEW", "old content resurrected");
});
await Test("same-size edit with restored mtime invalidates version", async () =>
{
    var path = Path.Combine(sb.Root, "movie.bin"); var time = File.GetLastWriteTimeUtc(path);
    var e = (await sb.ListAsync("")).Single(e => e.Name == "movie.bin");
    File.WriteAllText(path, "XYZ"); File.SetLastWriteTimeUtc(path, time);
    await Reject(async () => await ca.CallAsync(nb.Self, new Request { Kind = "delete", Path = e.Path, Version = e.Version, OperationId = Store.NewOperationId() }));
});
await Test("rename is conditional and idempotent", async () =>
{
    var e = (await sb.ListAsync("")).Single(e => e.Name == "movie.bin");
    var r = new Request { Kind = "rename", Path = e.Path, Name = "renamed.bin", Version = e.Version, OperationId = Store.NewOperationId() };
    var first = await ca.CallAsync(nb.Self, r); var replay = await ca.CallAsync(nb.Self, r);
    var newExists = File.Exists(Path.Combine(sb.Root, "renamed.bin"));
    var oldExists = File.Exists(Path.Combine(sb.Root, "movie.bin"));
    Check(newExists && !oldExists, $"rename failed: newExists={newExists}, oldExists={oldExists}, first={first.Path}, replay={replay.Path}, entries=[{string.Join(", ", Directory.GetFileSystemEntries(sb.Root).Select(Path.GetFileName))}]");
});
await Test("native moves preserve exact destination names across path lengths", () =>
{
    var probe = Path.Combine(home, "rename-probe"); Directory.CreateDirectory(probe);
    var current = Path.Combine(probe, "start.txt"); File.WriteAllText(current, "exact bytes");
    for (int i = 0; i < 48; i++)
    {
        var next = Path.Combine(probe, "中文-" + new string('x', i % 31 + 1) + i + ".txt");
        FileSystem.Move(current, next);
        Check(File.Exists(next) && !File.Exists(current), "Native rename produced an unexpected filename: " + string.Join(", ", Directory.GetFiles(probe)));
        Check(File.ReadAllText(next) == "exact bytes", "Native rename changed content");
        current = next;
    }
    var from = Path.Combine(probe, "folder"); var to = Path.Combine(probe, "文件夹-new");
    Directory.CreateDirectory(from); FileSystem.Move(from, to);
    Check(Directory.Exists(to) && !Directory.Exists(from), "Directory rename changed destination name");
    return Task.CompletedTask;
});
await Test("path traversal, ADS and reserved names rejected", async () =>
{
    foreach (var path in new[] { "../outside", "/etc", "C:/Users", "foo\\bar", "x:stream", "CON", "x/../y", "foo.", "LPT1.txt" })
        await Reject(async () => await ca.CallAsync(nb.Self, new Request { Kind = "list", Path = path }));
});
await Test("symlink directory cannot escape share", async () =>
{
    var link = Path.Combine(sb.Root, "escape");
    try { Directory.CreateSymbolicLink(link, home); }
    catch (UnauthorizedAccessException) { Console.WriteLine("  NOTE symlink creation requires Windows developer mode; manual check required."); return; }
    await Reject(async () => await ca.CallAsync(nb.Self, new Request { Kind = "list", Path = "escape" }));
    Directory.Delete(link);
});
await Test("mkdir lost acknowledgement is idempotent", async () =>
{
    var req = new Request { Kind = "mkdir", Path = "", ParentIdentity = rootB, Name = "folder", OperationId = Store.NewOperationId() };
    var first = await ca.CallAsync(nb.Self, req); var second = await ca.CallAsync(nb.Self, req);
    Check(first.Path == second.Path && first.Version == second.Version, "mkdir duplicate");
});
await Test("recreated target directory rejects old incoming task", async () =>
{
    var target = Path.Combine(sb.Root, "folder"); var oldId = FileSystem.StableId(target);
    Directory.Move(target, target + "-old"); Directory.CreateDirectory(target);
    await Reject(async () => await ca.UploadAsync(nb.Self, source, "folder", "movie.bin", Store.NewOperationId(), null, null, default, oldId));
    Check(!File.Exists(Path.Combine(target, "movie.bin")), "wrong directory received old task");
});
await Test("crash after filesystem rename recovers once", async () =>
{
    var path = Path.Combine(sb.Root, "crash.txt"); File.WriteAllText(path, "keep");
    var req = new Request { Kind = "delete", Path = "crash.txt", Version = FileSystem.Version(path), OperationId = Store.NewOperationId() };
    sb.AfterFilesystemCommit = () => throw new IOException("simulated process crash");
    await Reject(async () => await ca.CallAsync(nb.Self, req)); sb.AfterFilesystemCommit = null;
    File.WriteAllText(path, "replacement");
    await ca.CallAsync(nb.Self, req);
    Check(File.ReadAllText(path) == "replacement", "recovery deleted replacement");
});
await Test("missing recovery evidence fails closed", async () =>
{
    var path = Path.Combine(sb.Root, "uncertain.txt"); File.WriteAllText(path, "old");
    var req = new Request { Kind = "rename", Path = "uncertain.txt", Name = "uncertain-new.txt", Version = FileSystem.Version(path), OperationId = Store.NewOperationId() };
    sb.AfterFilesystemCommit = () => throw new IOException("simulated process crash");
    await Reject(async () => await ca.CallAsync(nb.Self, req)); sb.AfterFilesystemCommit = null;
    File.Delete(Path.Combine(sb.Root, "uncertain-new.txt")); File.WriteAllText(path, "new");
    await Reject(async () => await ca.CallAsync(nb.Self, req));
    Check(File.ReadAllText(path) == "new", "ambiguous operation repeated");
});
await Test("bad checksum never creates final file", async () =>
{
    var req = new Request { Kind = "upload", OperationId = Store.NewOperationId(), Name = "bad.bin", Size = 3, Hash = new string('0',64), ParentIdentity = rootB };
    using var duplex = new ScriptedDuplex([1,2,3]);
    await Reject(() => sb.ReceiveAsync(req, a.Id, duplex, default));
    Check(!File.Exists(Path.Combine(sb.Root, "bad.bin")), "corrupt file committed");
});
await Test("truncated transfer leaves no final or partial file", async () =>
{
    var req = new Request { Kind = "upload", OperationId = Store.NewOperationId(), Name = "truncated.bin", Size = 100, Hash = new string('0',64), ParentIdentity = rootB };
    using var duplex = new ScriptedDuplex([1,2]);
    await Reject(() => sb.ReceiveAsync(req, a.Id, duplex, default));
    Check(!File.Exists(Path.Combine(sb.Root, "truncated.bin")) && !Directory.EnumerateFiles(sb.StateRoot, "*.part").Any(), "incomplete transfer leaked");
});
await Test("certificate pin mismatch is rejected", async () =>
{
    await ca.PairAsync(nc.Self, default);
    var impostor = nc.Self with { Address = nb.Self.Address, Port = nb.Port };
    await Reject(async () => await ca.CallAsync(impostor, new Request { Kind = "list" }));
});
await Test("third device receives independent copy", async () =>
{
    var result = await ca.UploadAsync(nc.Self, source, "", "movie.bin", Store.NewOperationId(), null, null, default, rootC);
    Check(File.ReadAllBytes(Path.Combine(sc.Root,result.Path)).SequenceEqual(payload), "third peer mismatch");
});
await Test("reverse direction B to A works", async () =>
{
    var path = Path.Combine(sb.Root, "reverse.txt"); File.WriteAllText(path, "reverse");
    await cb.UploadAsync(na.Self, path, "", "reverse.txt", Store.NewOperationId(), null, null, default, rootA);
    Check(File.ReadAllText(Path.Combine(sa.Root, "reverse.txt")) == "reverse", "reverse failed");
});
await Test("folder queue, empty folder and unicode files", async () =>
{
    var folder = Path.Combine(home, "folder-source"); Directory.CreateDirectory(Path.Combine(folder,"empty"));
    File.WriteAllText(Path.Combine(folder,"电影字幕.txt"), "你好，世界");
    using var queue = new TransferQueue(na);
    var job = queue.Enqueue("", folder, null, b.Id, "", rootB);
    for (int i=0;i<300 && job.State != "完成" && job.State != "失败";i++) await Task.Delay(50);
    Check(job.State == "完成", job.State + ": " + job.Error);
    Check(Directory.Exists(Path.Combine(sb.Root,"folder-source","empty")), "empty folder lost");
    Check(File.ReadAllText(Path.Combine(sb.Root,"folder-source","电影字幕.txt")) == "你好，世界", "unicode lost");
});
await Test("concurrent same-name transfers keep both without overwrite", async () =>
{
    var p1 = Path.Combine(home, "p1.txt"); var p2 = Path.Combine(home,"p2.txt");
    File.WriteAllText(p1,"ONE"); File.WriteAllText(p2,"TWO");
    await Task.WhenAll(
        ca.UploadAsync(nb.Self,p1,"","parallel.txt",Store.NewOperationId(),null,null,default,rootB),
        ca.UploadAsync(nb.Self,p2,"","parallel.txt",Store.NewOperationId(),null,null,default,rootB));
    var contents = Directory.GetFiles(sb.Root,"parallel*.txt").Select(File.ReadAllText).Order().ToArray();
    Check(contents.SequenceEqual(new[]{"ONE","TWO"}),"concurrent data loss");
});
await Test("source changed after selection is not silently copied", async () =>
{
    var p = Path.Combine(home,"changed.txt"); File.WriteAllText(p,"first"); var version=FileSystem.Version(p); File.WriteAllText(p,"later");
    await Reject(async()=>await ca.UploadAsync(nb.Self,p,"","changed.txt",Store.NewOperationId(),version,null,default,rootB));
    Check(!File.Exists(Path.Combine(sb.Root,"changed.txt")),"changed source committed");
});
await Test("rapid same-size rewrites with restored mtime always invalidate version", () =>
{
    var p = Path.Combine(home, "rapid-edit.txt"); File.WriteAllText(p, "0000");
    var time = File.GetLastWriteTimeUtc(p);
    for (int i = 1; i <= 64; i++)
    {
        var before = FileSystem.Version(p);
        File.WriteAllText(p, i.ToString("D4")); File.SetLastWriteTimeUtc(p, time);
        Check(FileSystem.Version(p) != before, $"Undetected same-size rewrite {i}; waiting is not a correctness mechanism");
    }
    return Task.CompletedTask;
});
await Test("mtime inversion never chooses an older copy", async () =>
{
    var p=Path.Combine(home,"clock.txt"); File.WriteAllText(p,"PAST-TIMESTAMP-NEW-CONTENT"); File.SetLastWriteTimeUtc(p,DateTime.UtcNow.AddDays(-2));
    var old=Path.Combine(sb.Root,"clock.txt"); File.WriteAllText(old,"OLD"); File.SetLastWriteTimeUtc(old,DateTime.UtcNow.AddDays(2));
    var r=await ca.UploadAsync(nb.Self,p,"","clock.txt",Store.NewOperationId(),null,null,default,rootB);
    Check(File.ReadAllText(Path.Combine(sb.Root,r.Path))=="PAST-TIMESTAMP-NEW-CONTENT" && File.ReadAllText(old)=="OLD","mtime controlled resolution");
});
await Test("remote-to-local queued copy completes with correct content", async () =>
{
    using var queue=new TransferQueue(na);
    var entry=(await sb.ListAsync("")).Single(x=>x.Name=="reverse.txt");
    var job=queue.Enqueue(b.Id,entry.Path,entry,a.Id,"",rootA);
    for(int i=0;i<300&&job.State!="完成"&&job.State!="失败";i++)await Task.Delay(50);
    Check(job.State=="完成",job.Error);
    Check(File.ReadAllText(Path.Combine(sa.Root,job.Results[entry.Name]))=="reverse","remote queue mismatch");
});
await Test("process-restart recovery preserves recreated files", async () =>
{
    var root=Path.Combine(home,"restart"); Request req;
    using(var s=new Store(root,c))
    {
        var p=Path.Combine(root,"file.txt");File.WriteAllText(p,"OLD");
        req=new Request{Kind="delete",Path="file.txt",Version=FileSystem.Version(p),OperationId=Store.NewOperationId()};
        s.AfterFilesystemCommit=()=>throw new IOException("crash before durable acknowledgement");
        await Reject(async()=>await s.MutateAsync(req,a.Id));
        File.WriteAllText(p,"NEW");
    }
    using(var s=new Store(root,c))
    {
        await s.MutateAsync(req,a.Id);
        Check(File.ReadAllText(Path.Combine(root,"file.txt"))=="NEW","restart replay deleted replacement");
    }
});
await Test("second writer for same shared folder is blocked", async () =>
{
    await Reject(()=>{using var other=new Store(sb.Root,c); return Task.CompletedTask;});
});
await Test("revocation during receive blocks final commit", async () =>
{
    var bytes=new byte[]{1,2,3};var req=new Request{Kind="upload",OperationId=Store.NewOperationId(),Name="revoked.bin",Size=3,
        Hash=Convert.ToHexString(SHA256.HashData(bytes)),ParentIdentity=rootB};
    using var stream=new ScriptedDuplex(bytes);
    await Reject(()=>sb.ReceiveAsync(req,a.Id,stream,default,()=>false));
    Check(!File.Exists(Path.Combine(sb.Root,"revoked.bin")),"revoked transfer committed");
});
await Test("deleting original does not delete received independent copy", async () =>
{
    File.Delete(source); await ca.CallAsync(nb.Self, new Request { Kind = "list" });
    Check(File.Exists(Path.Combine(sc.Root,"movie.bin")), "unexpected mirrored deletion");
});
await Test("revoked trust blocks further operations", async () =>
{
    b.Revoke(a.Id); await Reject(async () => await ca.CallAsync(nb.Self, new Request { Kind = "list" }));
});
await Test("trash restore keeps recreated destination and restores original separately",async()=>
{
    var item=sb.ListTrash().Single(x=>x.OriginalPath=="movie.bin");
    File.WriteAllText(Path.Combine(sb.Root,"movie.bin"),"NEWEST");
    var restored=await sb.RestoreAsync(item);
    Check(restored!="movie.bin"&&File.ReadAllBytes(Path.Combine(sb.Root,restored)).SequenceEqual(payload),"restore failed");
    Check(File.ReadAllText(Path.Combine(sb.Root,"movie.bin"))=="NEWEST","restore overwrote new content");
});

Console.WriteLine($"\n{passed} tests passed. Runtime: {System.Runtime.InteropServices.RuntimeInformation.OSDescription}");
Console.WriteLine("Native Explorer dragging, Windows firewall and multi-PC discovery require the Windows acceptance checklist.");
na.Dispose(); nb.Dispose(); nc.Dispose();

sealed class ScriptedDuplex(byte[] input) : Stream
{
    private readonly MemoryStream read = new(input);
    public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException(); public override long Position { get => read.Position; set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count) => read.Read(buffer, offset, count);
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => read.ReadAsync(buffer, ct);
    public override void Write(byte[] buffer, int offset, int count) { }
    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default) => ValueTask.CompletedTask;
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}
