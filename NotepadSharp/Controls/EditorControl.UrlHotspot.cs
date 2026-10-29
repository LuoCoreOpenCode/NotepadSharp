using System.Runtime.InteropServices;
using NotepadSharp.Services;
using ScintillaNET;

namespace NotepadSharp.Controls;

/// <summary>
/// URL 热点：照 ND2 scintillaeditview.cpp:3612-3686（addHotSpot）与 :2639-2675（mouseDoubleClickEvent）。
/// 高亮走 indicator 不是 styling，扫描只覆盖可视区，每次 SCN_UPDATEUI 无条件重算。
/// 位置一律用原生消息取 UTF-8 字节偏移：ScintillaNET 的托管属性（Lines/GetTextRange/IndicatorFillRange…）
/// 按 UTF-16 码元换算，和 ND2 的字节口径混用会随中文漂移，所以这里一条托管位置 API 都不碰。
/// </summary>
public partial class EditorControl
{
    /// <summary>
    /// ND2 用的是 URL_INDIC = 8（scintillaeditview.h:97），但 8 在本项目里是查找高亮专用
    /// （EditorControl.cs:24），9/10 是括号配对，21..25 是标记色 → 热点改用 11。
    /// 换号不影响行为，只是同一格 indicator 的归属不同，算刻意偏离。
    /// </summary>
    private const int UrlIndicatorIndex = 11;

    // 消息号取自 ND2 的 Scintilla.h
    private const int SCI_GETCURRENTPOS = 2008;
    private const int SCI_POSITIONFROMPOINT = 2022;
    private const int SCI_GETLINEENDPOSITION = 2136;
    private const int SCI_SETSEL = 2160;
    private const int SCI_LINEFROMPOSITION = 2166;
    private const int SCI_POSITIONFROMLINE = 2167;
    private const int SCI_STYLEGETFORE = 2481;
    private const int SCI_SETINDICATORCURRENT = 2500;
    private const int SCI_SETINDICATORVALUE = 2502;
    private const int SCI_INDICATORFILLRANGE = 2504;
    private const int SCI_INDICATORCLEARRANGE = 2505;
    private const int SCI_INDICATORALLONFOR = 2506;
    private const int SCI_INDICATORSTART = 2508;
    private const int SCI_INDICATOREND = 2509;
    private const int SCI_GETCHARACTERPOINTER = 2520;
    private const int STYLE_DEFAULT = 32;

    /// <summary>
    /// 进程级开关，对应 ND2 的静态位 CCNotePad::s_hightWebAddr（ccnotepad.cpp:466，默认 0）。
    /// ND2 只在启动读一次（:1369-1373），勾选切换时只改这个位 + 落盘（:9880-9885），
    /// 既不刷新已存在的编辑器，也不清除已画出的范围 —— 关掉后残影留着，是它的原有行为，照抄。
    /// </summary>
    public static bool HighlightWebAddr { get; set; }

    private int SciMsg(int msg, int wParam, int lParam)
        => _scintilla.DirectMessage(msg, (IntPtr)wParam, (IntPtr)lParam).ToInt32();

    /// <summary>按原生字节偏移取一段文本（ND2 里就是 QString::mid，索引是 QChar，这里先按字节切再解码）</summary>
    private string GetTextBytes(int start, int length)
    {
        if (length <= 0) return string.Empty;
        var ptr = _scintilla.DirectMessage(SCI_GETCHARACTERPOINTER, IntPtr.Zero, IntPtr.Zero);
        if (ptr == IntPtr.Zero) return string.Empty;
        // 文档缓冲在 UTF-8 代码页下就是 UTF-8 字节流；指针只在文档改动前有效，这里只读不写。
        // 不用 PtrToStringUTF8：它遇到 NUL 就截断，文档里真有 \0 时会少扫一截
        var bytes = new byte[length];
        Marshal.Copy(ptr + start, bytes, 0, length);
        return System.Text.Encoding.UTF8.GetString(bytes);
    }

