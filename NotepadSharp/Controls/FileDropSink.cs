using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace NotepadSharp.Controls;

/// <summary>
/// 编辑器子窗口的外部文件拖放接收器。
///
/// Scintilla 在子窗口上注册了自己的 OLE IDropTarget（负责编辑器内部的文本拖拽），
/// WinForms 的 <see cref="Control.AllowDrop"/> 抢不到它（实测 DragEnter 根本不触发），
/// 所以这里走传统的 shell 拖放通道：给窗口挂 <c>DragAcceptFiles</c>，
/// 外部文件在它上面松手时系统会投递 <c>WM_DROPFILES</c>，由本类解析出文件路径。
/// 不动 Scintilla 的 IDropTarget，编辑器内拖拽文本的行为不受影响。
/// </summary>
internal sealed class FileDropSink : NativeWindow
{
    private const int WM_DROPFILES = 0x0233;

    [DllImport("shell32.dll")]
    private static extern void DragAcceptFiles(IntPtr hWnd, bool accept);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint DragQueryFileW(IntPtr hDrop, uint iFile, StringBuilder? buffer, uint bufferCount);

    [DllImport("shell32.dll")]
    private static extern void DragFinish(IntPtr hDrop);

    /// <summary>有文件被拖到编辑器上并松手（参数为文件/目录路径）</summary>
    public event Action<string[]>? FilesDropped;

    private readonly Control _target;

    public FileDropSink(Control target)
    {
        _target = target;
        AssignHandle(target.Handle);
        DragAcceptFiles(Handle, true);
        target.HandleDestroyed += OnHandleDestroyed;
    }

    private void OnHandleDestroyed(object? sender, EventArgs e) => Release();

    /// <summary>摘下窗口子类化（句柄重建或控件销毁时调用，可重复调用）</summary>
    public void Release()
    {
        _target.HandleDestroyed -= OnHandleDestroyed;
        try { ReleaseHandle(); } catch { /* 句柄已随控件销毁 */ }
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_DROPFILES)
        {
            var paths = ReadPaths(m.WParam);
            if (paths.Length > 0) FilesDropped?.Invoke(paths);
            return;
        }
        base.WndProc(ref m);
    }

    private static string[] ReadPaths(IntPtr hDrop)
    {
        if (hDrop == IntPtr.Zero) return Array.Empty<string>();
        try
        {
            uint count = DragQueryFileW(hDrop, 0xFFFFFFFF, null, 0);
            var paths = new List<string>((int)count);
            for (uint i = 0; i < count; i++)
            {
                uint length = DragQueryFileW(hDrop, i, null, 0);
                if (length == 0) continue;
                var sb = new StringBuilder((int)length + 1);
                if (DragQueryFileW(hDrop, i, sb, (uint)sb.Capacity) == 0) continue;
                paths.Add(sb.ToString());
            }
            return paths.ToArray();
        }
        finally { DragFinish(hDrop); }
    }
}
