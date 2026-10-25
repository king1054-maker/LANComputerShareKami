using System.Text.Json;
using LanBridge.Core;

namespace LanBridge.Windows;

public sealed class FilePane : UserControl
{
    private readonly MainForm owner;
    public string? PeerId { get; private set; }
    public string DirectoryPath { get; private set; } = "";
    public string DirectoryIdentity { get; private set; } = "";
    public readonly ListView Files = new() { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true,
        HideSelection = false, MultiSelect = true, AllowDrop = true, BorderStyle = BorderStyle.None };
    private readonly Label title = new() { AutoSize = true, Font = new Font("Microsoft YaHei UI", 12, FontStyle.Bold), Margin = new Padding(8, 10, 5, 8) };
    private readonly Label path = new() { Dock = DockStyle.Top, Height = 32, Padding = new Padding(10, 7, 0, 0), BackColor = Color.FromArgb(243, 246, 250) };
    private readonly Label state = new() { Dock = DockStyle.Bottom, Height = 30, Padding = new Padding(10, 5, 0, 0), ForeColor = Color.DimGray };
    private bool refreshing;
    private int generation;
    private string signature = "";
    public bool Dragging { get; private set; }
    public FileEntry[] Selection => Files.SelectedItems.Cast<ListViewItem>().Select(x => (FileEntry)x.Tag!).ToArray();
    public FilePane(MainForm owner, string heading)
    {
        this.owner = owner; Dock = DockStyle.Fill; BackColor = Color.White; Margin = new Padding(6);
        title.Text = heading;
        var header = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 48, WrapContents = false, BackColor = Color.White };
        var up = new Button { Text = "↑ 上一级", AutoSize = true, Margin = new Padding(10, 8, 0, 0) };
        up.Click += async (_, _) => { var i = DirectoryPath.LastIndexOf('/'); Navigate(i < 0 ? "" : DirectoryPath[..i]); await RefreshAsync(); };
        var refresh = new Button { Text = "刷新", AutoSize = true, Margin = new Padding(6, 8, 0, 0) };
        refresh.Click += async (_, _) => await RefreshAsync();
        header.Controls.AddRange([title, up, refresh]);
        Files.Columns.Add("名称", 275); Files.Columns.Add("大小", 95); Files.Columns.Add("修改时间", 155);
        Files.Font = new Font("Microsoft YaHei UI", 10); Files.BackColor = Color.White;
        Controls.Add(Files); Controls.Add(state); Controls.Add(path); Controls.Add(header);
        Files.Enter += (_, _) => owner.ActivePane = this;
        Files.MouseDown += (_, _) => owner.ActivePane = this;
        Files.DoubleClick += async (_, _) =>
        {
            var item = Selection.FirstOrDefault(); if (item == null) return;
            if (item.IsDirectory) { Navigate(item.Path); await RefreshAsync(); }
            else owner.Open(this, item);
        };
        Files.ItemDrag += (_, _) =>
        {
            if (PeerId == null || Selection.Length == 0) return;
            try
            {
                Dragging = true; var bundle = new DragBundle(PeerId, Selection);
                var data = new DeferredDragData(bundle, () => owner.Materialize(bundle));
                Files.DoDragDrop(new DataObject(data), DragDropEffects.Copy);
            }
            catch (Exception ex) { owner.Error(ex); }
            finally { Dragging = false; }
        };
        Files.DragEnter += HandleDragOver;
        Files.DragOver += HandleDragOver;
        Files.DragDrop += (_, e) =>
        {
            if (e.Data == null) return;
            var hit = Files.GetItemAt(Files.PointToClient(new Point(e.X, e.Y)).X, Files.PointToClient(new Point(e.X, e.Y)).Y)?.Tag as FileEntry;
            try { AcceptData(e.Data, hit?.IsDirectory == true ? hit : null); } catch (Exception ex) { owner.Error(ex); }
        };
        Files.KeyDown += async (_, e) =>
        {
            if (e.KeyCode == Keys.Delete) { e.Handled = true; await owner.DeleteAsync(this); }
            else if (e.KeyCode == Keys.F2) { e.Handled = true; await owner.RenameAsync(this); }
            else if (e.KeyCode == Keys.F5) { e.Handled = true; await RefreshAsync(); }
            else if (e.Control && e.KeyCode == Keys.C) { e.Handled = true; owner.CopyClipboard(this); }
            else if (e.Control && e.KeyCode == Keys.V) { e.Handled = true; try { var d = Clipboard.GetDataObject(); if (d != null) AcceptData(d); } catch (Exception ex) { owner.Error(ex); } }
        };
        var menu = new ContextMenuStrip();
        menu.Items.Add("打开", null, (_, _) => { var item = Selection.FirstOrDefault(); if (item != null) owner.Open(this, item); });
        menu.Items.Add("复制", null, (_, _) => owner.CopyClipboard(this));
        menu.Items.Add("粘贴到此文件夹", null, (_, _) => { try { var d = Clipboard.GetDataObject(); if (d != null) AcceptData(d); } catch (Exception ex) { owner.Error(ex); } });
        menu.Items.Add("新建文件夹", null, async (_, _) => await owner.CreateDirectoryAsync(this));
        menu.Items.Add("重命名", null, async (_, _) => await owner.RenameAsync(this));
        menu.Items.Add("删除（移入该设备回收区）", null, async (_, _) => await owner.DeleteAsync(this));
        Files.ContextMenuStrip = menu;
    }
    private void HandleDragOver(object? sender, DragEventArgs e)
    {
        e.Effect = PeerId != null && DirectoryIdentity.Length > 0 && e.Data != null &&
            (e.Data.GetDataPresent(DeferredDragData.Format) || e.Data.GetDataPresent(DataFormats.FileDrop)) ? DragDropEffects.Copy : DragDropEffects.None;
    }
    public void AcceptData(IDataObject data, FileEntry? folder = null)
    {
        if (PeerId == null || DirectoryIdentity.Length == 0) throw new IOException("请先连接设备并打开接收目录。");
        var destination = folder?.Path ?? DirectoryPath;
        var destinationIdentity = folder?.Identity ?? DirectoryIdentity;
        if (data.GetDataPresent(DeferredDragData.Format) && data.TryGetData<string>(DeferredDragData.Format, out var json))
        {
            var bundle = JsonSerializer.Deserialize<DragBundle>(json) ?? throw new IOException("拖放内容无效。");
            foreach (var item in bundle.Entries)
                owner.Queue.Enqueue(bundle.PeerId, item.Path, item, PeerId, destination, destinationIdentity);
        }
        else if (data.TryGetData<string[]>(DataFormats.FileDrop, out var files))
            foreach (var file in files) owner.Queue.Enqueue("", file, null, PeerId, destination, destinationIdentity);
    }
    public void SetPeer(string? id, string name)
    {
        PeerId = id; title.Text = name; Navigate(""); Files.Items.Clear(); state.Text = id == null ? "等待局域网设备上线" : "正在连接…";
    }
    public void Navigate(string relative)
    { DirectoryPath = relative; DirectoryIdentity = ""; signature = ""; generation++; path.Text = "/" + relative; }
    public async Task RefreshAsync()
    {
        if (refreshing || Dragging || PeerId == null || IsDisposed) return;
        refreshing = true; var current = generation; var peerId = PeerId;
        try
        {
            if (!owner.Node.Identity.IsTrusted(peerId) && peerId != owner.Node.Identity.Id)
            { DirectoryIdentity = ""; state.Text = "首次使用请点击“连接设备”"; return; }
            var reply = await owner.Client.CallAsync(owner.Node.GetPeer(peerId), new Request { Kind = "list", Path = DirectoryPath });
            if (generation != current || peerId != PeerId || IsDisposed) return;
            DirectoryIdentity = reply.Version;
            var next = JsonFiles.Digest(reply.Entries);
            if (next != signature)
            {
                var selected = Selection.Select(x => x.Path).ToHashSet();
                Files.BeginUpdate(); Files.Items.Clear();
                foreach (var file in reply.Entries)
                {
                    var row = new ListViewItem((file.IsDirectory ? "▸  " : "    ") + file.Name) { Tag = file };
                    row.SubItems.Add(file.IsDirectory ? "文件夹" : MainForm.SizeText(file.Size));
                    row.SubItems.Add(file.ModifiedUtc.ToLocalTime().ToString("MM-dd HH:mm"));
                    Files.Items.Add(row); row.Selected = selected.Contains(file.Path);
                }
                Files.EndUpdate(); signature = next;
            }
            state.Text = $"{reply.Entries.Length} 个项目 · 拖入即复制 · Delete 删除";
        }
        catch (Exception ex)
        {
            if (generation == current) { DirectoryIdentity = ""; state.Text = "离线 / 无法访问：" + ex.Message; }
        }
        finally { refreshing = false; }
    }
}