    /// <summary>
    /// 每次 UPDATEUI 都会走这里（ND2 在 handleUpdateUI:3763 无条件调用）。
    /// 开关关着时整段直接 return，什么都不做。
    /// </summary>
    private void AddHotSpot()
    {
        if (!HighlightWebAddr) return;

        // ND2 :3619-3628：先读当前值，和目标值不同才下发
        var ind = _scintilla.Indicators[UrlIndicatorIndex];
        if (ind.Style != IndicatorStyle.Plain || ind.Alpha != 70 || ind.Flags != IndicatorFlags.ValueFore)
        {
            ind.Style = IndicatorStyle.Plain;
            ind.Alpha = 70;                 // SCI_INDICSETALPHA
            ind.Flags = IndicatorFlags.ValueFore;   // SCI_INDICSETFLAGS + SC_INDICFLAG_VALUEFORE
            // ND2 这里还把 hover 风格设成 INDIC_EXPLORERLINK(22)（:3625），但它自带的 Scintilla 没有这个
            // 风格（Scintilla.h 里最大只到 INDIC_GRADIENTCENTRE=21），设进去等于没设。
            // 我们不设 hover 风格，落到的还是同一个默认值，屏幕上的效果一致。
        }

        // 可视区起止（ND2 getVisibleStartAndEndPosition :3330-3342，用 POSITIONFROMPOINT 取四角对应的位置）。
        // ND2 传的是 QRect 的 right()/bottom()，Qt 里那是「闭区间」的最后像素（宽-1 / 高-1）；
        // .NET 的 Rectangle.Right/Bottom 是开区间，直接传会落到客户区外一格，所以这里 -1。
        int pos = SciMsg(SCI_POSITIONFROMPOINT, 0, 0);
        int startPos = SciMsg(SCI_POSITIONFROMLINE, SciMsg(SCI_LINEFROMPOSITION, pos, 0), 0);
        var rc = _scintilla.ClientRectangle;
        pos = SciMsg(SCI_POSITIONFROMPOINT, rc.Right - 1, rc.Bottom - 1);
        int endPos = SciMsg(SCI_GETLINEENDPOSITION, SciMsg(SCI_LINEFROMPOSITION, pos, 0), 0);
        if (startPos >= endPos) return;

        SciMsg(SCI_SETINDICATORCURRENT, UrlIndicatorIndex, 0);

        // 颜色取正文默认前景：FLAGS=VALUEFORE 时，画出来的颜色就是下面这个 value（ND2 :3645-3646）
        int indicFore = SciMsg(SCI_STYLEGETFORE, STYLE_DEFAULT, 0);
        SciMsg(SCI_SETINDICATORVALUE, indicFore, 0);

        string text = GetTextBytes(startPos, endPos - startPos);
        if (text.Length == 0) return;

        // ND2 :3663-3681：码元下标往前推，字节下标按每段的 UTF-8 长度同步推
        int startWide = 0;
        int startEncoded = 0;
        while (true)
        {
            bool r = UrlScanner.IsUrl(text, startWide, out int lenWide);
            if (lenWide <= 0) break;

            int lenEncoded = UrlScanner.Utf8Bytes(text, startWide, lenWide);
            if (r)
                SciMsg(SCI_INDICATORFILLRANGE, startEncoded + startPos, lenEncoded);
            else
                SciMsg(SCI_INDICATORCLEARRANGE, startEncoded + startPos, lenEncoded);

            startWide += lenWide;
            startEncoded += lenEncoded;

            if (startWide >= text.Length || startEncoded + startPos >= endPos) break;
        }
    }

    /// <summary>
    /// 双击打开：ND2 mouseDoubleClickEvent（:2639-2675）。判定顺序照它抄——
    /// 开关关着不拦、光标处没有本号 indicator 不拦、位置落在范围外不拦、URL 不合法不拦；
    /// 命中就先把双击自动选中的那个词取消掉，再交给系统浏览器。
    /// </summary>
    private void OnEditorDoubleClick()
    {
        if (!HighlightWebAddr) return;

        int position = SciMsg(SCI_GETCURRENTPOS, 0, 0);
        if ((SciMsg(SCI_INDICATORALLONFOR, position, 0) & (1 << UrlIndicatorIndex)) == 0) return;

        int start = SciMsg(SCI_INDICATORSTART, UrlIndicatorIndex, position);
        int end = SciMsg(SCI_INDICATOREND, UrlIndicatorIndex, position);
        if (position < start || position > end) return;

        // 撤销双击顺带选中的词，免得等浏览器起来的过程中一直亮着词选区
        SciMsg(SCI_SETSEL, position, position);

        string url = GetTextBytes(start, end - start);
        if (!UrlScanner.IsValidUrl(url)) return;

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true,
            });
        }
        catch
        {
            // ND2 的 QDesktopServices::openUrl 失败也是不吭声
        }
    }
}
