using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;

namespace LanBridge.Core;

public sealed class Node : IDisposable
{
    public const int DiscoveryPort = 47731;
    public const int DefaultPort = 47732;
    private static readonly IPAddress Group = IPAddress.Parse("239.255.42.99");
    public Identity Identity { get; }
    public Store Store { get; }
    public int Port { get; private set; }
    public Peer Self => new(Identity.Id, Identity.Name + "（本机）", "127.0.0.1", Port, DateTime.UtcNow);
    public ConcurrentDictionary<string, Peer> Peers { get; } = new();
    public Func<string, string, Task<bool>>? PairApproval { get; set; }
    public event Action? PeersChanged;
    public event Action<string>? Diagnostic;
    private readonly CancellationTokenSource stop = new();
    private readonly SemaphoreSlim clients = new(16);
    private readonly SemaphoreSlim pairing = new(1);
    private readonly ConcurrentDictionary<string, byte> activeOperations = new();
    private readonly ConcurrentDictionary<int, TcpClient> connections = new();
    private int connectionSequence;
    private TcpListener? listener;
    private UdpClient? udp;
    public Node(Identity identity, Store store) { Identity = identity; Store = store; }
    public void Start(int port = DefaultPort, bool discovery = true)
    {
        listener = new TcpListener(IPAddress.Any, port);
        try { listener.Start(); }
        catch (SocketException) when (port != 0) { listener = new TcpListener(IPAddress.Any, 0); listener.Start(); }
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        _ = AcceptLoopAsync();
        if (!discovery) return;
        try
        {
            udp = new UdpClient(AddressFamily.InterNetwork);
            udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            udp.Client.Bind(new IPEndPoint(IPAddress.Any, DiscoveryPort));
            udp.EnableBroadcast = true; udp.MulticastLoopback = true;
            var addresses = NetworkInterface.GetAllNetworkInterfaces().Where(n => n.OperationalStatus == OperationalStatus.Up)
                .SelectMany(n => n.GetIPProperties().UnicastAddresses).Select(a => a.Address)
                .Where(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a)).ToArray();
            foreach (var address in addresses)
            { try { udp.JoinMulticastGroup(Group, address); } catch (SocketException) { } }
            if (addresses.Length == 0) udp.JoinMulticastGroup(Group);
            _ = DiscoverLoopAsync(); _ = AdvertiseLoopAsync();
        }
        catch (Exception ex) { Diagnostic?.Invoke("自动发现暂不可用：" + ex.Message); }
    }
    private async Task AcceptLoopAsync()
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                var client = await listener!.AcceptTcpClientAsync(stop.Token);
                if (!await clients.WaitAsync(0, stop.Token)) { client.Dispose(); continue; }
                var id = Interlocked.Increment(ref connectionSequence); connections[id] = client;
                _ = Task.Run(async () =>
                { try { await ServeAsync(client); } finally { client.Dispose(); connections.TryRemove(id, out _); clients.Release(); } });
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException) { }
    }
    private async Task ServeAsync(TcpClient client)
    {
        using var ssl = new SslStream(client.GetStream(), false, (_, cert, _, _) => cert != null);
        string actor = ""; string activeKey = ""; bool payloadStarted = false;
        try
        {
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop.Token))
            {
                timeout.CancelAfter(TimeSpan.FromSeconds(15));
                await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions {
                    ServerCertificate = Identity.Certificate, ClientCertificateRequired = true,
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13, CertificateRevocationCheckMode = X509RevocationMode.NoCheck
                }, timeout.Token);
            }
            if (ssl.RemoteCertificate == null) throw new IOException("缺少设备证书。");
            actor = Identity.Fingerprint(ssl.RemoteCertificate);
            Request request;
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop.Token))
            { timeout.CancelAfter(TimeSpan.FromSeconds(15)); request = await Wire.ReadAsync<Request>(ssl, timeout.Token, 16_384); }
            if (request.Kind == "pair")
            {
                if (!Identity.IsTrusted(actor))
                {
                    if (PairApproval == null || !await pairing.WaitAsync(0, stop.Token)) throw new IOException("设备正在处理其他配对请求，请稍后重试。");
                    try
                    {
                        var name = request.DeviceName.Length > 80 ? request.DeviceName[..80] : request.DeviceName;
                        if (!await PairApproval(actor, name).WaitAsync(TimeSpan.FromSeconds(90), stop.Token)) throw new IOException("对方未同意配对。");
                        Identity.Trust(actor, name);
                    }
                    finally { pairing.Release(); }
                }
                await Wire.WriteAsync(ssl, new Reply(), stop.Token); return;
            }
            if (actor != Identity.Id && !Identity.IsTrusted(actor)) throw new IOException("尚未配对或授权已撤销。");
            if (request.Kind is "upload" or "delete" or "rename" or "mkdir")
            {
                activeKey = actor + ":" + request.OperationId;
                if (!activeOperations.TryAdd(activeKey, 0)) { activeKey = ""; throw new IOException("同一任务仍在执行，请稍后重试。"); }
            }
            switch (request.Kind)
            {
                case "list": await Wire.WriteAsync(ssl, await Store.ListWithIdentityAsync(request.Path, stop.Token), stop.Token); break;
                case "upload":
                    await Store.ReceiveAsync(request, actor, ssl, stop.Token, () => actor == Identity.Id || Identity.IsTrusted(actor)); break;
                case "download":
                    // After the file header, errors are represented by early EOF; never inject JSON into a file body.
                    await Store.SendAsync(request, ssl, stop.Token, () => payloadStarted = true); break;
                case "delete": case "rename": case "mkdir":
                    await Wire.WriteAsync(ssl, await Store.MutateAsync(request, actor, stop.Token), stop.Token); break;
                default: throw new IOException("不支持的请求。");
            }
        }
        catch (Exception ex)
        {
            try { Identity.Log("request-failed", new { actor, activeKey, error = ex.Message }); } catch (IOException) { }
            if (!payloadStarted)
                try { using var t = new CancellationTokenSource(TimeSpan.FromSeconds(3)); await Wire.WriteAsync(ssl, Reply.Fail(ex.Message), t.Token); } catch (Exception) { }
        }
        finally { if (activeKey.Length > 0) activeOperations.TryRemove(activeKey, out _); }
    }
    private async Task AdvertiseLoopAsync()
    {
        while (!stop.IsCancellationRequested)
        {
            var data = JsonSerializer.SerializeToUtf8Bytes(new { app = "LanBridge/1", id = Identity.Id, name = Identity.Name, port = Port });
            try
            {
                // Multicast plus each interface's directed broadcast. No internet rendezvous service.
                var endpoints = new List<IPAddress> { Group, IPAddress.Broadcast };
                foreach (var nic in NetworkInterface.GetAllNetworkInterfaces().Where(n => n.OperationalStatus == OperationalStatus.Up))
                foreach (var ip in nic.GetIPProperties().UnicastAddresses.Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a.Address)))
                {
                    var a = ip.Address.GetAddressBytes(); var m = ip.IPv4Mask.GetAddressBytes();
                    endpoints.Add(new IPAddress(a.Zip(m, (x, y) => (byte)(x | ~y)).ToArray()));
                }
                foreach (var endpoint in endpoints.Distinct())
                    try { await udp!.SendAsync(data, new IPEndPoint(endpoint, DiscoveryPort), stop.Token); } catch (SocketException) { }
                foreach (var peer in Peers.Values.Where(p => DateTime.UtcNow - p.SeenUtc > TimeSpan.FromSeconds(12)))
                    if (Peers.TryRemove(peer.Id, out _)) PeersChanged?.Invoke();
                await Task.Delay(2000, stop.Token);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) { Diagnostic?.Invoke(ex.Message); await Task.Delay(2000, CancellationToken.None); }
        }
    }
    private async Task DiscoverLoopAsync()
    {
        while (!stop.IsCancellationRequested)
        {
            try
            {
                var result = await udp!.ReceiveAsync(stop.Token);
                if (result.Buffer.Length > 2048) continue;
                using var json = JsonDocument.Parse(result.Buffer); var root = json.RootElement;
                if (root.GetProperty("app").GetString() != "LanBridge/1") continue;
                var id = root.GetProperty("id").GetString() ?? "";
                var name = root.GetProperty("name").GetString() ?? ""; var port = root.GetProperty("port").GetInt32();
                if (id == Identity.Id || id.Length != 64 || !id.All(Uri.IsHexDigit) || name.Length > 80 || port < 1 || port > 65535) continue;
                var changed = !Peers.TryGetValue(id, out var previous) || previous.Address != result.RemoteEndPoint.Address.ToString() || previous.Port != port;
                Peers[id] = new(id, name, result.RemoteEndPoint.Address.ToString(), port, DateTime.UtcNow);
                if (changed) PeersChanged?.Invoke();
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException) { return; }
            catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException or SocketException) { }
        }
    }
    public Peer GetPeer(string id) => id == Identity.Id ? Self : Peers.TryGetValue(id, out var peer) ? peer : throw new IOException("设备离线，等待对方重新上线后重试。");
    public void Dispose()
    {
        stop.Cancel(); listener?.Stop(); udp?.Dispose();
        foreach (var c in connections.Values) c.Dispose();
    }
}

