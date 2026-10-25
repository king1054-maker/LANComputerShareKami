using LanBridge.Core;

namespace LanBridge.Windows;

public sealed class Settings
{
    public string SharedFolder { get; set; } = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "LanBridge 收件箱");
}
internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        if (Environment.GetCommandLineArgs().Contains("--self-test-drag"))
        {
            var showUi = !Environment.GetCommandLineArgs().Contains("--headless");
            try { DragInteropTest.Run(showUi); }
            catch (Exception ex) { File.WriteAllText("drag-self-test.txt", "FAIL: " + ex); if (showUi) MessageBox.Show(ex.ToString(), "拖放自检失败"); Environment.ExitCode = 1; }
            return;
        }
        var data = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LanBridge");
        var index = Array.IndexOf(Environment.GetCommandLineArgs(), "--profile");
        if (index >= 0 && index + 1 < Environment.GetCommandLineArgs().Length) data = System.IO.Path.GetFullPath(Environment.GetCommandLineArgs()[index + 1]);
        try
        {
            Directory.CreateDirectory(data);
            var settingsPath = System.IO.Path.Combine(data, "settings.json");
            var settings = File.Exists(settingsPath) ? JsonFiles.Read<Settings>(settingsPath) : new Settings();
            JsonFiles.Write(settingsPath, settings);
            using var identity = new Identity(data);
            using var store = new Store(settings.SharedFolder, identity);
            using var node = new Node(identity, store);
            using var queue = new TransferQueue(node);
            using var form = new MainForm(node, queue, settings);
            Application.Run(form);
        }
        catch (Exception ex)
        {
            MessageBox.Show("无法启动 LanBridge。若程序已经运行，请使用已打开的窗口。\n\n" + ex.Message,
                "LanBridge", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}
