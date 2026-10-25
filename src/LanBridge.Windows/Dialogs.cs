using System.Collections.Specialized;
using System.Text.Json;
using System.Diagnostics.CodeAnalysis;
using System.Reflection.Metadata;
using LanBridge.Core;

namespace LanBridge.Windows;

public sealed record DragBundle(string PeerId, FileEntry[] Entries);

public static class Dialogs
{
    public static string? Ask(IWin32Window owner, string title, string value)
    {
        using var form = new Form { Text = title, Width = 460, Height = 155, StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false };
        var text = new TextBox { Left = 20, Top = 20, Width = 400, Text = value };
        var ok = new Button { Text = "确定", Left = 260, Top = 60, Width = 75, DialogResult = DialogResult.OK };
        var cancel = new Button { Text = "取消", Left = 345, Top = 60, Width = 75, DialogResult = DialogResult.Cancel };
        form.Controls.AddRange([text, ok, cancel]); form.AcceptButton = ok; form.CancelButton = cancel;
        return form.ShowDialog(owner) == DialogResult.OK ? text.Text : null;
    }

    public static T Wait<T>(IWin32Window owner, string title, Func<CancellationToken, IProgress<string>, Task<T>> work)
    {
        using var form = new Form { Text = title, Width = 480, Height = 170, StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false, ControlBox = false };
        var text = new Label { Left = 20, Top = 20, Width = 425, Height = 45, Text = "正在准备，请稍候…" };
        var bar = new ProgressBar { Left = 20, Top = 70, Width = 325, Style = ProgressBarStyle.Marquee };
        var cancel = new Button { Text = "取消", Left = 360, Top = 65, Width = 85 };
        using var ct = new CancellationTokenSource(); cancel.Click += (_, _) => { ct.Cancel(); cancel.Enabled = false; };
        form.Controls.AddRange([text, bar, cancel]);
        T? result = default; Exception? error = null;
        form.Shown += async (_, _) =>
        {
            var progress = new Progress<string>(s => { if (!form.IsDisposed) text.Text = s; });
            try { result = await Task.Run(() => work(ct.Token, progress)); }
            catch (Exception ex) { error = ex; }
            finally { form.Close(); }
        };
        form.ShowDialog(owner);
        if (error != null) throw error;
        return result!;
    }
}

// Native Explorer asks for CF_HDROP only when it needs actual local files.
// Internal pane-to-pane drops use our JSON format and never invoke this materializer.
// Use a managed IDataObject adapter rather than subclassing DataObject:
// .NET 10's native COM implementation forwards to its composition and bypasses subclass overrides.
public sealed class DeferredDragData : ITypedDataObject
{
    public const string Format = "LanBridge.Selection.v1";
    private readonly Func<string[]> materialize;
    private readonly IDataObject inner = new DataObject();
    private string[]? files;
    public DeferredDragData(DragBundle bundle, Func<string[]> materialize)
    { this.materialize = materialize; inner.SetData(Format, false, JsonSerializer.Serialize(bundle)); }
    public string[] GetFormats(bool autoConvert) => inner.GetFormats(autoConvert).Append(DataFormats.FileDrop).Distinct().ToArray();
    public string[] GetFormats() => GetFormats(true);
    public bool GetDataPresent(string format, bool autoConvert) => format == DataFormats.FileDrop || inner.GetDataPresent(format, autoConvert);
    public bool GetDataPresent(string format) => GetDataPresent(format, true);
    public bool GetDataPresent(Type format) => GetDataPresent(format.FullName!);
    public object? GetData(string format, bool autoConvert)
    {
        if (format != DataFormats.FileDrop) return inner.GetData(format, autoConvert);
        return files ??= materialize();
    }
    public object? GetData(string format) => GetData(format, true);
    public object? GetData(Type format) => GetData(format.FullName!);
    public bool TryGetData<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] T>([NotNullWhen(true), MaybeNullWhen(false)] out T data)
        => TryGetData(typeof(T).FullName!, true, out data);
    public bool TryGetData<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] T>(string format, [NotNullWhen(true), MaybeNullWhen(false)] out T data)
        => TryGetData(format, true, out data);
    public bool TryGetData<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] T>(string format, bool autoConvert, [NotNullWhen(true), MaybeNullWhen(false)] out T data)
    {
        if (GetData(format, autoConvert) is T value) { data = value; return true; }
        data = default; return false;
    }
    public bool TryGetData<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] T>(string format, Func<TypeName, Type?> resolver, bool autoConvert, [NotNullWhen(true), MaybeNullWhen(false)] out T data)
        => TryGetData(format, autoConvert, out data);
    public void SetData(string format, bool autoConvert, object? data) => inner.SetData(format, autoConvert, data);
    public void SetData(string format, object? data) => inner.SetData(format, data);
    public void SetData(Type format, object? data) => inner.SetData(format, data);
    public void SetData(object? data) => inner.SetData(data);
}
