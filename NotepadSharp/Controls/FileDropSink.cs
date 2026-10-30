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
    /// <summary>shell 拖放通道投递给窗口消息的编号：wParam 是 HDROP 句柄</summary>
    private const int WM_DROPFILES = 0x0233;

    /// <summary>DragAcceptFiles：告诉 shell 这个窗口接不接受文件拖放；accept=false 时撤销登记</summary>
    /// <param name="hWnd">目标窗口句柄</param>
    /// <param name="accept">true = 接收，窗口会因此收到 WM_DROPFILES</param>
    [DllImport("shell32.dll")]
    private static extern void DragAcceptFiles(IntPtr hWnd, bool accept);

    /// <summary>
    /// DragQueryFileW：从 HDROP 里问/取拖放项。
    /// iFile=0xFFFFFFFF 且 buffer=null 时返回文件总数；iFile=序号且 buffer=null 时返回该路径的字符数；
    /// 传了 buffer 就把路径（不带引号的 Win32 形式）写进去并返回写入的字符数。
    /// </summary>
    /// <param name="hDrop">WM_DROPFILES 的 wParam</param>
    /// <param name="iFile">要问第几个文件，或 0xFFFFFFFF 表示问总数</param>
    /// <param name="buffer">接收路径的缓冲区；为 null 时只做询问</param>
    /// <param name="bufferCount">缓冲区容量（字符数）</param>
    /// <returns>问总数时返回个数；问长度时返回字符数（不含终止符）；取文本时返回写入的字符数，失败为 0</returns>
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint DragQueryFileW(IntPtr hDrop, uint iFile, StringBuilder? buffer, uint bufferCount);

    /// <summary>DragFinish：释放 HDROP 及其内部缓冲区，处理完一次拖放必须调一次，否则泄漏</summary>
    /// <param name="hDrop">WM_DROPFILES 的 wParam</param>
    [DllImport("shell32.dll")]
    private static extern void DragFinish(IntPtr hDrop);

    /// <summary>有文件被拖到编辑器上并松手（参数为文件/目录路径）</summary>
    public event Action<string[]>? FilesDropped;

    /// <summary>被挂载的控件（这里是 Scintilla 的 WinForms 包装），只为在它销毁时摘掉自己</summary>
    private readonly Control _target;

    /// <summary>
    /// 挂到 <paramref name="target"/> 的窗口句柄上开始接收拖放。
    /// 调用前必须保证句柄已创建，否则 <see cref="Control.Handle"/> 会当场把控件句柄建出来。
    /// </summary>
    /// <param name="target">要接收外部文件拖放的控件</param>
    public FileDropSink(Control target)
    {
        _target = target;
        AssignHandle(target.Handle);                       // NativeWindow 子类化：WndProc 接管这个句柄的消息链
        DragAcceptFiles(Handle, true);                     // 向 shell 登记「本窗口接受文件拖放」
        target.HandleDestroyed += OnHandleDestroyed;       // 句柄重建（浮动/停靠、控件销毁）时自动摘干净
    }

    /// <summary>控件句柄销毁的回调：本对象随之失效，直接释放子类化</summary>
    /// <param name="sender">触发事件的控件</param>
    /// <param name="e">无实际内容</param>
    private void OnHandleDestroyed(object? sender, EventArgs e) => Release();

    /// <summary>摘下窗口子类化（句柄重建或控件销毁时调用，可重复调用）</summary>
    public void Release()
    {
        _target.HandleDestroyed -= OnHandleDestroyed;
        try { ReleaseHandle(); } catch { /* 句柄已随控件销毁 */ }
    }

    /// <summary>
    /// 接管后的窗口过程：只截 WM_DROPFILES，其余消息原样交给上一级 WndProc（也就是 Scintilla 自己），
    /// 因此编辑器内部的文本拖拽、键盘输入都不受影响。
    /// </summary>
    /// <param name="m">窗口消息</param>
    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_DROPFILES)
        {
            // 一次拖放可能有多个文件；解析出来非空才对外通知，空拖盘（比如从桌面拖空白）直接吞掉
            var paths = ReadPaths(m.WParam);
            if (paths.Length > 0) FilesDropped?.Invoke(paths);
            return;   // 已处理，不再往下传，免得 Scintilla 把这条消息当成自己的内部拖拽
        }
        base.WndProc(ref m);   // 其它消息原样放行
    }

    /// <summary>
    /// 把 HDROP 里的路径逐条抄成托管字符串数组。
    /// 分两步查：先问长度再按长度开 <see cref="StringBuilder"/>，避免按固定上限截断超长路径（如 UNC + 深目录）。
    /// </summary>
    /// <param name="hDrop">WM_DROPFILES 的 wParam（HDROP 句柄）</param>
    /// <returns>拖放项的路径列表；句柄无效或没解析出任何项时为空数组</returns>
    private static string[] ReadPaths(IntPtr hDrop)
    {
        if (hDrop == IntPtr.Zero) return Array.Empty<string>();
        try
        {
            // 0xFFFFFFFF 是「问有几个文件」的约定值
            uint count = DragQueryFileW(hDrop, 0xFFFFFFFF, null, 0);
            var paths = new List<string>((int)count);
            for (uint i = 0; i < count; i++)
            {
                uint length = DragQueryFileW(hDrop, i, null, 0);        // 先问第 i 项要多少字符
                if (length == 0) continue;                              // 空项/取不到，跳过
                var sb = new StringBuilder((int)length + 1);            // +1 留给结尾的 NUL
                if (DragQueryFileW(hDrop, i, sb, (uint)sb.Capacity) == 0) continue;   // 返回 0 表示没写进去
                paths.Add(sb.ToString());
            }
            return paths.ToArray();
        }
        finally { DragFinish(hDrop); }   // 无论解析成败都要释放 HDROP，这是 shell 拖放的硬性要求
    }
}
