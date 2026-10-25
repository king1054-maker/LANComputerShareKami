using System.Text.Json;
using System.Security.Cryptography;

namespace LanBridge.Core;

public sealed record FileEntry(string Name, string Path, bool IsDirectory, long Size, string Version, DateTime ModifiedUtc, string Identity = "");
public sealed record Peer(string Id, string Name, string Address, int Port, DateTime SeenUtc)
{
    public string Code => Identity.ShortCode(Id);
    public override string ToString() => Name;
}
public sealed class Request
{
    public string Kind { get; set; } = "";
    public string OperationId { get; set; } = "";
    public string Path { get; set; } = "";
    public string Name { get; set; } = "";
    public string Version { get; set; } = "";
    public string Hash { get; set; } = "";
    public long Size { get; set; }
    public string DeviceName { get; set; } = "";
    public string ParentIdentity { get; set; } = "";
}
public sealed class Reply
{
    public string Status { get; set; } = "ok";
    public string Error { get; set; } = "";
    public string Path { get; set; } = "";
    public string Hash { get; set; } = "";
    public long Size { get; set; }
    public string Version { get; set; } = "";
    public FileEntry[] Entries { get; set; } = [];
    public static Reply Fail(string error) => new() { Status = "error", Error = error };
    public void EnsureSuccess() { if (Status == "error") throw new IOException(Error); }
}
public sealed record TransferProgress(string Id, string Name, string Direction, long Done, long Total, string State);
public sealed record TrashItem(string RecordName, string OriginalPath, DateTime DeletedUtc);

public static class JsonFiles
{
    public static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    public static T Read<T>(string path) => JsonSerializer.Deserialize<T>(File.ReadAllBytes(path)) ?? throw new IOException("状态文件无效：" + path);
    public static void Write<T>(string path, T value)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        var temp = path + ".new";
        using (var file = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        { JsonSerializer.Serialize(file, value, Options); file.Flush(true); }
        File.Move(temp, path, true);
    }
    public static string Digest<T>(T value) => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value)));
}

public sealed class OperationRecord
{
    public string Id { get; set; } = "";
    public string RequestHash { get; set; } = "";
    public string Kind { get; set; } = "";
    public string State { get; set; } = "prepared";
    public string Source { get; set; } = "";
    public string Target { get; set; } = "";
    public string Identity { get; set; } = "";
    public Reply Result { get; set; } = new();
}

public static class Names
{
    public static string Leaf(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 200 || name is "." or ".." ||
            name.Any(c => c < 32 || "<>:\"/\\|?*".Contains(c)) || name.EndsWith('.') || name.EndsWith(' '))
            throw new IOException("文件名无效。");
        var stem = name.Split('.')[0].ToUpperInvariant();
        if (stem is "CON" or "PRN" or "AUX" or "NUL" ||
            (stem.Length == 4 && (stem.StartsWith("COM") || stem.StartsWith("LPT")) && "123456789¹²³".Contains(stem[3])))
            throw new IOException("这是 Windows 保留文件名。");
        return name;
    }
    public static string Relative(string path)
    {
        if (path == "") return "";
        if (path.Length > 2000 || path.Contains('\\') || path.StartsWith('/') || path.EndsWith('/')) throw new IOException("路径无效。");
        foreach (var part in path.Split('/')) Leaf(part);
        return path;
    }
    public static string Join(string parent, string name) => parent.Length == 0 ? Leaf(name) : Relative(parent) + "/" + Leaf(name);
}
