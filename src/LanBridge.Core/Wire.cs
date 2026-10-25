using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;

namespace LanBridge.Core;

public static class Wire
{
    public const int BufferSize = 256 * 1024;
    public const int MaxHeader = 16 * 1024 * 1024;
    public static async Task WriteAsync<T>(Stream stream, T value, CancellationToken ct)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(value);
        if (payload.Length > MaxHeader) throw new IOException("目录或消息过大。");
        var header = new byte[4]; BinaryPrimitives.WriteInt32BigEndian(header, payload.Length);
        await stream.WriteAsync(header, ct); await stream.WriteAsync(payload, ct); await stream.FlushAsync(ct);
    }
    public static async Task<T> ReadAsync<T>(Stream stream, CancellationToken ct, int limit = MaxHeader)
    {
        var header = new byte[4]; await stream.ReadExactlyAsync(header, ct);
        var size = BinaryPrimitives.ReadInt32BigEndian(header);
        if (size < 2 || size > limit) throw new IOException("消息长度无效。");
        var data = new byte[size]; await stream.ReadExactlyAsync(data, ct);
        return JsonSerializer.Deserialize<T>(data) ?? throw new IOException("消息无效。");
    }
    public static async Task<string> CopyHashAsync(Stream input, Stream output, long size, Action<long>? progress, CancellationToken ct)
    {
        if (size < 0) throw new IOException("文件长度无效。");
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[BufferSize]; long copied = 0;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (copied < size)
        {
            using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct); idle.CancelAfter(TimeSpan.FromSeconds(45));
            var n = await input.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, size - copied)), idle.Token);
            if (n == 0) throw new EndOfStreamException("连接中断，文件不完整。");
            hash.AppendData(buffer, 0, n); await output.WriteAsync(buffer.AsMemory(0, n), idle.Token); copied += n;
            if (clock.ElapsedMilliseconds >= 150) { progress?.Invoke(copied); clock.Restart(); }
        }
        progress?.Invoke(copied);
        return Convert.ToHexString(hash.GetHashAndReset());
    }
}
