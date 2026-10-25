using System.Collections.Specialized;
using System.Diagnostics;
using LanBridge.Core;

namespace LanBridge.Windows;

public sealed class MainForm : Form
{
    public Node Node { get; }
    public RemoteClient Client { get; }
    public TransferQueue Queue { get; }
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public FilePane ActivePane { get; set; }
    private readonly FilePane left;
    private readonly FilePane right;
    private readonly ComboBox devices = new() { Width = 235, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly Button connect = new() { Text = "连接设备", AutoSize = true };
    private readonly Label status = new() { Dock = DockStyle.Bottom, Height = 28, Padding = new Padding(12, 5, 0, 0), ForeColor = Color.DimGray };
    private readonly ListView tasks = new() { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, HideSelection = false, ShowItemToolTips = true, BorderStyle = BorderStyle.None };
    private readonly Dictionary<string, ListViewItem> taskRows = new();
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 2000 };
    private readonly Settings settings;
    private bool switching;
    public MainForm(Node node, TransferQueue queue, Settings settings)
    {
        Node = node; Queue = queue; Client = new(node.Identity); this.settings = settings;
        Text = "LanBridge · 邻窗互传"; Width = 1180; Height = 780; MinimumSize = new Size(940, 620);
        StartPosition = FormStartPosition.CenterScreen; BackColor = Color.FromArgb(237, 242, 248);
        Font = new Font("Microsoft YaHei UI", 10); AutoScaleMode = AutoScaleMode.Dpi;
        var heading = new Panel { Dock = DockStyle.Top, Height = 82, BackColor = Color.FromArgb(27, 43, 68) };
        heading.Controls.Add(new Label { Text = "邻窗互传", ForeColor = Color.White, Font = new Font(Font.FontFamily, 21, FontStyle.Bold), Left = 18, Top = 10, AutoSize = true });
        heading.Controls.Add(new Label { Text = "拖到旁边，就传过去。", ForeColor = Color.FromArgb(190, 211, 238), Left = 22, Top = 49, AutoSize = true });
        heading.Controls.Add(new Label { Text = $"{node.Identity.Name}    本机配对码：{Identity.ShortCode(node.Identity.Id)}", ForeColor = Color.White,
            AutoSize = true, Anchor = AnchorStyles.Top | AnchorStyles.Right, Left = 560, Top = 29 });
        var toolbar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 50, Padding = new Padding(10, 8, 0, 0), WrapContents = false };
        toolbar.Controls.Add(new Label { Text = "旁边的电脑", AutoSize = true, Margin = new Padding(4, 6, 8, 0) });
        toolbar.Controls.Add(devices); toolbar.Controls.Add(connect);
        Button Add(string label, EventHandler click)
        { var b = new Button { Text = label, AutoSize = true, Margin = new Padding(6, 0, 0, 0) }; b.Click += click; toolbar.Controls.Add(b); return b; }
        Add("发送文件", (_, _) => ChooseFiles());
        Add("发送文件夹", (_, _) => ChooseFolder());
        Add("设置", (_, _) => SettingsDialog());
        var panes = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Padding = new Padding(6) };
        panes.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50)); panes.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        left = new FilePane(this, "我的收件箱"); right = new FilePane(this, "旁边的电脑"); ActivePane = left;
        panes.Controls.Add(left, 0, 0); panes.Controls.Add(right, 1, 0);
        var bottom = new Panel { Dock = DockStyle.Bottom, Height = 195, Padding = new Padding(12, 0, 12, 5), BackColor = Color.White };
        var taskbar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 38, WrapContents = false };
        taskbar.Controls.Add(new Label { Text = "传输任务", AutoSize = true, Margin = new Padding(2, 8, 15, 0), Font = new Font(Font, FontStyle.Bold) });
        var cancel = new Button { Text = "暂停所选", AutoSize = true, Margin = new Padding(5) };
        var retry = new Button { Text = "重试所选", AutoSize = true, Margin = new Padding(5) };
        cancel.Click += (_, _) => { foreach (ListViewItem r in tasks.SelectedItems) Queue.Cancel((string)r.Tag!); };
        retry.Click += (_, _) => { foreach (ListViewItem r in tasks.SelectedItems) Queue.Retry((string)r.Tag!); };
        taskbar.Controls.AddRange([cancel, retry]);
        tasks.Columns.Add("文件 / 文件夹", 330); tasks.Columns.Add("方向", 80); tasks.Columns.Add("状态", 200); tasks.Columns.Add("进度 / 说明", 470);
        bottom.Controls.Add(tasks); bottom.Controls.Add(taskbar);
        Controls.Add(panes); Controls.Add(bottom); Controls.Add(toolbar); Controls.Add(heading); Controls.Add(status);
        devices.SelectedIndexChanged += async (_, _) =>
        {
            if (switching) return;
            if (devices.SelectedItem is Peer p) { right.SetPeer(p.Id, p.Name); await right.RefreshAsync(); }
        };
        connect.Click += async (_, _) => await PairAsync();
        node.PairApproval = (id, name) =>
        {
            var answer = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            Ui(() => answer.TrySetResult(MessageBox.Show(this,
                $"“{name}”请求连接。\n\n请核对对方屏幕上的本机配对码：\n{Identity.ShortCode(id)}\n\n同意后，双方可访问彼此共享文件夹，并复制、重命名和删除其中的文件。\n不会开放整个硬盘。", "首次配对", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes));
            return answer.Task;
        };
        node.PeersChanged += () => Ui(UpdateDevices);
        node.Diagnostic += text => Ui(() => status.Text = text);
        node.Store.Progress += p => Ui(() => UpdateProgress(p, true));
        queue.Progress += p => Ui(() => UpdateProgress(p, false));
        queue.Changed += job => Ui(() => UpdateJob(job));
        timer.Tick += async (_, _) => { await Task.WhenAll(left.RefreshAsync(), right.RefreshAsync()); };
        Shown += async (_, _) =>
        {
            node.Start(); left.SetPeer(node.Identity.Id, "我的收件箱");
            status.Text = "共享位置：" + node.Store.Root + "   · 只刷新目录，不自动同步副本";
            foreach (var job in Queue.Jobs.Values) UpdateJob(job);
            UpdateDevices(); timer.Start(); await left.RefreshAsync();
        };
        FormClosing += (_, e) =>
        {
            if (Queue.Jobs.Values.Any(j => j.State is "传输中" or "正在准备" or "等待中") &&
                MessageBox.Show(this, "仍有传输任务。关闭将中断传输，之后可重试。确定关闭？", "关闭", MessageBoxButtons.YesNo) != DialogResult.Yes)
                e.Cancel = true;
            else { timer.Stop(); Queue.Dispose(); Node.Dispose(); }
        };
    }
    private void Ui(Action action)
    {
        if (IsDisposed || !IsHandleCreated) return;
        try { if (InvokeRequired) BeginInvoke(action); else action(); } catch (InvalidOperationException) { }
    }
    private void UpdateDevices()
    {
        var old = (devices.SelectedItem as Peer)?.Id;
        var peers = Node.Peers.Values.OrderByDescending(p => Node.Identity.IsTrusted(p.Id)).ThenBy(p => p.Name).ToArray();
        switching = true;
        devices.Items.Clear(); devices.Items.AddRange(peers.Cast<object>().ToArray());
        var selected = peers.FirstOrDefault(p => p.Id == old) ?? peers.FirstOrDefault();
        devices.SelectedItem = selected; switching = false;
        if (selected?.Id != right.PeerId)
        {
            right.SetPeer(selected?.Id, selected?.Name ?? "等待旁边的电脑");
            _ = right.RefreshAsync();
        }
        connect.Enabled = selected != null;
        connect.Text = selected != null && Node.Identity.IsTrusted(selected.Id) ? "已配对 · 刷新" : "连接设备";
    }
    private async Task PairAsync()
    {
        if (devices.SelectedItem is not Peer peer) return;
        if (!Node.Identity.IsTrusted(peer.Id) && MessageBox.Show(this, $"请核对“{peer.Name}”屏幕上的配对码：\n\n{peer.Code}\n\n同意后，该设备也将获得你共享文件夹的管理权限。\n是否连接？",
            "首次连接", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        connect.Enabled = false; status.Text = "等待对方确认配对…";
        try { await Client.PairAsync(peer, CancellationToken.None); UpdateDevices(); await right.RefreshAsync(); status.Text = "配对成功，以后自动连接。"; }
        catch (Exception ex) { Error(ex); }
        finally { connect.Enabled = true; }
    }
    public void Error(Exception ex)
    {
        if (ex is OperationCanceledException) return;
        MessageBox.Show(this, ex.Message, "操作未完成", MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }
    public static string SizeText(long n) => n >= 1L << 30 ? $"{n / (double)(1L << 30):0.00} GB" : n >= 1 << 20 ? $"{n / (double)(1 << 20):0.0} MB" : n >= 1024 ? $"{n / 1024d:0.0} KB" : n + " B";
    private ListViewItem Row(string id, string name, string direction)
    {
        if (taskRows.TryGetValue(id, out var old)) return old;
        var row = new ListViewItem(name) { Tag = id }; row.SubItems.AddRange([direction, "", ""]);
        tasks.Items.Insert(0, row); taskRows[id] = row; return row;
    }
    private void UpdateJob(TransferJob job)
    {
        var row = Row(job.Id, job.Name, "复制"); row.SubItems[2].Text = job.State;
        row.SubItems[3].Text = job.Error.Length > 0 ? job.Error : job.State == "完成" ? "已校验并提交" : "";
        row.ToolTipText = job.Error;
        if (job.State == "完成") { _ = left.RefreshAsync(); _ = right.RefreshAsync(); }
    }
    private void UpdateProgress(TransferProgress p, bool incoming)
    {
        var row = Row((incoming ? "in-" : "") + p.Id, p.Name, p.Direction);
        row.SubItems[2].Text = p.State;
        row.SubItems[3].Text = p.Total == 0 ? SizeText(p.Done) : $"{100d * p.Done / p.Total:0}%   {SizeText(p.Done)} / {SizeText(p.Total)}";
    }
    private void ChooseFiles()
    {
        using var dialog = new OpenFileDialog { Multiselect = true, Title = "选择要发送到旁边电脑的文件" };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        var data = new DataObject(DataFormats.FileDrop, dialog.FileNames);
        try { right.AcceptData(data); } catch (Exception ex) { Error(ex); }
    }
    private void ChooseFolder()
    {
        using var dialog = new FolderBrowserDialog { Description = "选择要发送到旁边电脑的文件夹", UseDescriptionForTitle = true };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        try { right.AcceptData(new DataObject(DataFormats.FileDrop, new[] { dialog.SelectedPath })); } catch (Exception ex) { Error(ex); }
    }
    public async Task DeleteAsync(FilePane pane)
    {
        var selected = pane.Selection; if (pane.PeerId == null || selected.Length == 0) return;
        var peerId = pane.PeerId;
        if (MessageBox.Show(this, $"删除所选 {selected.Length} 项？\n文件将移到所属电脑的 LanBridge 回收区。", "删除文件", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
        try
        {
            foreach (var e in selected) await CallMutationAsync(peerId, new Request { Kind = "delete", Path = e.Path, Version = e.Version, OperationId = Store.NewOperationId() });
            await pane.RefreshAsync();
        }
        catch (Exception ex) { Error(ex); }
    }
    public async Task RenameAsync(FilePane pane)
    {
        var e = pane.Selection.FirstOrDefault(); if (e == null || pane.PeerId == null) return;
        var peerId = pane.PeerId; var name = Dialogs.Ask(this, "重命名", e.Name); if (name == null || name == e.Name) return;
        try
        {
            await CallMutationAsync(peerId, new Request { Kind = "rename", Path = e.Path, Version = e.Version, Name = name, OperationId = Store.NewOperationId() });
            await pane.RefreshAsync();
        }
        catch (Exception ex) { Error(ex); }
    }
    public async Task CreateDirectoryAsync(FilePane pane)
    {
        if (pane.PeerId == null || pane.DirectoryIdentity.Length == 0) return;
        var peerId = pane.PeerId; var parent = pane.DirectoryPath; var parentId = pane.DirectoryIdentity;
        var name = Dialogs.Ask(this, "新建文件夹", "新建文件夹"); if (name == null) return;
        try
        {
            await CallMutationAsync(peerId, new Request { Kind = "mkdir", Path = parent, ParentIdentity = parentId, Name = name, OperationId = Store.NewOperationId() });
            await pane.RefreshAsync();
        }
        catch (Exception ex) { Error(ex); }
    }
    private async Task CallMutationAsync(string peerId, Request request)
    {
        // Persist the exact request before sending. A lost reply can be retried with the same ID after restart.
        var folder = System.IO.Path.Combine(Node.Identity.DataDirectory, "pending-actions"); Directory.CreateDirectory(folder);
        var file = System.IO.Path.Combine(folder, request.OperationId + ".json");
        JsonFiles.Write(file, new PendingAction(peerId, request));
        await Client.CallAsync(Node.GetPeer(peerId), request); File.Delete(file);
    }
    private sealed record PendingAction(string PeerId, Request Request);
    public string[] Materialize(DragBundle bundle)
    {
        if (bundle.PeerId == Node.Identity.Id)
            return bundle.Entries.Select(e =>
            {
                var path = Node.Store.Resolve(e.Path, false);
                if (FileSystem.Version(path) != e.Version) throw new IOException("文件已变化，请重新选择。"); return path;
            }).ToArray();
        return Dialogs.Wait(this, "正在准备本地文件", async (ct, progress) =>
        {
            var export = System.IO.Path.Combine(Node.Identity.DataDirectory, "exports", Store.NewOperationId()); Directory.CreateDirectory(export);
            var paths = new List<string>(); int count = 0;
            async Task Fetch(FileEntry entry, string destination)
            {
                ct.ThrowIfCancellationRequested(); if (++count > 20_000) throw new IOException("文件过多，请分批导出。");
                Names.Leaf(entry.Name);
                if (entry.IsDirectory)
                {
                    Directory.CreateDirectory(destination);
                    var list = await Client.CallAsync(Node.GetPeer(bundle.PeerId), new Request { Kind = "list", Path = entry.Path }, ct);
                    if (entry.Identity.Length > 0 && list.Version != entry.Identity) throw new IOException("源文件夹已被替换，请重新选择。");
                    foreach (var child in list.Entries) await Fetch(child, System.IO.Path.Combine(destination, child.Name));
                }
                else
                {
                    progress.Report("正在校验远程文件：" + entry.Name);
                    await Client.DownloadAsync(Node.GetPeer(bundle.PeerId), entry, destination,
                        (done, total) => progress.Report(entry.Name + "   " + SizeText(done) + " / " + SizeText(total)), ct);
                }
            }
            try
            {
                foreach (var entry in bundle.Entries) { var path = System.IO.Path.Combine(export, Names.Leaf(entry.Name)); await Fetch(entry, path); paths.Add(path); }
                return paths.ToArray();
            }
            catch { try { Directory.Delete(export, true); } catch (IOException) { } throw; }
        });
    }
    public void CopyClipboard(FilePane pane)
    {
        if (pane.PeerId == null || pane.Selection.Length == 0) return;
        try
        {
            var bundle = new DragBundle(pane.PeerId, pane.Selection);
            var paths = Materialize(bundle); var data = new DataObject();
            data.SetData(DeferredDragData.Format, false, System.Text.Json.JsonSerializer.Serialize(bundle));
            var collection = new StringCollection(); collection.AddRange(paths); data.SetFileDropList(collection);
            Clipboard.SetDataObject(data, true); status.Text = "已复制，可以粘贴到软件窗口或 Windows 文件夹。";
        }
        catch (Exception ex) { Error(ex); }
    }
    public void Open(FilePane pane, FileEntry entry)
    {
        if (pane.PeerId == null) return;
        if (entry.IsDirectory) { pane.Navigate(entry.Path); _ = pane.RefreshAsync(); return; }
        try
        {
            var paths = Materialize(new(pane.PeerId, [entry]));
            Process.Start(new ProcessStartInfo(paths[0]) { UseShellExecute = true });
        }
        catch (Exception ex) { Error(ex); }
    }
    private void SettingsDialog()
    {
        using var form = new Form { Text = "设置与维护", Width = 620, Height = 420, StartPosition = FormStartPosition.CenterParent, Font = Font };
        var flow = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new Padding(18) };
        flow.Controls.Add(new Label { Text = "共享目录：\n" + Node.Store.Root, Width = 560, Height = 55 });
        void Add(string text, EventHandler action) { var b = new Button { Text = text, Width = 350, Height = 34 }; b.Click += action; flow.Controls.Add(b); }
        Add("打开本机共享文件夹", (_, _) => Process.Start(new ProcessStartInfo(Node.Store.Root) { UseShellExecute = true }));
        Add("更换共享文件夹（下次启动生效）", (_, _) =>
        {
            using var pick = new FolderBrowserDialog { Description = "仅选择你愿意向已配对设备开放管理权限的文件夹" };
            if (pick.ShowDialog(form) == DialogResult.OK)
            {
                settings.SharedFolder = pick.SelectedPath;
                JsonFiles.Write(System.IO.Path.Combine(Node.Identity.DataDirectory, "settings.json"), settings);
                MessageBox.Show(form, "已保存。关闭并重新打开程序后生效；不会移动现有文件。");
            }
        });
        Add("本机回收区 / 恢复文件", (_, _) => TrashDialog());
        Add("打开日志与待确认操作目录", (_, _) => Process.Start(new ProcessStartInfo(Node.Identity.DataDirectory) { UseShellExecute = true }));
        Add("重试未收到回执的删除 / 改名操作", async (_, _) =>
        {
            var folder = System.IO.Path.Combine(Node.Identity.DataDirectory, "pending-actions"); if (!Directory.Exists(folder)) return;
            foreach (var path in Directory.GetFiles(folder, "*.json"))
            {
                try { var p = JsonFiles.Read<PendingAction>(path); await Client.CallAsync(Node.GetPeer(p.PeerId), p.Request); File.Delete(path); }
                catch (Exception ex) { Error(ex); }
            }
            await left.RefreshAsync(); await right.RefreshAsync();
        });
        Add("撤销当前右侧设备的授权", (_, _) =>
        {
            if (right.PeerId == null) return;
            if (MessageBox.Show(form, "撤销后，该设备需要重新配对才能访问。", "撤销授权", MessageBoxButtons.OKCancel) == DialogResult.OK)
            { Node.Identity.Revoke(right.PeerId); UpdateDevices(); _ = right.RefreshAsync(); }
        });
        flow.Controls.Add(new Label { Text = "回收区不会自动清空。远程打开 / 拖出的缓存位于数据目录 exports。", Width = 560, Height = 42, ForeColor = Color.DimGray });
        form.Controls.Add(flow); form.ShowDialog(this);
    }
    private void TrashDialog()
    {
        using var form = new Form { Text = "本机回收区", Width = 730, Height = 450, StartPosition = FormStartPosition.CenterParent, Font = Font };
        var list = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, MultiSelect = false };
        list.Columns.Add("原路径", 450); list.Columns.Add("删除时间", 220);
        void Reload()
        {
            list.Items.Clear();
            foreach (var item in Node.Store.ListTrash())
            { var row = new ListViewItem(item.OriginalPath) { Tag = item }; row.SubItems.Add(item.DeletedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm")); list.Items.Add(row); }
        }
        var restore = new Button { Text = "恢复所选（原位置有同名文件时保留两份）", Dock = DockStyle.Bottom, Height = 42 };
        restore.Click += async (_, _) =>
        {
            if (list.SelectedItems.Count == 0) return;
            try { await Node.Store.RestoreAsync((TrashItem)list.SelectedItems[0].Tag!); Reload(); await left.RefreshAsync(); }
            catch (Exception ex) { Error(ex); }
        };
        form.Controls.Add(list); form.Controls.Add(restore);
        try { Reload(); form.ShowDialog(this); } catch (Exception ex) { Error(ex); }
    }
}
