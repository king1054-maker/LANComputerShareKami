using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using LanBridge.Core;

namespace LanBridge.Windows;

internal static class DragInteropTest
{
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint DragQueryFileW(IntPtr drop, uint index, StringBuilder? file, uint length);
    [DllImport("ole32.dll")]
    private static extern void ReleaseStgMedium(ref STGMEDIUM medium);
    public static void Run(bool showUi = true)
    {
        int calls = 0; var path = Path.Combine(Path.GetTempPath(), "LanBridge-drag-test.txt"); File.WriteAllText(path, "test");
        try
        {
            var lazy = new DeferredDragData(new DragBundle("test", [new("test", "test", false, 4, "v", DateTime.UtcNow)]),
                () => { calls++; return [path]; });
            var data = new DataObject(lazy);
            System.Windows.Forms.IDataObject managed = data;
            if (managed.GetData(DeferredDragData.Format) is not string || calls != 0) throw new Exception("Internal drag format caused materialization.");
            var com = (System.Runtime.InteropServices.ComTypes.IDataObject)data;
            var format = new FORMATETC { cfFormat = 15, dwAspect = DVASPECT.DVASPECT_CONTENT, lindex = -1, tymed = TYMED.TYMED_HGLOBAL };
            if (com.QueryGetData(ref format) != 0 || calls != 0) throw new Exception("CF_HDROP format was not advertised lazily.");
            com.GetData(ref format, out var medium);
            try
            {
                var text = new StringBuilder(1024); DragQueryFileW(medium.unionmember, 0, text, 1024);
                if (text.ToString() != path || calls != 1) throw new Exception("CF_HDROP did not return the real file exactly once.");
            }
            finally { ReleaseStgMedium(ref medium); }
            File.WriteAllText("drag-self-test.txt", "PASS: internal JSON; lazy CF_HDROP query; COM file path materialization.\nReal Explorer drop still requires manual acceptance.");
            if (showUi) MessageBox.Show("拖放 COM 自检通过。结果保存在 drag-self-test.txt。", "LanBridge 自检");
        }
        finally { File.Delete(path); }
    }
}
