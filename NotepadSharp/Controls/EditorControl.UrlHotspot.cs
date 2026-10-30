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

    // 消息号取自 ND2 的 Scintilla.h（下面每个常量的数字就是该头文件里的 SCI_xxx 编号）
    /// <summary>SCI_GETCURRENTPOS：取当前光标位置（UTF-8 字节偏移）</summary>
    private const int SCI_GETCURRENTPOS = 2008;
    /// <summary>SCI_POSITIONFROMPOINT：把客户区像素坐标换算成文档位置；点在窗口外返回 -1</summary>
    private const int SCI_POSITIONFROMPOINT = 2022;
    /// <summary>SCI_GETLINEENDPOSITION：某行的行尾位置（不含换行符）</summary>
    private const int SCI_GETLINEENDPOSITION = 2136;
    /// <summary>SCI_SETSEL：设置选区，wParam=锚点、lParam=光标；两者相等即把光标落点清空选区</summary>
    private const int SCI_SETSEL = 2160;
    /// <summary>SCI_LINEFROMPOSITION：文档位置 → 所在行号</summary>
    private const int SCI_LINEFROMPOSITION = 2166;
    /// <summary>SCI_POSITIONFROMLINE：行号 → 该行行首的文档位置</summary>
    private const int SCI_POSITIONFROMLINE = 2167;
    /// <summary>SCI_STYLEGETFORE：取某个样式的前景色（0x00BBGGRR），用来给热点取线色</summary>
    private const int SCI_STYLEGETFORE = 2481;
    /// <summary>SCI_SETINDICATORCURRENT：选定后面填/擦范围操作的是哪个 indicator 号</summary>
    private const int SCI_SETINDICATORCURRENT = 2500;
    /// <summary>SCI_SETINDICATORVALUE：设 indicator 的值；FLAGS 带 VALUEFORE 时这个值就是画出来的颜色</summary>
    private const int SCI_SETINDICATORVALUE = 2502;
    /// <summary>SCI_INDICATORFILLRANGE：给 [position, position+length) 画上 indicator</summary>
    private const int SCI_INDICATORFILLRANGE = 2504;
    /// <summary>SCI_INDICATORCLEARRANGE：擦掉 [position, position+length) 的 indicator</summary>
    private const int SCI_INDICATORCLEARRANGE = 2505;
    /// <summary>SCI_INDICATORALLONFOR：返回该位置上画了 indicator 的位掩码，按位与判断本号在不在</summary>
    private const int SCI_INDICATORALLONFOR = 2506;
    /// <summary>SCI_INDICATORSTART：给定位置所在那段 indicator 的起点</summary>
    private const int SCI_INDICATORSTART = 2508;
    /// <summary>SCI_INDICATOREND：给定位置所在那段 indicator 的终点</summary>
    private const int SCI_INDICATOREND = 2509;
    /// <summary>SCI_GETCHARACTERPOINTER：返回文档缓冲首地址，可直接按字节读（文档改动后即失效）</summary>
    private const int SCI_GETCHARACTERPOINTER = 2520;
    /// <summary>STYLE_DEFAULT：正文默认样式号，热点颜色跟着它走才能保证换主题也不错</summary>
    private const int STYLE_DEFAULT = 32;

    /// <summary>
    /// 进程级开关，对应 ND2 的静态位 CCNotePad::s_hightWebAddr（ccnotepad.cpp:466，默认 0）。
    /// ND2 只在启动读一次（:1369-1373），勾选切换时只改这个位 + 落盘（:9880-9885），
    /// 既不刷新已存在的编辑器，也不清除已画出的范围 —— 关掉后残影留着，是它的原有行为，照抄。
    /// </summary>
    public static bool HighlightWebAddr { get; set; }

    /// <summary>
    /// 发一条 Scintilla 原生消息并把返回值按 Int32 取出来。
    /// 本文件所有位置读写都走它，保证口径统一是原生 UTF-8 字节偏移（见类型摘要）。
    /// </summary>
    /// <param name="msg">消息号，取自上面的 SCI_xxx 常量</param>
    /// <param name="wParam">第一个参数，语义随消息号而定（多为位置或 indicator 号）</param>
    /// <param name="lParam">第二个参数，语义随消息号而定</param>
    /// <returns>消息返回值（多为文档位置；出错或不适用时为 -1）</returns>
    private int SciMsg(int msg, int wParam, int lParam)
        => _scintilla.DirectMessage(msg, (IntPtr)wParam, (IntPtr)lParam).ToInt32();

    /// <summary>按原生字节偏移取一段文本（ND2 里就是 QString::mid，索引是 QChar，这里先按字节切再解码）</summary>
    /// <param name="start">起始的 UTF-8 字节偏移</param>
    /// <param name="length">要取的字节数</param>
    /// <returns>解码后的字符串；长度非正或拿不到缓冲时为空串</returns>
    private string GetTextBytes(int start, int length)
    {
        if (length <= 0) return string.Empty;   // 空区间不必去要指针
        var ptr = _scintilla.DirectMessage(SCI_GETCHARACTERPOINTER, IntPtr.Zero, IntPtr.Zero);   // 文档缓冲首地址
        if (ptr == IntPtr.Zero) return string.Empty;   // 极个别时机拿不到指针，安全退让
        // 文档缓冲在 UTF-8 代码页下就是 UTF-8 字节流；指针只在文档改动前有效，这里只读不写。
        // 不用 PtrToStringUTF8：它遇到 NUL 就截断，文档里真有 \0 时会少扫一截
        var bytes = new byte[length];
        Marshal.Copy(ptr + start, bytes, 0, length);   // 按字节偏移直接拷一段出来
        return System.Text.Encoding.UTF8.GetString(bytes);   // 整段按 UTF-8 解码；调用方传的边界都落在行/URL 段上，正常不会把多字节字符切一半
    }

    /// <summary>
    /// 每次 UPDATEUI 都会走这里（ND2 在 handleUpdateUI:3763 无条件调用）。
    /// 开关关着时整段直接 return，什么都不做。
    /// </summary>
    private void AddHotSpot()
    {
        if (!HighlightWebAddr) return;   // 开关关着时连擦除都不下发——已画出的残影留着，与 ND2 一致（见 HighlightWebAddr 注释）

        // ND2 :3619-3628：先读当前值，和目标值不同才下发
        var ind = _scintilla.Indicators[UrlIndicatorIndex];
        if (ind.Style != IndicatorStyle.Plain || ind.Alpha != 70 || ind.Flags != IndicatorFlags.ValueFore)
        {
            ind.Style = IndicatorStyle.Plain;
            ind.Alpha = 70;                 // SCI_INDICSETALPHA：70/255 的透明度，和 ND2 一致
            ind.Flags = IndicatorFlags.ValueFore;   // SCI_INDICSETFLAGS + SC_INDICFLAG_VALUEFORE：线的颜色取自 value
            // ND2 这里还把 hover 风格设成 INDIC_EXPLORERLINK(22)（:3625），但它自带的 Scintilla 没有这个
            // 风格（Scintilla.h 里最大只到 INDIC_GRADIENTCENTRE=21），设进去等于没设。
            // 我们不设 hover 风格，落到的还是同一个默认值，屏幕上的效果一致。
        }

        // 可视区起止（ND2 getVisibleStartAndEndPosition :3330-3342，用 POSITIONFROMPOINT 取四角对应的位置）。
        // ND2 传的是 QRect 的 right()/bottom()，Qt 里那是「闭区间」的最后像素（宽-1 / 高-1）；
        // .NET 的 Rectangle.Right/Bottom 是开区间，直接传会落到客户区外一格，所以这里 -1。
        int pos = SciMsg(SCI_POSITIONFROMPOINT, 0, 0);                                            // 客户区左上角对应的文档位置
        int startPos = SciMsg(SCI_POSITIONFROMLINE, SciMsg(SCI_LINEFROMPOSITION, pos, 0), 0);      // 归到那一行的行首，扫描从这里开始
        var rc = _scintilla.ClientRectangle;                                                      // 客户区像素矩形，只取它的右下角落点
        pos = SciMsg(SCI_POSITIONFROMPOINT, rc.Right - 1, rc.Bottom - 1);                         // 客户区右下角对应的文档位置
        int endPos = SciMsg(SCI_GETLINEENDPOSITION, SciMsg(SCI_LINEFROMPOSITION, pos, 0), 0);      // 归到那一行的正文末尾，扫描到这里为止
        if (startPos >= endPos) return;                                                           // 空文档或可视区退化成一格，没什么可扫

        SciMsg(SCI_SETINDICATORCURRENT, UrlIndicatorIndex, 0);                                    // 后面两次填/擦都作用在热点这个号上

        // 颜色取正文默认前景：FLAGS=VALUEFORE 时，画出来的颜色就是下面这个 value（ND2 :3645-3646）
        int indicFore = SciMsg(SCI_STYLEGETFORE, STYLE_DEFAULT, 0);
        SciMsg(SCI_SETINDICATORVALUE, indicFore, 0);

        string text = GetTextBytes(startPos, endPos - startPos);   // 只把可视区这一段取出来扫，长文档不会跟着变慢
        if (text.Length == 0) return;   // 可视区取不到正文（首屏未布局等），本轮什么都不下发

        // ND2 :3663-3681：码元下标往前推，字节下标按每段的 UTF-8 长度同步推
        int startWide = 0;        // text 内的 UTF-16 码元游标（UrlScanner 用的口径）
        int startEncoded = 0;     // 同一段起点对应的 UTF-8 字节游标（Scintilla 用的口径）
        while (true)
        {
            // 判游标处是不是 URL：是 → lenWide 是 URL 长度；不是 → lenWide 是「可以放心跳过的非 URL 长度」
            bool r = UrlScanner.IsUrl(text, startWide, out int lenWide);
            if (lenWide <= 0) break;      // 状态机走到尽头（或返回了 0 步长），必须退出，否则死循环

            int lenEncoded = UrlScanner.Utf8Bytes(text, startWide, lenWide);   // 本段的 UTF-8 字节数，填/擦范围按它给
            // 是 URL 就画线，不是就把这段已经画过的线擦掉——两段都要动手，
            // 滚动后旧可视区残留的高亮才清得掉（ND2 同样是 fill/clear 成对下发）
            if (r)
                SciMsg(SCI_INDICATORFILLRANGE, startEncoded + startPos, lenEncoded);
            else
                SciMsg(SCI_INDICATORCLEARRANGE, startEncoded + startPos, lenEncoded);

            // 两个口径各自按本段长度推进：码元游标走 lenWide，字节游标走 lenEncoded，不能混用
            startWide += lenWide;
            startEncoded += lenEncoded;

            if (startWide >= text.Length || startEncoded + startPos >= endPos) break;   // 双保险收口，防止越出可视区
        }
    }

    /// <summary>
    /// 双击打开：ND2 mouseDoubleClickEvent（:2639-2675）。判定顺序照它抄——
    /// 开关关着不拦、光标处没有本号 indicator 不拦、位置落在范围外不拦、URL 不合法不拦；
    /// 命中就先把双击自动选中的那个词取消掉，再交给系统浏览器。
    /// </summary>
    private void OnEditorDoubleClick()
    {
        if (!HighlightWebAddr) return;   // 开关关着时这里一律不拦，双击还是正常的「选词」

        // 光标位置（字节偏移）上必须画着本号的 indicator，否则这一双击与热点无关
        int position = SciMsg(SCI_GETCURRENTPOS, 0, 0);
        if ((SciMsg(SCI_INDICATORALLONFOR, position, 0) & (1 << UrlIndicatorIndex)) == 0) return;

        // 取这一段 indicator 的边界；位置落在范围外（indicator 已过期）也当没命中
        int start = SciMsg(SCI_INDICATORSTART, UrlIndicatorIndex, position);
        int end = SciMsg(SCI_INDICATOREND, UrlIndicatorIndex, position);
        if (position < start || position > end) return;

        // 撤销双击顺带选中的词，免得等浏览器起来的过程中一直亮着词选区
        SciMsg(SCI_SETSEL, position, position);

        // 按字节取出这段文本再校验：非 http(s)、缺主机名的都算不上可打开的 URL（ND2 用 QUrl::isValid）
        string url = GetTextBytes(start, end - start);
        if (!UrlScanner.IsValidUrl(url)) return;

        try
        {
            // UseShellExecute=true 走系统外壳，等于 ND2 的 QDesktopServices::openUrl：交给默认浏览器
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