public sealed class RemoteClient
{
    private readonly Identity identity;
    public RemoteClient(Identity identity) => this.identity = identity;
    private async Task<SslStream> ConnectAsync(Peer peer, bool pairing, CancellationToken ct)
    {
        if (!pairing && peer.Id != identity.Id && !identity.IsTrusted(peer.Id)) throw new IOException("请先连接并配对该设备。");
        var tcp = new TcpClient { NoDelay = true };
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(10));
            await tcp.ConnectAsync(IPAddress.Parse(peer.Address), peer.Port, timeout.Token);
            // SslStream owns the NetworkStream, which owns the socket. Certificate identity is pinned, not DNS/time based.
            var stream = new SslStream(new NetworkStream(tcp.Client, true), false,
                (_, certificate, _, _) => certificate != null && Identity.Fingerprint(certificate) == peer.Id);
            try
            {
                await stream.AuthenticateAsClientAsync(new SslClientAuthenticationOptions {
                    TargetHost = "LanBridge", ClientCertificates = new X509CertificateCollection { identity.Certificate },
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13, CertificateRevocationCheckMode = X509RevocationMode.NoCheck
                }, timeout.Token);
                return stream;
            }
            catch { stream.Dispose(); throw; }
        }
        catch { tcp.Dispose(); throw; }
    }
    public async Task PairAsync(Peer peer, CancellationToken ct)
    {
        await using var ssl = await ConnectAsync(peer, true, ct);
        await Wire.WriteAsync(ssl, new Request { Kind = "pair", DeviceName = identity.Name }, ct);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(100));
        var reply = await Wire.ReadAsync<Reply>(ssl, timeout.Token); reply.EnsureSuccess(); identity.Trust(peer.Id, peer.Name);
    }
    public async Task<Reply> CallAsync(Peer peer, Request request, CancellationToken ct = default)
    {
        await using var ssl = await ConnectAsync(peer, false, ct);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(20));
        await Wire.WriteAsync(ssl, request, timeout.Token);
        var reply = await Wire.ReadAsync<Reply>(ssl, timeout.Token); reply.EnsureSuccess(); return reply;
    }
    public async Task<Reply> UploadAsync(Peer peer, string sourcePath, string destination, string name, string operationId,
        string? expectedSourceVersion, Action<long, long, string>? progress, CancellationToken ct, string parentIdentity)
    {
        FileSystem.NoLinks(sourcePath);
        using var guard = new PathGuard(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(sourcePath))!);
        await using var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, Wire.BufferSize, true);
        var version = FileSystem.Version(sourcePath, source.SafeFileHandle);
        if (expectedSourceVersion != null && version != expectedSourceVersion) throw new IOException("源文件已变化，请创建新的复制任务。");
        progress?.Invoke(0, source.Length, "校验源文件");
        var hash = await FileSystem.HashAsync(source, ct); source.Position = 0;
        if (version != FileSystem.Version(sourcePath, source.SafeFileHandle)) throw new IOException("源文件正在变化。");
        await using var ssl = await ConnectAsync(peer, false, ct);
        var req = new Request { Kind = "upload", Path = destination, Name = name, OperationId = operationId, Size = source.Length, Hash = hash, ParentIdentity = parentIdentity };
        await Wire.WriteAsync(ssl, req, ct);
        Reply reply;
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
        { timeout.CancelAfter(TimeSpan.FromSeconds(25)); reply = await Wire.ReadAsync<Reply>(ssl, timeout.Token); }
        reply.EnsureSuccess(); if (reply.Status != "ready") return reply;
        var actual = await Wire.CopyHashAsync(source, ssl, source.Length, n => progress?.Invoke(n, source.Length, "正在传输"), ct);
        if (actual != hash) throw new IOException("传输期间源内容变化，需核对目标结果。");
        progress?.Invoke(source.Length, source.Length, "等待目标提交");
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
        { timeout.CancelAfter(TimeSpan.FromSeconds(45)); reply = await Wire.ReadAsync<Reply>(ssl, timeout.Token); }
        reply.EnsureSuccess(); return reply;
    }
    public async Task DownloadAsync(Peer peer, FileEntry entry, string cachePath, Action<long, long>? progress, CancellationToken ct)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(cachePath)!);
        var part = cachePath + ".download";
        try
        {
            await using var ssl = await ConnectAsync(peer, false, ct);
            await Wire.WriteAsync(ssl, new Request { Kind = "download", Path = entry.Path, Version = entry.Version }, ct);
            Reply reply;
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
            { timeout.CancelAfter(TimeSpan.FromMinutes(30)); reply = await Wire.ReadAsync<Reply>(ssl, timeout.Token); }
            reply.EnsureSuccess();
            if (reply.Size != entry.Size || reply.Size < 0 || reply.Hash.Length != 64) throw new IOException("文件元数据变化或无效。");
            await using (var output = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, Wire.BufferSize, true))
            {
                var hash = await Wire.CopyHashAsync(ssl, output, reply.Size, n => progress?.Invoke(n, reply.Size), ct);
                if (hash != reply.Hash) throw new IOException("下载校验失败。");
                await output.FlushAsync(ct); output.Flush(true);
            }
            File.Move(part, cachePath, true);
        }
        finally { try { File.Delete(part); } catch (IOException) { } }
    }
}
