using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;

namespace LanBridge.Core;

public static class FileSystem
{
    // FILETIME consists of two DWORDs (4-byte aligned), so this structure MUST use Pack=4.
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct HandleInfo
    {
        public uint Attributes; public long CreationTime; public long AccessTime; public long WriteTime;
        public uint Volume; public uint SizeHigh; public uint SizeLow; public uint Links; public uint IndexHigh; public uint IndexLow;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct BasicInfo { public long Creation; public long Access; public long Write; public long Change; public uint Attributes; }
    [StructLayout(LayoutKind.Explicit, Size = 256)]
    private struct Statx
    {
        [FieldOffset(32)] public ulong Inode; [FieldOffset(40)] public ulong Size;
        [FieldOffset(96)] public long ChangeSec; [FieldOffset(104)] public uint ChangeNsec;
        [FieldOffset(136)] public uint DeviceMajor; [FieldOffset(140)] public uint DeviceMinor;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, FileShare share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle file, out HandleInfo info);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle file, int kind, out BasicInfo info, uint size);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle file, int kind, IntPtr info, uint size);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle file, uint code, IntPtr input, uint inputSize,
        [Out] byte[] output, uint outputSize, out uint returned, IntPtr overlapped);
    [DllImport("libc", SetLastError = true, EntryPoint = "statx")]
    private static extern int LinuxStatx(int directory, string path, int flags, uint mask, out Statx stat);

    public static SafeFileHandle OpenHandle(string path, bool mutate = false)
    {
        // READ_DATA / LIST_DIRECTORY is essential: attribute-only handles do not enforce share-mode locking.
        var file = CreateFileW(path, 0x80u | 0x1u | (mutate ? 0x10000u : 0u), FileShare.Read | (Directory.Exists(path) ? FileShare.Write : 0),
            IntPtr.Zero, 3, 0x02000000u | 0x00200000u, IntPtr.Zero); // BACKUP_SEMANTICS | OPEN_REPARSE_POINT
        if (file.IsInvalid) { file.Dispose(); throw new IOException("无法锁定文件：" + path, new Win32Exception(Marshal.GetLastWin32Error())); }
        if (!GetFileInformationByHandle(file, out var info) || (info.Attributes & 0x400) != 0)
        { file.Dispose(); throw new IOException("不允许访问符号链接或目录联接。"); }
        return file;
    }
    public static string StableId(string path, SafeFileHandle? handle = null)
    {
        if (OperatingSystem.IsWindows())
        {
            using var own = handle == null ? OpenHandle(path) : null;
            if (!GetFileInformationByHandle(handle ?? own!, out var i)) throw new Win32Exception(Marshal.GetLastWin32Error());
            return $"{i.Volume:X8}:{i.IndexHigh:X8}{i.IndexLow:X8}:{i.CreationTime:X16}";
        }
        if (LinuxStatx(-100, path, 0x100, 0x7ff, out var s) != 0) throw new IOException("文件不存在：" + path);
        return $"{s.DeviceMajor}:{s.DeviceMinor}:{s.Inode}";
    }
    public static string Version(string path, SafeFileHandle? handle = null)
    {
        if (OperatingSystem.IsWindows())
        {
            using var own = handle == null ? OpenHandle(path) : null;
            var h = handle ?? own!;
            if (!GetFileInformationByHandleEx(h, 0, out var i, (uint)Marshal.SizeOf<BasicInfo>())) throw new Win32Exception(Marshal.GetLastWin32Error());
            if (!GetFileInformationByHandle(h, out var f)) throw new Win32Exception(Marshal.GetLastWin32Error());
            var usn = ChangeSequence(path, h);
            return JsonFiles.Digest(new { id = StableId(path, h), usn, i.Change, i.Write, f.SizeHigh, f.SizeLow });
        }
        if (LinuxStatx(-100, path, 0x100, 0x7ff, out var s) != 0) throw new IOException("文件不存在：" + path);
        return JsonFiles.Digest(new { id = StableId(path), s.ChangeSec, s.ChangeNsec, s.Size });
    }
    private static long ChangeSequence(string path, SafeFileHandle lockedHandle)
    {
        // The data handle can be asynchronous. Query through a separate synchronous
        // attribute handle while the original handle prevents replacement/writes.
        using var query = CreateFileW(path, 0x80u, FileShare.ReadWrite | FileShare.Delete,
            IntPtr.Zero, 3, 0x02000000u | 0x00200000u, IntPtr.Zero);
        if (query.IsInvalid || StableId(path, query) != StableId(path, lockedHandle))
            throw new IOException("文件身份已变化，请刷新后重试。");
        var record = new byte[4096];
        // FSCTL_READ_FILE_USN_DATA; null input requests USN_RECORD_V2.
        if (!DeviceIoControl(query, 0x000900EB, IntPtr.Zero, 0, record, (uint)record.Length, out var returned, IntPtr.Zero))
            throw new IOException("无法读取文件变更序号。请使用已启用 USN 日志的本机 NTFS 磁盘。",
                new Win32Exception(Marshal.GetLastWin32Error()));
        if (returned < 60 || BitConverter.ToUInt16(record, 4) != 2 || BitConverter.ToUInt32(record, 0) > returned)
            throw new IOException("文件系统返回了不支持的变更序号格式。");
        var usn = BitConverter.ToInt64(record, 24);
        if (usn <= 0) throw new IOException("此磁盘没有可用的文件变更日志。请使用已启用 USN 日志的本机 NTFS 磁盘。");
        return usn;
    }
    public static void Move(string source, string destination, SafeFileHandle? handle = null)
    {
        if (!OperatingSystem.IsWindows())
        { if (Directory.Exists(source)) Directory.Move(source, destination); else File.Move(source, destination); return; }
        using var own = handle == null ? OpenHandle(source, true) : null;
        var bytes = System.Text.Encoding.Unicode.GetBytes(System.IO.Path.GetFullPath(destination));
        int rootOffset = IntPtr.Size == 8 ? 8 : 4;
        int lengthOffset = rootOffset + IntPtr.Size;
        int nameOffset = lengthOffset + 4;
        // Win32 path conversion needs a NUL-terminated WCHAR string even though
        // FileNameLength excludes that terminator. Otherwise trailing heap data
        // can become part of the actual destination filename on Windows.
        var bufferSize = nameOffset + bytes.Length + sizeof(char);
        var buffer = Marshal.AllocHGlobal(bufferSize);
        try
        {
            for (var i = 0; i < nameOffset; i++) Marshal.WriteByte(buffer, i, 0);
            Marshal.WriteIntPtr(buffer, rootOffset, IntPtr.Zero);
            Marshal.WriteInt32(buffer, lengthOffset, bytes.Length);
            Marshal.Copy(bytes, 0, buffer + nameOffset, bytes.Length);
            Marshal.WriteInt16(buffer, nameOffset + bytes.Length, 0);
            if (!SetFileInformationByHandle(handle ?? own!, 3, buffer, (uint)bufferSize))
                throw new IOException("移动失败；文件可能被占用或目标已存在。", new Win32Exception(Marshal.GetLastWin32Error()));
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }
    public static bool Exists(string path) => File.Exists(path) || Directory.Exists(path);
    public static async Task<string> HashAsync(Stream stream, CancellationToken ct) => Convert.ToHexString(await SHA256.HashDataAsync(stream, ct));
    public static void NoLinks(string path)
    {
        for (var current = System.IO.Path.GetFullPath(path); current != null; current = System.IO.Path.GetDirectoryName(current))
            if (Exists(current) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("共享路径不能包含符号链接或目录联接。");
    }
}

public sealed class PathGuard : IDisposable
{
    private readonly List<SafeFileHandle> handles = [];
    public PathGuard(string path)
    {
        try
        {
            var ancestors = new Stack<string>();
            for (string? p = System.IO.Path.GetFullPath(path); p != null; p = System.IO.Path.GetDirectoryName(p)) ancestors.Push(p);
            foreach (var p in ancestors)
            {
                if (!Directory.Exists(p)) throw new DirectoryNotFoundException(p);
                FileSystem.NoLinks(p);
                if (OperatingSystem.IsWindows()) handles.Add(FileSystem.OpenHandle(p));
            }
        }
        catch { Dispose(); throw; }
    }
    public void Dispose() { foreach (var h in handles) h.Dispose(); handles.Clear(); }
}
