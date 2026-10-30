using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using NotepadSharp.Services;
using ScintillaNET;

namespace NotepadSharp.Controls;

/// <summary>
/// 编辑器行 / 列批量操作，对应「格式」菜单。
/// 作用域分三类，按 ND2 的原样：**行级整备**（排序 / 反排 / 去重）走 <see cref="CurrentLineScope"/>
/// ——多光标只有矩形选区才处理且按列切片、单行选区不动作、无选区处理全文；
/// **静默正则类**（移除空行 / 去除行首尾空白 / TAB 互转）永远无视选区处理整篇；
/// 剩下标了「本地增强」的才用 <see cref="WorkLineRange"/>（有跨行选区只处理选区行，否则全文）。
/// 批量改动都包在一个 undo action 内，可一次撤销。
/// </summary>
public partial class EditorControl
{
    // ==================== 标记索引 ====================
    // Scintilla 的 marker 槽位是全局共享的：0-19 里折叠占用了 6-12、书签占用了 2，
    // 所以差异底色与查找标记只能往后挑互不冲突的编号；改任何一个都要同步检查边距掩码。

    /// <summary>差异底色「新增行」槽位（对比窗口里表示「这一侧是新写的」），由 CompareWindow 使用</summary>
    public const int WriteMarkerIndex = 16;
    /// <summary>差异底色「修改行」槽位，两侧窗口共用同一编号</summary>
    public const int ModifyMarkerIndex = 18;
    /// <summary>差异底色「删除行」槽位</summary>
    public const int DeleteMarkerIndex = 20;
    /// <summary>查找结果整行标记的预留槽位（当前仅声明，实际查找高亮走 indicator）</summary>
    public const int FindMarkMarkerIndex = 24;
    /// <summary>ND2 书签边距用的 1 号标记位；本项目书签走 <see cref="BookmarkMarkerIndex"/>（2 号），此常量仅为对齐 ND2 编号表保留</summary>
    public const int Bookmark1MarkerIndex = 1;

    // ==================== 差异标记（文件对比窗口用） ====================

    /// <summary>
    /// 差异底色标记本身（SC_MARK_BACKGROUND 直接铺满整行，不占边距）。
    /// 底色 marker 只需配置一次，之后每行 MarkerAdd 即生效；由对比窗口在打开与刷新时调用。
    /// 三种颜色刻意与 ND2 对比窗口一致——新增偏绿、修改偏黄、删除偏橙红。
    /// </summary>
    public void ConfigureDiffMarkers()
    {
        // 三个槽位各自铺一种底色；FromArgb 的第 1 个 255 是 Alpha，底色不透明
        ConfigureDiffMarker(WriteMarkerIndex, System.Drawing.Color.FromArgb(255, 200, 235, 205));
        ConfigureDiffMarker(ModifyMarkerIndex, System.Drawing.Color.FromArgb(255, 250, 230, 155));
        ConfigureDiffMarker(DeleteMarkerIndex, System.Drawing.Color.FromArgb(255, 250, 200, 195));
    }

    /// <summary>
    /// 把指定 marker 槽位配置成「整行背景色」样式。
    /// </summary>
    /// <param name="index">marker 槽位号，取自本类的 *MarkerIndex 常量</param>
    /// <param name="color">要铺的底色；前景色同时设成同色，避免 Symbol 模式下露出异色图形</param>
    private void ConfigureDiffMarker(int index, System.Drawing.Color color)
    {
        var marker = _scintilla.Markers[index];
        // Background 类型不占用边距，直接给整行着色，与边距里的书签图标互不干扰
        marker.Symbol = MarkerSymbol.Background;
        marker.SetBackColor(color);
        marker.SetForeColor(color);
    }

    /// <summary>
    /// 给整行打差异底色。
    /// 行号越界时静默忽略——对比窗口在滚动/重算差异时可能拿着已经变了的行号回来调用，不能抛异常。
    /// </summary>
    /// <param name="line">0 起的文档行号</param>
    /// <param name="markerIndex">底色槽位，取 <see cref="WriteMarkerIndex"/> / <see cref="ModifyMarkerIndex"/> / <see cref="DeleteMarkerIndex"/></param>
    public void MarkDiffLine(int line, int markerIndex)
    {
        if (line < 0 || line >= _scintilla.Lines.Count) return;
        // MarkerAdd 是「或」语义：同一行叠多个底色不会互相顶掉，最后一层决定观感
        _scintilla.Lines[line].MarkerAdd(markerIndex);
    }

    /// <summary>
    /// 清掉全文所有行的 marker（底色 + 书签一起抹掉），用于重新做一次差异计算。
    /// MarkerAddSet(0) 是「用掩码覆盖整行 marker 集合」，传 0 就是清空。
    /// </summary>
    public void ClearDiffMarkers()
    {
        for (int i = 0; i < _scintilla.Lines.Count; i++)
        {
            _scintilla.Lines[i].MarkerAddSet(0u);
        }
    }

    // ==================== 工作行范围 ====================
    /// <summary>
    /// 要处理的行区间（含端点）；无跨行选区时为整个文档。
    /// 这是「本地增强」类操作的作用域口径，与 ND2 的 <see cref="CurrentLineScope"/> 不同：
    /// 这里单行选区也算「没选区」（走全文），返回的行号是文档行号，不是字节/码元位置。
    /// </summary>
    /// <returns>
    /// (First, Last) 闭区间行号；空文档返回 (0, -1)，调用方用 <c>last &lt; first</c> 判空并直接退出。
    /// </returns>
    private (int First, int Last) WorkLineRange()
    {
        int lineCount = _scintilla.Lines.Count;
        // Scintilla 的缓冲即使全空也至少有 1 行；真为 0 只可能是刚被清空的极端情况，给个空区间
        if (lineCount == 0) return (0, -1);

        // 两个端点各自折算成行号；SelectionStart/End 恒为「起 &lt; 止」的绝对位置，不需要再排序
        int anchorLine = _scintilla.LineFromPosition(_scintilla.SelectionStart);
        int caretLine = _scintilla.LineFromPosition(_scintilla.SelectionEnd);

        // 空选区（位置选区）或选区落在同一行内 → 视为「没选」，把整个文档当作工作范围
        if (_scintilla.SelectionStart == _scintilla.SelectionEnd || anchorLine == caretLine)
            return (0, lineCount - 1);

        // 跨行选区：只处理被选中的这几行（含首末两行整行）
        return (Math.Min(anchorLine, caretLine), Math.Max(anchorLine, caretLine));
    }

    /// <summary>
    /// 行起点（SCI_POSITIONFROMLINE）。
    /// 返回的是托管层的 UTF-16 码元位置，ScintillaNET 会自己换算成底层 UTF-8 字节偏移。
    /// </summary>
    /// <param name="line">0 起的行号，越界会由 ScintillaNET 抛索引异常，调用方需自行保证合法</param>
    /// <returns>该行第一个字符的码元位置（行首符之后）</returns>
    private int LineStartPos(int line) => _scintilla.Lines[line].Position;

    /// <summary>
    /// 行尾位置（含该行换行符，等于下一行起始）。
    /// 即「整行 + 行尾符」的第一个位置：末行没有下一行，退化为文档总长。
    /// 用它当替换区间右端会连带吃掉换行符，删除整行正是靠这一点；只想留着行尾请用
    /// <see cref="LineContentEndPos"/>。
    /// </summary>
    /// <param name="line">0 起的行号</param>
    /// <returns>下一行的起始码元位置；<paramref name="line"/> 为最后一行时返回 <c>TextLength</c></returns>
    private int LineEndPos(int line)
        => line + 1 < _scintilla.Lines.Count ? _scintilla.Lines[line + 1].Position : _scintilla.TextLength;

    /// <summary>
    /// 本行内容末尾，不含行尾符（SCI_GETLINEENDPOSITION）。
    /// ScintillaNET 的 Line.EndPosition 是 Position+Length、长度里带行尾符，直接用会多吃掉一个换行。
    /// 这里的下标都是托管 API 的 UTF-16 码元位（ScintillaNET 内部再换算成 UTF-8 字节），
    /// 别再套 GetByteCount，否则行里有中文就会多推几个位置。
    /// </summary>
    /// <param name="line">0 起的行号</param>
    /// <returns>该行最后一个内容字符之后的码元位置；空行时等于 <see cref="LineStartPos"/></returns>
    private int LineContentEndPos(int line)
    {
        int start = LineStartPos(line);
        // 整行（含行尾符）取回来再剪：Scintilla 的「行长度」把 \r\n 或 \n 算在内，无法直接区分
        string text = _scintilla.GetTextRange(start, LineEndPos(line) - start);
        int cut = text.Length;
        // 先剥 \n 再剥 \r：CRLF 会连着去掉两个字符，LFCR 之类怪异组合也能各自去掉一个
        if (cut > 0 && text[cut - 1] == '\n') cut--;
        if (cut > 0 && text[cut - 1] == '\r') cut--;
        return start + cut;
    }

    /// <summary>
    /// 整行文本（不含换行符）。
    /// 用于排序/去重/统计等「按行内容」的场合；返回的字符串长度是 UTF-16 码元数。
    /// </summary>
    /// <param name="line">0 起的行号</param>
    /// <returns>行内容（可能是空串，但绝不含 \r 或 \n）</returns>
    private string LineBody(int line)
    {
        int s = LineStartPos(line);
        // TrimEnd 只裁尾部这两个字符，不会误删行尾的空格或 TAB
        return _scintilla.GetTextRange(s, LineEndPos(line) - s).TrimEnd('\r', '\n');
    }

    /// <summary>
    /// 整行文本（含换行符），即 <see cref="LineStartPos"/> 到下一行起始之间的原始内容。
    /// 末行若文档不以行尾符结束，返回的就不带行尾符。
    /// </summary>
    /// <param name="line">0 起的行号</param>
    /// <returns>行内容 + 行尾符</returns>
    private string LineFull(int line)
    {
        int s = LineStartPos(line);
        return _scintilla.GetTextRange(s, LineEndPos(line) - s);
    }

    /// <summary>
    /// 当前文档的行尾符字符串（1-2 个字符），批量写回时必须用它拼接，
    /// 不能硬编码 "\n" 或 "\r\n"，否则会把用户的 EOL 风格改掉。
    /// EolMode 只记录 CrLf/Cr/Lf 三种；文档里混用行尾时以模式设置为准（默认走 _ 分支即 \r\n）。
    /// </summary>
    private string EolString => _scintilla.EolMode switch
    {
        Eol.Cr => "\r",
        Eol.Lf => "\n",
        _ => "\r\n",
    };

    /// <summary>
    /// 用给定的若干行文本替换 [first, last] 这段闭区间行。
    /// 替换区间右端用 <see cref="LineEndPos"/>（含行尾符），写回的每行都自带行尾符，
    /// 所以行数增减都不会残留旧行尾；传入空序列等价于删掉这几行。
    /// </summary>
    /// <param name="first">起始行号（含）</param>
    /// <param name="last">结束行号（含）；小于 <paramref name="first"/> 时不动作</param>
    /// <param name="lines">新的行内容集合，每项都不带行尾符</param>
    private void SetLineRange(int first, int last, IEnumerable<string> lines)
    {
        // 越界/空区间直接放弃：调用方拿到的是行号，非法说明选区已经被别的操作改过
        if (first < 0 || last < first) return;

        string[] arr = lines.ToArray();
        string text = string.Join(EolString, arr);
        // 末尾补一个行尾符，让最后一行和原文一样「以行尾结束」，避免拼接后与下一行粘连
        if (arr.Length > 0) text += EolString;

        // Target* + ReplaceTarget 是一次原子替换，同时保证只产生一个撤销动作内的编辑
        _scintilla.TargetStart = LineStartPos(first);
        _scintilla.TargetEnd = LineEndPos(last);
        _scintilla.ReplaceTarget(text);
    }

    /// <summary>
    /// 把一段批量改动包进一个 undo action，让用户按一次 Ctrl+Z 就能整体回退。
    /// 用 try/finally 是为了 <paramref name="body"/> 抛异常时也能配对调用 EndUndoAction，
    /// 否则 Scintilla 的撤销栈会一直停在「组内」状态。
    /// </summary>
    /// <param name="body">要原子化的编辑操作</param>
    private void WithUndoGroup(Action body)
    {
        _scintilla.BeginUndoAction();
        try { body(); }
        finally { _scintilla.EndUndoAction(); }
    }

    /// <summary>
    /// 读出工作行 → 变换 → 写回，「本地增强」类行操作统一走这条管线。
    /// 读的是 <see cref="LineBody"/>（不含行尾），写回时由 <see cref="SetLineRange"/> 统一补行尾，
    /// 所以变换函数只管纯文本行，不用操心 CRLF/LF 差异。
    /// </summary>
    /// <param name="transform">输入按文档顺序的行列表，返回新行列表；返回 null 表示放弃本次改动</param>
    private void TransformLines(Func<List<string>, List<string>?> transform)
    {
        // 作用域由选区决定：有跨行选区只处理选区行，否则整篇
        var (first, last) = WorkLineRange();
        if (last < first) return;

        var lines = new List<string>(last - first + 1);
        for (int i = first; i <= last; i++) lines.Add(LineBody(i));

        var result = transform(lines);
        // null = 变换方主动否决（例如认为不该动），保持文档原样且不产生空撤销组
        if (result == null) return;

        WithUndoGroup(() => SetLineRange(first, last, result));
    }

    // ==================== ND2 行操作作用域（dealLineSort / removeAnyDuplicateLines） ====================

    /// <summary>
    /// ND2 这类整段行操作的作用域，逐条照 <c>dealLineSort</c>（ccnotepad.cpp:9589）：
    /// 多光标只有矩形选区才处理并且按列切片，非矩形直接不动作；
    /// 普通选区必须跨行（单行选区 return），无选区才处理全文。
    /// </summary>
    /// <param name="Ok">作用域是否可用；false 表示按 ND2 的语义应直接不动作</param>
    /// <param name="HasSelection">是否来自普通（非矩形）跨行选区，决定排完要不要还原选区</param>
    /// <param name="FromLine">起始行号（含，文档行号）</param>
    /// <param name="ToLine">结束行号（含，文档行号）</param>
    /// <param name="FromColumn">矩形选区的左列（UTF-16 码元列，含虚拟空间）；非矩形时为 0</param>
    /// <param name="ToColumn">矩形选区的右列（不含）；为 0 表示整行参与比较</param>
    private readonly record struct LineScope(bool Ok, bool HasSelection, int FromLine, int ToLine, int FromColumn, int ToColumn);

    /// <summary>
    /// 解析当前 ND2 行操作作用域。三种情形互斥：
    /// 1) 多光标（Selections.Count &gt; 1）：必须是矩形选区，用矩形锚点/光标换算行列，并把列宽带出去做切片比较；
    /// 2) 单光标有选区：要求跨行，单行选区按 ND2 直接放弃；
    /// 3) 无选区：整个文档 [0, Lines.Count-1]。
    /// </summary>
    /// <returns>解析好的 <see cref="LineScope"/>；Ok=false 时其余字段无意义</returns>
    private LineScope CurrentLineScope()
    {
        if (_scintilla.Selections.Count > 1)
        {
            // 多光标但不是矩形（多个独立选区）：ND2 这里 return，什么都不做
            if (!IsRectangleSelection) return new LineScope(false, false, 0, 0, 0, 0);

            // 矩形选区的两个角：位置是码元绝对偏移，还要各自加上「虚拟空间」列数
            // （虚拟空间 = 光标停在一行文字末尾之后的空白区，那部分不在文档里，必须手工补上）
            int anchor = _scintilla.RectangularSelectionAnchor;
            int caret = _scintilla.RectangularSelectionCaret;
            int anchorLine = _scintilla.LineFromPosition(anchor);
            int caretLine = _scintilla.LineFromPosition(caret);
            int anchorOffset = anchor - LineStartPos(anchorLine) + _scintilla.RectangularSelectionAnchorVirtualSpace;
            int caretOffset = caret - LineStartPos(caretLine) + _scintilla.RectangularSelectionCaretVirtualSpace;

            // 行、列各自取 min/max 归一成「左上-右下」，HasSelection=false：排完不还原普通选区
            return new LineScope(true, false,
                Math.Min(anchorLine, caretLine), Math.Max(anchorLine, caretLine),
                Math.Min(anchorOffset, caretOffset), Math.Max(anchorOffset, caretOffset));
        }

        int selStart = _scintilla.SelectionStart;
        int selEnd = _scintilla.SelectionEnd;
        if (selStart != selEnd)
        {
            var (first, last) = SelectionLinesRange();
            if (first == last) return new LineScope(false, true, 0, 0, 0, 0);   // One single line selection is not allowed.
            // 普通跨行选区：整行参与，列边界留 0（SortKey 里 toColumn==0 即「不切片」）
            return new LineScope(true, true, first, last, 0, 0);
        }

        // 无选区 → 全文；末行是 Lines.Count-1（哪怕最后一个位置是个空行）
        return new LineScope(true, false, 0, _scintilla.Lines.Count - 1, 0, 0);
    }

    /// <summary>
    /// SCI_SELECTIONISRECTANGLE。ScintillaNET 没有把它包成属性，只能走 DirectMessage。
    /// 返回非 0 表示当前处于列块（矩形）选择模式，与「有几个选区」是两件独立的事。
    /// </summary>
    private bool IsRectangleSelection
        => _scintilla.DirectMessage(2372, IntPtr.Zero, IntPtr.Zero).ToInt64() != 0;

    /// <summary>
    /// ND2 getSelectionLinesRange：选区尾正好落在下一行行首时，不算进下一行。
    /// 典型场景是双击/三击整行或 Shift+下箭头——选区末端是下一行的 Position，
    /// 但那一行一个字符都没被选中，若计入就会多排一行。
    /// 注意这里没有做 min/max 归一：SelectionStart/End 已是有序位置，line1 恒不大于 line2。
    /// </summary>
    /// <returns>(First, Last) 闭区间行号；空选区时两者都是光标所在行</returns>
    private (int First, int Last) SelectionLinesRange()
    {
        int line1 = _scintilla.LineFromPosition(_scintilla.SelectionStart);
        int line2 = _scintilla.LineFromPosition(_scintilla.SelectionEnd);
        if (line1 != line2 && LineStartPos(line2) == _scintilla.SelectionEnd) line2--;
        return (line1, line2);
    }

    /// <summary>
    /// ND2 的 sortLines 与 removeAnyDuplicateLines 是同一条管线（scintillaeditview.cpp:2932/3051）：
    /// 按文档行尾符切段 → 变换 → 拼回，整篇时不补尾行、选区段要把被 split 吃掉的最后一个空段还回去。
    /// 与 <see cref="TransformLines"/> 的区别：这里直接替换整段原始文本（含行尾），不做「每行补行尾」的规范化，
    /// 因此能保住原文的行尾风格与末尾有没有换行的状态。
    /// </summary>
    /// <param name="scope"><see cref="CurrentLineScope"/> 解析出的作用域；Ok=false 或首末行相同则不动作</param>
    /// <param name="transform">输入切出来的行段（可能含空段），返回新的行段列表</param>
    private void RewriteLineScope(LineScope scope, Func<List<string>, List<string>> transform)
    {
        // FromLine >= ToLine 意味着作用域不足两行（单行没什么可排/可去重），与 ND2 一样不动作
        if (!scope.Ok || scope.FromLine >= scope.ToLine) return;

        // 整段区间：从首行行首到末行「含行尾符」的位置，取的是原始文本
        int startPos = LineStartPos(scope.FromLine);
        int endPos = LineEndPos(scope.ToLine);
        string text = _scintilla.GetTextRange(startPos, endPos - startPos);
        string eol = EolString;

        // 按文档行尾符切段；StringSplitOptions.None 保证空行留下成为一个空字符串段
        var lines = new List<string>(text.Split(new[] { eol }, StringSplitOptions.None));
        bool entireDocument = scope.ToLine == _scintilla.Lines.Count - 1;
        if (!entireDocument && lines.Count > 0 && lines[^1].Length == 0)
        {
            // 选区末尾的 EOL 被 split 成了空段，它其实属于上一行；补回去会多出一行，先摘掉
            lines.RemoveAt(lines.Count - 1);
        }

        var result = transform(lines);

        // 拼回原文：非整篇时补一个行尾符，让这一段和它后面那行之间仍然有分隔
        string joined = string.Join(eol, result);
        if (!entireDocument) joined += eol;
        // 内容没变就不写文档，避免产生一条空撤销、也不触发 Modified 脏标记
        if (text == joined) return;

        WithUndoGroup(() =>
        {
            _scintilla.TargetStart = startPos;
            _scintilla.TargetEnd = endPos;
            _scintilla.ReplaceTarget(joined);
        });

        if (scope.HasSelection)
        {
            // ND2 排完把选区恢复成处理过的那几行
            // 右端用 LineContentEndPos：停在末行内容末尾，不把行尾符选进选区
            _scintilla.SetSelection(LineStartPos(scope.FromLine), LineContentEndPos(scope.ToLine));
        }
    }

    /// <summary>
    /// ND2 ISorter::getSortKey：toColumn!=0 时只有这一段列宽参与比较。
    /// 列号是 UTF-16 码元列（矩形选区换算来的），不是字节偏移。
    /// </summary>
    /// <param name="line">整行文本（不含行尾符）</param>
    /// <param name="fromColumn">起始列（含）</param>
    /// <param name="toColumn">结束列（不含）；0 表示不切片、整行做 key</param>
    /// <returns>用于比较的 key 字符串</returns>
    private static string SortKey(string line, int fromColumn, int toColumn)
    {
        if (toColumn == 0) return line;
        // 行比选区列还短：这行在矩形选区里是空白，key 取空串（排在最前）
        if (line.Length < fromColumn) return string.Empty;
        // 起止列重合 = 零宽矩形（光标构成的列块），ND2 语义是从该列一直取到行尾
        if (fromColumn == toColumn) return line[fromColumn..];
        return line.Substring(fromColumn, toColumn - fromColumn);
    }

    /// <summary>
    /// 走 ND2 作用域管线的排序入口。
    /// </summary>
    /// <param name="compare">行比较委托；传 null 表示「反转行序」（ReverseOrder），此时列切片不参与</param>
    private void SortLinesNd(Comparison<string>? compare)
    {
        var scope = CurrentLineScope();
        RewriteLineScope(scope, lines =>
        {
            if (compare == null) lines.Reverse();
            // 列切片只在比较时生效：排序键是切片，最终移动的是整行
            else lines.Sort((a, b) => compare(SortKey(a, scope.FromColumn, scope.ToColumn),
                                               SortKey(b, scope.FromColumn, scope.ToColumn)));
            return lines;
        });
    }

    // ==================== 排序 ====================

    /// <summary>「格式 → 排序行」支持的排序方式；前 5 项对应 ND2 真实接线，其余为本地增强</summary>
    public enum SortMode
    {
        /// <summary>按码元序号升序（区分大小写，大写字母排在小写之前）</summary>
        LexAsc, LexDesc,
        /// <summary>忽略大小写的序号升/降序</summary>
        LexAscIgnoreCase, LexDescIgnoreCase,
        /// <summary>不比较，直接把行序整体反转（ND2 ReverseSorter）</summary>
        ReverseOrder,
        /// <summary>按行内首个整数升/降序</summary>
        IntegerAsc, IntegerDesc,
        /// <summary>以小数点为分隔按数值升/降序</summary>
        DecimalDotAsc, DecimalDotDesc,
        /// <summary>以逗号为小数分隔符按数值升/降序（欧洲写法）</summary>
        DecimalCommaAsc, DecimalCommaDesc,
        /// <summary>随机打乱行序（Fisher-Yates）</summary>
        Randomize,
    }

    /// <summary>
    /// 数值排序用的取数正则：可选符号 + 至少一位数字 + 后续的 . , 与数字。
    /// 只匹配「第一个」数，因此 "a1b2" 取到 1；Compiled 是因为排序可能被频繁调用。
    /// </summary>
    private static readonly Regex NumberPattern = new(@"[-+]?\d[\d.,]*", RegexOptions.Compiled);

    /// <summary>
    /// 按指定方式排序工作行。
    /// 两条管线互斥：ND2 已接线的前 5 种走 <see cref="SortLinesNd"/>（尊重矩形列切片、单行选区不动作），
    /// 其余本地增强走 <see cref="TransformLines"/>（有跨行选区只排选区行，否则全文）。
    /// </summary>
    /// <param name="mode">排序方式</param>
    public void SortLines(SortMode mode)
    {
        // 前五个是 ND2 行编辑里真实接线的排序项，走它自己的作用域管线
        switch (mode)
        {
            case SortMode.LexAsc:
                SortLinesNd(string.CompareOrdinal);
                return;
            case SortMode.LexDesc:
                // 取负号实现降序：CompareOrdinal 返回的是差值，取负后严格反序
                SortLinesNd((a, b) => -string.CompareOrdinal(a, b));
                return;
            case SortMode.LexAscIgnoreCase:
                SortLinesNd((a, b) => string.Compare(a, b, StringComparison.OrdinalIgnoreCase));
                return;
            case SortMode.LexDescIgnoreCase:
                SortLinesNd((a, b) => -string.Compare(a, b, StringComparison.OrdinalIgnoreCase));
                return;
            case SortMode.ReverseOrder:
                // null 比较器 = 行序反转，ND2 的 std::reverse
                SortLinesNd(null);
                return;
        }

        // 以下都是 ND2 没接线的本地增强
        switch (mode)
        {
            case SortMode.IntegerAsc:
                TransformLines(l => l.OrderBy(ExtractInteger).ToList());
                break;
            case SortMode.IntegerDesc:
                TransformLines(l => l.OrderByDescending(ExtractInteger).ToList());
                break;
            case SortMode.DecimalDotAsc:
                TransformLines(l => l.OrderBy(x => ExtractDecimal(x, '.')).ToList());
                break;
            case SortMode.DecimalDotDesc:
                TransformLines(l => l.OrderByDescending(x => ExtractDecimal(x, '.')).ToList());
                break;
            case SortMode.DecimalCommaAsc:
                TransformLines(l => l.OrderBy(x => ExtractDecimal(x, ',')).ToList());
                break;
            case SortMode.DecimalCommaDesc:
                TransformLines(l => l.OrderByDescending(x => ExtractDecimal(x, ',')).ToList());
                break;
            case SortMode.Randomize:
                TransformLines(Shuffle);
                break;
        }
    }

    /// <summary>
    /// 从一行里抽出用于「整数排序」的值。
    /// 取匹配到的第一段数字串，只留数字字符再解析，符号从原始匹配串单独判。
    /// </summary>
    /// <param name="line">整行文本（不含行尾符）</param>
    /// <returns>解析出的整数；没有数字或解析失败返回 long.MaxValue，让这类行统一沉到降序末尾/升序末尾</returns>
    private static long ExtractInteger(string line)
    {
        var m = NumberPattern.Match(line);
        if (!m.Success) return long.MaxValue;

        // 丢掉 . , 等分隔符，"1,234" 当 1234 处理；只认数字
        string digits = new(m.Value.Where(char.IsDigit).ToArray());
        if (digits.Length == 0) return long.MaxValue;
        // 超长数字串 TryParse 会失败，同样归到 MaxValue（不抛异常）
        if (!long.TryParse(digits, out long v)) return long.MaxValue;
        return m.Value.StartsWith('-') ? -v : v;
    }

    /// <summary>
    /// 从一行里抽出用于「小数排序」的值。
    /// </summary>
    /// <param name="line">整行文本（不含行尾符）</param>
    /// <param name="decimalSeparator">本次调用认定的小数分隔符（'.' 或 ','），会先归一成 '.'</param>
    /// <returns>解析出的浮点数；无数字或解析失败返回 double.MaxValue</returns>
    private static double ExtractDecimal(string line, char decimalSeparator)
    {
        var m = NumberPattern.Match(line);
        if (!m.Success) return double.MaxValue;

        string raw = m.Value.Replace(decimalSeparator, '.');
        // 只保留第一个小数点，其余按千位分隔符丢弃
        int firstDot = raw.IndexOf('.');
        if (firstDot >= 0)
            raw = raw[..(firstDot + 1)] + raw[(firstDot + 1)..].Replace(".", "");

        // 符号要在清理之前判断，因为后面会把 '-' 之外的非数字字符一起过滤
        bool negative = raw.StartsWith('-');
        string cleaned = new(raw.Where(c => char.IsDigit(c) || c == '.').ToArray());
        // InvariantCulture：固定用 '.' 做小数点，不受系统区域设置影响
        return double.TryParse(cleaned, NumberStyles.Float, CultureInfo.InvariantCulture, out double v)
            ? (negative ? -v : v)
            : double.MaxValue;
    }

    /// <summary>
    /// Fisher-Yates 原地洗牌（从后往前，每次与前面随机位置交换），保证每种排列等概率。
    /// </summary>
    /// <param name="lines">待打乱的行列表，原地修改</param>
    /// <returns>同一个列表实例（便于作为 transform 委托传入）</returns>
    private static List<string> Shuffle(List<string> lines)
    {
        var rnd = new Random();
        // i 从末行递减到 1；Next(i+1) 取值 0..i，i==0 时只剩一个位置无需交换
        for (int i = lines.Count - 1; i > 0; i--)
        {
            int j = rnd.Next(i + 1);
            (lines[i], lines[j]) = (lines[j], lines[i]);
        }
        return lines;
    }

    // ==================== 去重 / 空行 / 空白 ====================

    /// <summary>
    /// ND2「删除重复行」= removeAnyDuplicateLines（scintillaeditview.cpp:2932）：
    /// 整行原样比较（区分大小写、不 trim），保留首次出现；作用域和排序一样，单行选区直接不动作。
    /// 也就是说 "abc" 与 "abc " 算两行，"Abc" 与 "abc" 也算两行。
    /// </summary>
    public void RemoveDuplicateLines()
    {
        var scope = CurrentLineScope();
        RewriteLineScope(scope, lines =>
        {
            // StringComparer.Ordinal：按码元逐位比，排除当前区域性的忽略大小写等价规则
            var seen = new HashSet<string>(StringComparer.Ordinal);
            // HashSet.Add 只在第一次成功时返回 true，天然实现「保留首次出现」
            return lines.Where(seen.Add).ToList();
        });
    }

    /// <summary>
    /// ND2「移除空行」/「移除空行(包括空白字符)」（ccnotepad.cpp:9137/9143 → findwin.cpp:916）：
    /// 静默调用替换所有的正则引擎，无视选区处理整篇，返回替换次数好让宿主照 ND2 那样报状态栏。
    /// 模式吃掉「整行 + 行尾符」，所以最后一行若没有行尾符（^$ 后面不跟换行）不会被删。
    /// </summary>
    /// <param name="containingBlankChars">true 时空格与 TAB 组成的「空白行」也算空行一并删除</param>
    /// <returns>被删除的行数（正则替换命中次数）</returns>
    public int RemoveEmptyLines(bool containingBlankChars)
        => SilentRegexReplaceAll(containingBlankChars ? @"^[\t ]*$(\r\n|\r|\n)" : @"^$(\r\n|\r|\n)");

    /// <summary>
    /// 连续空行压成一行（ND2 没有这一项，本地增强）。
    /// 「空」的判定是 Trim 后长度为 0，即纯空白行同样算空行；非空行原样保留，不改内容。
    /// </summary>
    public void RemoveConsecutiveEmptyLines()
        => TransformLines(l =>
        {
            var result = new List<string>();
            bool prevBlank = false;
            foreach (var line in l)
            {
                bool blank = line.Trim().Length == 0;
                // 与上一行都是空行 → 丢弃这一行；prevBlank 保持 true，于是连续 N 个空行只留第一个
                if (blank && prevBlank) continue;
                result.Add(line);
                prevBlank = blank;
            }
            return result;
        });

    /// <summary>
    /// 删除文档首尾的空行（ND2 没有这一项，本地增强）。
    /// 作用域仍是 <see cref="WorkLineRange"/>：有跨行选区时是「选区首尾」，无选区时才是整篇首尾。
    /// </summary>
    public void RemoveHeadEndEmptyLines()
        => TransformLines(l =>
        {
            // 双指针向内收缩，跳过两端所有 Trim 后为空的行
            int first = 0, last = l.Count - 1;
            while (first <= last && l[first].Trim().Length == 0) first++;
            while (last >= first && l[last].Trim().Length == 0) last--;
            // 全空时 first 会越过 last，用 Max(0,…) 兜住长度，返回空列表 = 整个范围被清空
            return l.GetRange(first, Math.Max(0, last - first + 1));
        });

    /// <summary>
    /// 以下三条是 ND2 的死控件（连续去重 / 去重后排序），保留为本地增强。
    /// 只去掉「紧邻」的重复行，A/B/A 这种不连续的重复不受影响。
    /// </summary>
    public void RemoveConsecutiveDuplicateLines()
        => TransformLines(l => l
            // 带上原始下标，好跟「上一行」比较；idx==0 的首行永远保留
            .Select((line, idx) => new { line, idx })
            .Where(x => x.idx == 0 || x.line != l[x.idx - 1])
            .Select(x => x.line)
            .ToList());

    /// <summary>
    /// 去重并排序（「删除重复行」的另一变体）：先按 Ordinal 排序，再保留首次出现，
    /// 因此输出是「已排序且无重复」的行集合。走 <see cref="WorkLineRange"/> 作用域，不是 ND2 管线。
    /// </summary>
    public void RemoveDuplicateLinesSorted()
        => TransformLines(l =>
        {
            var sorted = l.OrderBy(x => x, StringComparer.Ordinal).ToList();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            return sorted.Where(seen.Add).ToList();
        });

    /// <summary>
    /// ND2「去除行首空白」（ccnotepad.cpp:9033 → findwin.cpp:308 removeLineHeadEndBlank(1)）：
    /// 静默正则全替 <c>^[ \t]+</c>，只吃空格和 TAB，无视选区处理整篇。
    /// </summary>
    /// <returns>被替换的次数（命中行数）</returns>
    public int RemoveLineHeadBlank() => SilentRegexReplaceAll("^[ \t]+");

    /// <summary>
    /// ND2「去除行尾空白」(:9042 → removeLineHeadEndBlank(2))：<c>[ \t]+$</c>。
    /// $ 匹配行尾（不换行模式下也含 \r\n 之前），所以 CRLF 文档不会被误吃掉换行。
    /// </summary>
    /// <returns>被替换的次数</returns>
    public int RemoveLineEndBlank() => SilentRegexReplaceAll("[ \t]+$");

    /// <summary>
    /// ND2「去除行首尾空白」(:9051)：连着调上面两条，各报各的次数（状态栏后一条盖掉前一条）。
    /// 顺序是先清行首再清行尾，两次独立全替。
    /// </summary>
    /// <returns>(行首命中次数, 行尾命中次数)</returns>
    public (int Head, int End) RemoveLineHeadEndBlank() => (RemoveLineHeadBlank(), RemoveLineEndBlank());

    /// <summary>
    /// ND2 的静默通道：m_isStatic=true 的 slot_replaceAll，正则、整篇、无视选区、不弹确认。
    /// MatchCase 固定 true（模式里没有大小写含义，关掉反而会让 ^ 与 $ 的行为受区域设置干扰），
    /// 替换文本恒为空串——这类「行整备」本质就是删。
    /// </summary>
    /// <param name="pattern">正则模式（不解释 $1 之类的替换变量）</param>
    /// <returns>替换命中次数</returns>
    private int SilentRegexReplaceAll(string pattern)
        => ReplaceAll(new SearchCondition(pattern, SearchMode.Regex, MatchCase: true, WholeWord: false), string.Empty);

    // ==================== 单行操作 ====================

    /// <summary>
    /// ND2 insertNewLineAboveCurrentLine（scintillaeditview.cpp:3014）：在第 N 行起点前插一个行尾符。
    /// 插的是「行尾符」而不是「行首符」：在第 N 行起点前放 \r\n，等于把第 N 行的内容挤到下一行，
    /// 上面凭空多出一条空行；首行没有上一行可借用，只能在文档 0 位置垫。
    /// </summary>
    public void InsertBlankLineAbove()
    {
        string newline = EolString;
        int currentLine = CurrentLine;

        if (currentLine == 0)
            _scintilla.InsertText(0, newline);                       // 光标在第一行：直接在文档最前面垫一个行尾符
        else
            _scintilla.InsertText(LineStartPos(currentLine) - newline.Length, newline);

        // 插完当前行号还没变，它指向的就是新出来的那条空行
        // 于是把光标定在「原 currentLine 的新起点」，也就是新空行的行首
        _scintilla.SetEmptySelection(LineStartPos(currentLine));
    }

    /// <summary>
    /// ND2 insertNewLineBelowCurrentLine（:3032）：末行只能 append，其余在下一行起点前插。
    /// 在下一行起点前放一个行尾符，相当于当前行末尾多出一个换行、下面多出一条空行。
    /// </summary>
    public void InsertBlankLineBelow()
    {
        string newline = EolString;
        int currentLine = CurrentLine;

        if (currentLine == _scintilla.Lines.Count - 1)
            _scintilla.AppendText(newline);
        else
            _scintilla.InsertText(LineStartPos(currentLine + 1), newline);

        // 光标落在新增空行的行首（插入后 currentLine+1 就是那条空行）
        _scintilla.SetEmptySelection(LineStartPos(currentLine + 1));
    }

    /// <summary>
    /// ND2「复制当前行」Ctrl+D = SCI_LINEDUPLICATE（ccnotepad.cpp:9480）。
    /// 有选区时复制选区覆盖的整组行，无选区时复制光标所在整行；直接交给引擎，自己不算行列。
    /// </summary>
    public void DuplicateCurrentLine() => _scintilla.DirectMessage(2404, IntPtr.Zero, IntPtr.Zero);

    /// <summary>
    /// ND2 上移/下移都是 SCI_MOVESELECTEDLINESUP/DOWN：有选区时整组选区行一起走。
    /// 到顶（第一行）时引擎自己吞掉不动作，无需前置判断。
    /// </summary>
    public void MoveSelectedLinesUp() => _scintilla.DirectMessage(2620, IntPtr.Zero, IntPtr.Zero);

    /// <summary>
    /// 下移选区行；SCI_MOVESELECTEDLINESDOWN 之后额外做一次 SCI_SCROLLRANGE，
    /// 让视口跟到新的选区位置（ND2 也是这么补的，否则一直按到底部时光标会跑出屏幕）。
    /// </summary>
    public void MoveSelectedLinesDown()
    {
        _scintilla.DirectMessage(2621, IntPtr.Zero, IntPtr.Zero);
        // ND2 下移后还要 SCI_SCROLLRANGE 把选区拉回可视区
        _scintilla.ScrollRange(_scintilla.SelectionEnd, _scintilla.SelectionStart);
    }

    /// <summary>
    /// 删除光标所在整行（连行尾符一起），即 [行首, 下一行行首) 区间置空。
    /// 末行时右端是 TextLength，删完可能只剩一个空行；包在一个撤销组里，一次 Ctrl+Z 可恢复。
    /// </summary>
    public void DeleteCurrentLine()
    {
        int line = CurrentLine;
        WithUndoGroup(() =>
        {
            _scintilla.TargetStart = LineStartPos(line);
            _scintilla.TargetEnd = LineEndPos(line);
            _scintilla.ReplaceTarget(string.Empty);
        });
        // 删掉第 line 行后，原来的第 line+1 行顶替上来，光标就停在这个行号（GoToLine 会自己夹到合法范围）
        GoToLine(line);
    }

    /// <summary>
    /// 删除光标前的内容（行首到光标）。
    /// 光标在行首时选区为空，等价于不动作；不会跨越换行符，因此不影响上一行。
    /// </summary>
    public void DeleteToLineStart()
    {
        int line = CurrentLine;
        _scintilla.SetSelection(LineStartPos(line), _scintilla.CurrentPosition);
        _scintilla.ReplaceSelection(string.Empty);
    }

    /// <summary>
    /// 删除光标后的内容（光标到行尾）。
    /// 右端用 <see cref="LineEndPos"/>（含行尾符），所以本行连同它的换行一起被删、下一行会提上来；
    /// 若只想删到内容末尾（保留换行），右端应换成 <see cref="LineContentEndPos"/>。
    /// </summary>
    public void DeleteToLineEnd()
    {
        int line = CurrentLine;
        _scintilla.SetSelection(_scintilla.CurrentPosition, LineEndPos(line));
        _scintilla.ReplaceSelection(string.Empty);
    }

    /// <summary>
    /// 剪切整行到剪贴板：先把选区铺成 [行首, 下一行行首)，再走 SafeCut（复制 + 删除）。
    /// 因为删掉了整行含换行，后续行号前移，删完用 GoToLine 把光标拉回同一行号（即原来的下一行）。
    /// </summary>
    public void CutCurrentLine()
    {
        int line = CurrentLine;
        _scintilla.SetSelection(LineStartPos(line), LineEndPos(line));
        SafeCut();
        GoToLine(line);
    }

    /// <summary>
    /// 复制整行到剪贴板（含行尾符）。文档内容不变，但选区会被铺成整行且不主动还原，
    /// 所以调用后视觉上当前行是「被选中」状态，插入点在行尾。
    /// </summary>
    public void CopyCurrentLine()
    {
        int line = CurrentLine;
        _scintilla.SetSelection(LineStartPos(line), LineEndPos(line));
        SafeCopy();
    }

    /// <summary>
    /// 复制光标到行尾（含行尾符）到剪贴板，常用于把本行剩余内容接到下一行去。
    /// 右端用 <see cref="LineEndPos"/>，因此复制出来的串以换行结尾，粘过去会自动换行。
    /// </summary>
    public void CopyToLineEnd()
    {
        int line = CurrentLine;
        _scintilla.SetSelection(_scintilla.CurrentPosition, LineEndPos(line));
        SafeCopy();
    }

    /// <summary>
    /// ND2 slot_splitLines（ccnotepad.cpp:9502）：先要求**恰好一个选区**（多光标静默什么都不做），
    /// 把选区扩到整行后 SCI_TARGETFROMSELECTION，再 SCI_LINESSPLIT。
    /// ND2 全仓从不 SETEDGEMODE，edgeMode 恒为 EDGE_NONE（:9514 的分支只会走 0），
    /// 传 0 = 让 Scintilla 自己取可视区像素宽（Editor.cpp:1625 GetTextRectangle().Width()）。
    /// 拆行是按「显示宽度」而非码元数，中英文混排时结果随字体度量变化。
    /// </summary>
    public void SplitLines()
    {
        if (_scintilla.Selections.Count != 1) return;

        // 选区先规整成「第一行行首 → 最后一行内容末尾」，不含行尾符，避免把空行也拆出换行
        var (first, last) = SelectionLinesRange();
        _scintilla.SetSelection(LineStartPos(first), LineContentEndPos(last));
        // 把当前选区灌进 target 区间，SCI_LINESSPLIT 只作用于 target 之内
        _scintilla.TargetFromSelection();
        _scintilla.DirectMessage(2289, IntPtr.Zero, IntPtr.Zero);
    }

    /// <summary>
    /// ND2 slot_joinLines（ccnotepad.cpp:9533）：首末行相同直接不动作，否则扩到整行后 SCI_LINESJOIN。
    /// 引擎行为（Editor.cpp:1593）：删掉行尾符，**只有前一行末不是空格时补一个空格**分隔，
    /// 续行自己的行首空白**原样保留**——所以自己拿 TrimStart 拼是错的。
    /// </summary>
    public void JoinLines()
    {
        var (first, last) = SelectionLinesRange();
        if (first == last) return;

        // 右端同样是「内容末尾」：拼接时不把行尾符当成待合并的内容
        _scintilla.SetSelection(LineStartPos(first), LineContentEndPos(last));
        _scintilla.TargetFromSelection();
        _scintilla.DirectMessage(2288, IntPtr.Zero, IntPtr.Zero);
    }

    /// <summary>
    /// 把选区中的转义 \n 变成真正换行（ND2 没有这一项，本地增强，挂在「更多行操作」）。
    /// 替换顺序有意为之：先处理两字符的 "\r\n" 字面量，否则会被后面的 "\n" 分支拆成 "\r" + 换行。
    /// </summary>
    public void ExpandEscapedNewlines()
    {
        string sel = SelectedText;
        if (string.IsNullOrEmpty(sel)) return;

        string replaced = sel.Replace("\\r\\n", "\n").Replace("\\n", "\n").Replace("\\t", "\t");
        // 没有命中就别 ReplaceSelection，否则会白白产生一次撤销动作并把选区压扁
        if (replaced == sel) return;

        _scintilla.ReplaceSelection(replaced);
    }

    // ==================== 大小写 ====================

    /// <summary>
    /// 对应 ND2 的 TextCaseType（scintillaeditview.h:39）；值顺序照抄，菜单按序号取。
    /// 各成员含义：Upper 全大写、Lower 全小写、Proper 词首大写其余压小写、ProperBlend 只抬词首不动其余、
    /// Sentence 句首大写其余压小写、SentenceBlend 只抬句首、Invert 大小写互反、Random 每字母随机。
    /// </summary>
    public enum CaseMode { Upper, Lower, Proper, ProperBlend, Sentence, SentenceBlend, Invert, Random }

    /// <summary>
    /// ND2 convertSelectedTextTo（scintillaeditview.cpp:2813）：只吃选区，
    /// 没有选区就什么都不做（不动当前行、也不提示），换完把选区原样还回去。
    /// 整段一次 ReplaceTarget，包在撤销组里，所以 Ctrl+Z 一次还原全部文字。
    /// </summary>
    /// <param name="mode">目标大小写形式</param>
    public void ChangeCase(CaseMode mode)
    {
        // SelectionStart/End 已归一化，反向选择也能正确处理
        int start = _scintilla.SelectionStart;
        int end = _scintilla.SelectionEnd;
        if (start == end) return;

        // 码元位置换算成长度：GetTextRange 的第二个参数是「取多少个 UTF-16 码元」
        string text = _scintilla.GetTextRange(start, end - start);
        string result = ConvertCase(text, mode);

        _scintilla.TargetStart = start;
        _scintilla.TargetEnd = end;
        WithUndoGroup(() => _scintilla.ReplaceTarget(result));
        // 替换后选区会被压扁，这里显式还原成原来的起止（长度没变，位置仍然有效）
        _scintilla.SetSelection(start, end);
    }

    /// <summary>
    /// 逐条照 ND2 changeCase（scintillaeditview.cpp:2695-2811）搬过来，字符判断按 UTF-16 码元，和 Qt 的 QChar 一致。
    /// 因为按码元处理，emoji 等代理对会被拆成两个「非字母」码元，行为与 ND2 完全相同（都算不上字母，原样输出）。
    /// </summary>
    /// <param name="text">要转换的文本，可含换行</param>
    /// <param name="mode">目标大小写形式</param>
    /// <returns>转换后的新文本；长度与 <paramref name="text"/> 相同，入参为空时原样返回</returns>
    public static string ConvertCase(string text, CaseMode mode)
    {
        if (text.Length == 0) return text;

        switch (mode)
        {
            case CaseMode.Upper:
                return text.ToUpperInvariant();
            case CaseMode.Lower:
                return text.ToLowerInvariant();

            case CaseMode.Proper:
            case CaseMode.ProperBlend:
            {
                // force=true（Proper）时非词首字母一律压回小写；ProperBlend 只抬词首、不动其余
                bool force = mode == CaseMode.Proper;
                char[] a = text.ToCharArray();
                for (int i = 0; i < a.Length; i++)
                {
                    if (!char.IsLetter(a[i])) continue;

                    // 前一个字符不是字母也不是数字才算词首
                    if (i < 1 || !char.IsLetterOrDigit(a[i - 1])) a[i] = char.ToUpperInvariant(a[i]);
                    else if (force) a[i] = char.ToLowerInvariant(a[i]);

                    // 撇号例外：it's / rock'n'roll 里的词首字母要压回小写（BLEND 也生效）
                    // 判定条件是「前一个字符是撇号，且再前一个字符是字母」，所以开头的 'x 不会被压小写
                    if (i >= 2 && a[i - 1] == '\'' && char.IsLetter(a[i - 2])) a[i] = char.ToLowerInvariant(a[i]);
                }
                return new string(a);
            }

            case CaseMode.Sentence:
            case CaseMode.SentenceBlend:
            {
                // force=true（Sentence）时句内其余字母压小写；SentenceBlend 只抬句首
                bool force = mode == CaseMode.Sentence;
                char[] a = text.ToCharArray();
                bool isNewSentence = true;   // 段首视作新句开头，第一个字母要大写
                // 这两个「上一次见过行尾符」的标志用来实现「连续两个换行 = 新句子」，单个换行不算
                bool wasEolR = false;
                bool wasEolN = false;
                for (int i = 0; i < a.Length; i++)
                {
                    if (char.IsLetter(a[i]))
                    {
                        if (isNewSentence)
                        {
                            a[i] = char.ToUpperInvariant(a[i]);
                            isNewSentence = false;
                        }
                        else if (force) a[i] = char.ToLowerInvariant(a[i]);

                        // 遇到字母就把两个行尾标志清掉，跨字母的空格/标点不影响「双换行」判定
                        wasEolR = false;
                        wasEolN = false;

                        // 孤立的 i → I：前后是空格/括号/引号/撇号才算孤立
                        if (a[i] == 'i'
                            && i >= 1 && (char.IsWhiteSpace(a[i - 1]) || a[i - 1] == '(' || a[i - 1] == '"')
                            && i + 1 < a.Length && (char.IsWhiteSpace(a[i + 1]) || a[i + 1] == '\''))
                        {
                            a[i] = 'I';
                        }
                    }
                    else if (a[i] is '.' or '!' or '?')
                    {
                        // 后面紧跟字母数字不算新句，防 3.14；ND2 只认半角标点
                        isNewSentence = i + 1 < a.Length && !char.IsLetterOrDigit(a[i + 1]);
                    }
                    else if (a[i] == '\r')
                    {
                        // 第一次见 \r 只置标志；紧接着又见一个 \r（老式 Mac 空行）才认定新句子
                        if (wasEolR) isNewSentence = true; else wasEolR = true;
                    }
                    else if (a[i] == '\n')
                    {
                        // 同理：\n\n（LF 文档里的空行）算新句子分隔
                        if (wasEolN) isNewSentence = true; else wasEolN = true;
                    }
                }
                return new string(a);
            }

            case CaseMode.Invert:
            {
                char[] a = text.ToCharArray();
                // 小写→大写；其余（含大写和非字母）走 ToLowerInvariant，非字母原样返回
                for (int i = 0; i < a.Length; i++)
                    a[i] = char.IsLower(a[i]) ? char.ToUpperInvariant(a[i]) : char.ToLowerInvariant(a[i]);
                return new string(a);
            }

            default:
            {
                // Random：逐个字母掷硬币，非字母跳过以保证输出长度与内容不变
                char[] a = text.ToCharArray();
                for (int i = 0; i < a.Length; i++)
                {
                    if (!char.IsLetter(a[i])) continue;
                    a[i] = RandomBit() ? char.ToUpperInvariant(a[i]) : char.ToLowerInvariant(a[i]);
                }
                return new string(a);
            }
        }
    }

    // ND2 用没播种的 std::rand()，等价于每次启动都是同一串；这里只要"看起来随机"，不做那层复刻
    /// <summary>随机大小写共用的随机源；进程级单例，避免频繁 new 导致同一时间种子相同</summary>
    private static readonly Random CaseRandom = new();

    /// <summary>取一个随机布尔（抛硬币），供 <see cref="CaseMode.Random"/> 分支使用</summary>
    private static bool RandomBit() => (CaseRandom.Next(2) & 1) == 1;

    // ==================== Tab / 空格 / 行尾符 ====================

    /// <summary>
    /// ND2 <c>SpaceTab</c> 三态（ccnotepad.cpp:9161/9166/9171 → spaceTabConvert）。
    /// TabToSpace 把每个 TAB 展成若干空格；SpaceToTabAll 全文空格并 TAB；
    /// SpaceToTabLeading 只并每行「行首」那一段空格。
    /// </summary>
    public enum SpaceTabConversion { TabToSpace, SpaceToTabAll, SpaceToTabLeading }

    /// <summary>TAB 转空格（列感知，整篇处理）</summary>
    public void TabsToSpaces() => ConvertTabsSpaces(SpaceTabConversion.TabToSpace);

    /// <summary>
    /// 空格转 TAB（列感知，整篇处理）。
    /// </summary>
    /// <param name="leadingOnly">true = 只转换每行行首的空白；false = 全文空格都参与（两者都走列感知的空格→TAB 状态机）</param>
    public void SpacesToTabs(bool leadingOnly)
        => ConvertTabsSpaces(leadingOnly ? SpaceTabConversion.SpaceToTabLeading : SpaceTabConversion.SpaceToTabAll);

    /// <summary>
    /// 照 ND2 <c>spaceTabConvert</c>（ccnotepad.cpp:9270-9477）搬的列感知互转：
    /// 读全文 → 按制表位重排 → SCI_SETTEXT 写回 → GOTOPOS，**无视选区**，各包一个 undo group。
    /// TAB→空格是 <c>tabWidth - (column % tabWidth)</c> 个空格（不是一个 TAB 换 tabWidth 个）；
    /// 空格→TAB 是**制表位对齐**（<c>tabStop</c> 从 tabWidth-1 起、每跨一档加 tabWidth），不是每 tabWidth 个空格并一根 TAB。
    /// 列只让「一个字符」计一次：ND2 判 UTF-8 首字节 <c>(c &amp; 0xC0) != 0x80</c>，这里等价地跳过代理项低半区。
    /// </summary>
    /// <param name="type">三态之一，见 <see cref="SpaceTabConversion"/></param>
    public void ConvertTabsSpaces(SpaceTabConversion type)
    {
        // 全文一次性读进来算；currentPos 是新光标，也要跟着文本增删同步平移
        var (text, newCurrentPos) = SpaceTabConvert(_scintilla.Text, _scintilla.TabWidth, _scintilla.CurrentPosition, type);
        if (text == null) return;

        WithUndoGroup(() =>
        {
            // 整体替换比逐行 ReplaceTarget 简单可靠，代价是样式/折叠会重算一次
            _scintilla.Text = text;
            // 位置口径与读入时一致，都是托管 UTF-16 码元偏移
            _scintilla.SetEmptySelection(newCurrentPos);
        });
    }

    /// <summary>
    /// 上面那条的纯函数部分（拆出来只为能把状态机单独跑一遍）。返回 null = ND2 的那两个提前 return。
    /// 输入输出都是整篇文档文本，位置（<paramref name="currentPos"/> 与返回的光标）均为 UTF-16 码元下标；
    /// 「列」则是显示列：TAB 跳到下一个制表位，代理项低半区不占列。
    /// </summary>
    /// <param name="source">整篇文档文本</param>
    /// <param name="tabWidth">制表位宽度（Scintilla 的 TabWidth，通常 4）</param>
    /// <param name="currentPos">当前光标的码元下标，用于换算新光标</param>
    /// <param name="type">转换方向</param>
    /// <returns>(新文本, 新光标位置)；新文本为 null 表示无需改动或输入为空</returns>
    public static (string? Text, int Caret) SpaceTabConvert(string source, int tabWidth, int currentPos, SpaceTabConversion type)
    {
        if (source.Length == 0) return (null, 0);     // ND2: docLength < 2 直接 return

        // ND2 对 Tab2Space 先数 TAB，一个都没有就连 undo 都不开、直接返回
        if (type == SpaceTabConversion.TabToSpace && source.IndexOf('\t') < 0) return (null, 0);

        // ND2 的 source 是 NUL 结尾的缓冲，越界读拿到的是 '\0'；这里用同样的读法兜住边界
        // 局部函数 Ch：按 ND2 的「NUL 结尾缓冲」语义安全读字符，越界返回 '\0'，因此永远不等于空格或 TAB。
        // （C# 的 /// 文档注释不适用于局部函数，这里按标准用行注释）
        char Ch(int index) => index >= 0 && index < source.Length ? source[index] : '\0';

        var dest = new StringBuilder(source.Length + 16);
        int column = 0;         // 当前显示列（0 起），行尾符会把它清零
        int newCurrentPos = 0;  // 新文本里对应旧 currentPos 的码元下标

        if (type == SpaceTabConversion.TabToSpace)
        {
            // TAB → 空格：逐个字符扫描，遇到 TAB 就补到下一个制表位
            for (int i = 0; i < source.Length; i++)
            {
                char c = Ch(i);
                if (c == '\t')
                {
                    // 一个 TAB 补几个空格取决于当前列，不是固定 tabWidth 个
                    int insertTabs = tabWidth - (column % tabWidth);
                    for (int j = 0; j < insertTabs; j++)
                    {
                        dest.Append(' ');
                        // 原位置还在光标之前，展开出的每个空格都要让光标右移一格
                        if (i <= currentPos) newCurrentPos++;
                    }
                    column += insertTabs;
                }
                else
                {
                    dest.Append(c);
                    if (i <= currentPos) newCurrentPos++;
                    // 行尾符把列归零；普通字符占一列；代理项低半区不额外占列（一个字符只算一次）
                    if (c == '\n' || c == '\r') column = 0;
                    else if (!char.IsLowSurrogate(c)) column++;
                }
            }
        }
        else
        {
            bool onlyLeading = type == SpaceTabConversion.SpaceToTabLeading;
            // 三个变量都活在循环外，这是 ND2 的原样（counter 只在特定分支里归零）
            bool nextChar = false;        // 本轮已经在空格扫描里消费过字符，跳过下面的通用处理
            int counter = 0;              // 从 i 起连续向右探测空格时已跨过的个数（前瞻偏移）
            int tabStop = tabWidth - 1;   // remember, counting from zero!  下一个制表位所在列
            bool nonSpaceFound = false;   // 「仅行首」模式下本行是否已经碰到非空白

            for (int i = 0; i < source.Length; i++)
            {
                // 还没决定收手（或不是「仅行首」模式）时，才做空格→TAB 的合并扫描
                if (!nonSpaceFound)
                {
                    // 只要 i+counter 处还是空格就一直前瞻，凑到制表位才并
                    while (Ch(i + counter) == ' ')
                    {
                        // column 是进入本行段时的列，counter 是已经数了的空格数：+1 就是「这个空格之后」的列
                        if (column + counter == tabStop)
                        {
                            tabStop += tabWidth;
                            if (counter >= 1)
                            {
                                // 两个及以上空格正好凑满一个制表位：一根 TAB 顶过去
                                dest.Append('\t');
                                i += counter;                 // 空格段整体被消费，循环再 +1 落到下一个字符
                                column += counter + 1;
                                counter = 0;
                                nextChar = true;              // 本字符已处理，别再走下面的通用分支
                                if (i <= currentPos) newCurrentPos++;
                                break;
                            }
                            // 后面还跟着空格或 TAB，单个空格也并成 TAB（这一支不 break，接着扫）
                            else if (Ch(i + 1) == ' ' || Ch(i + 1) == '\t')
                            {
                                dest.Append('\t');
                                i++;
                                column += 1;
                                counter = 0;
                                if (i <= currentPos) newCurrentPos++;
                            }
                            else
                            {
                                // 孤零零一个空格，不并
                                dest.Append(Ch(i));
                                column += 1;
                                counter = 0;
                                nextChar = true;
                                if (i <= currentPos) newCurrentPos++;
                                break;
                            }
                        }
                        // 还没到制表位：继续往右数空格
                        else counter++;
                    }

                    if (nextChar)
                    {
                        nextChar = false;
                        continue;
                    }

                    // 空格被右边的 TAB「吸收」，一起并成一根 TAB
                    if (Ch(i) == ' ' && Ch(i + counter) == '\t')
                    {
                        dest.Append('\t');
                        i += counter;
                        // 这一支是「空格 + 已有 TAB」，显示列直接落到这一格制表位的末端，不用累加
                        column = tabStop + 1;
                        tabStop += tabWidth;
                        counter = 0;
                        if (i <= currentPos) newCurrentPos++;
                        continue;
                    }
                }

                // 「行首」版：本行第一个非空白字符之后就不再转换
                if (onlyLeading && !nonSpaceFound) nonSpaceFound = true;

                char c = Ch(i);
                if (c == '\n' || c == '\r')
                {
                    // 换行：列与制表位归零，「仅行首」标志复位，下一行重新开始合并
                    dest.Append(c);
                    column = 0;
                    tabStop = tabWidth - 1;
                    nonSpaceFound = false;
                }
                else if (c == '\t')
                {
                    // 已是 TAB：原样保留，并把列推到该制表位的下一格
                    dest.Append(c);
                    column = tabStop + 1;
                    tabStop += tabWidth;
                    counter = 0;
                }
                else
                {
                    // 普通字符：原样输出，占一列（低代理不占，与 ND2 跳续字节等价）
                    dest.Append(c);
                    counter = 0;
                    if (!char.IsLowSurrogate(c))
                    {
                        column++;
                        // 正好踩在制表位边界上，下一个 TAB/空格段的目标位置要前进一档
                        if (column > 0 && column % tabWidth == 0) tabStop += tabWidth;
                    }
                }

                if (i <= currentPos) newCurrentPos++;
            }
        }

        return (dest.ToString(), newCurrentPos);
    }

    /// <summary>
    /// TAB 宽度（列感知互转与 TAB 键都按它算）。
    /// setter 夹在 1-64，避免 0 导致 <c>column % tabWidth</c> 除零。
    /// </summary>
    public int TabWidth
    {
        get => _scintilla.TabWidth;
        set => _scintilla.TabWidth = Math.Clamp(value, 1, 64);
    }

    /// <summary>
    /// 缩进时是按 TAB 还是按空格填充（影响编辑器自动缩进，不影响上面那个显式互转）。
    /// </summary>
    public bool UseTabs
    {
        get => _scintilla.UseTabs;
        set => _scintilla.UseTabs = value;
    }

    /// <summary>
    /// 整篇转换行尾符风格（Windows \r\n / UNIX \n / Mac \r）。
    /// ConvertEols 改写既有行尾，EolMode 决定后续新行用什么，两者必须一起设；包一个撤销组。
    /// </summary>
    /// <param name="eol">目标行尾模式</param>
    public void ConvertEol(Eol eol)
    {
        WithUndoGroup(() =>
        {
            _scintilla.ConvertEols(eol);
            _scintilla.EolMode = eol;
        });
    }

    // ==================== 列编辑器 ====================

    /// <summary>列编辑器编号列的进制形式：Decimal 十进制、HexUpper/HexLower 十六进制大小写、Octal 八进制、Binary 二进制</summary>
    public enum ColumnNumberFormat { Decimal, HexUpper, HexLower, Octal, Binary }

    /// <summary>
    /// 列编辑器：在选区的同一列上批量插入文本或编号。
    /// mode: 0=插入文本 1=复制首行 2=粘贴剪贴板 4=递增编号
    /// repeat: 每个编号重复的行数（&lt;1 视作 1）
    /// </summary>
    /// <param name="mode">模式：<c>1</c> 把首行该列以右的内容复制到每一行同一列；<c>2</c> 粘贴剪贴板文本；
    /// <c>3</c> 插入递增编号；其余（含 <c>0</c>）插入 <paramref name="text"/> 原文。
    /// 提醒：方法上方摘要里写的「4=递增编号」与实现不符，代码里编号分支的 mode 值是 3，宿主传参要以 3 为准。</param>
    /// <param name="text">mode 为默认分支时要插入的字面文本</param>
    /// <param name="initial">编号起始值</param>
    /// <param name="increment">步长（可为负）</param>
    /// <param name="repeat">同一编号重复使用的行数，小于 1 按 1 处理</param>
    /// <param name="numberDigits">编号补齐位数（十进制下即最少几位数字）</param>
    /// <param name="format">编号进制</param>
    /// <param name="leadingZero">是否用 0 左补到 <paramref name="numberDigits"/> 位</param>
    /// <param name="prefix">编号前缀（如 "0x"），永远拼在最前面</param>
    public void ColumnEditor(int mode, string text, int initial, int increment, int repeat, int numberDigits,
                             ColumnNumberFormat format, bool leadingZero, string prefix)
    {
        // 作用域：有跨行选区就是选区行，否则整篇
        // 宿主对话框要求先有选区，但那选区若只在一行内，WorkLineRange 仍会退化成「整篇」，见下面 col 的注释
        var (first, last) = WorkLineRange();
        if (last < first) return;
        if (repeat < 1) repeat = 1;

        // 目标列 = 选区起点相对「首行行首」的偏移，口径是 UTF-16 码元列；
        // 无跨行选区时 first=0，这里等于拿到光标的绝对码元位置，随后被各处 Math.Min 夹到行尾
        int col = _scintilla.SelectionStart - LineStartPos(first);
        WithUndoGroup(() =>
        {
            // 从上往下逐行插入；插入只加长本行、不改变行数，所以行号 i 全程稳定
            for (int i = first; i <= last; i++)
            {
                string insert = mode switch
                {
                    // 复制首行同列以右的内容；Math.Min 防首行比目标列短而抛越界
                    1 => LineBody(first).Substring(Math.Min(col, LineBody(first).Length)),
                    2 => SafeClipboardText(),
                    // (i - first) / repeat 让同一编号连续用 repeat 行，再乘步长叠加到初值上
                    3 => FormatColumnNumber(initial + ((i - first) / repeat) * increment, numberDigits, format, leadingZero, prefix),
                    _ => text,
                };

                // 插入点夹在 [行首, 行尾位置] 之间：短行就退到该行内容末尾（行尾符之前）
                int pos = LineStartPos(i) + Math.Min(col, LineEndPos(i) - LineStartPos(i));
                _scintilla.InsertText(pos, insert);
            }
        });
    }

    /// <summary>
    /// 把整数按指定进制格式化成列编辑器要的编号串。
    /// </summary>
    /// <param name="value">要格式化的编号值</param>
    /// <param name="digits">补零后的最小位数（仅 <paramref name="leadingZero"/> 为 true 时生效）</param>
    /// <param name="format">进制</param>
    /// <param name="leadingZero">是否左补 0</param>
    /// <param name="prefix">前缀文本</param>
    /// <returns>前缀 + 编号字符串</returns>
    private static string FormatColumnNumber(int value, int digits, ColumnNumberFormat format,
                                             bool leadingZero, string prefix)
    {
        // InvariantCulture：十六进制字母和十进制分隔符都不受区域设置影响
        string body = format switch
        {
            ColumnNumberFormat.HexUpper => value.ToString("X", CultureInfo.InvariantCulture),
            ColumnNumberFormat.HexLower => value.ToString("x", CultureInfo.InvariantCulture),
            ColumnNumberFormat.Octal => Convert.ToString(value, 8),
            ColumnNumberFormat.Binary => Convert.ToString(value, 2),
            _ => value.ToString(CultureInfo.InvariantCulture),
        };

        // PadLeft 把整个字符串（含 '-' 与前缀之外的符号位）当内容补齐，
        // 所以负数补零会得到 "00-10" 这种形态；digits 非正数时不补
        if (leadingZero && digits > 0)
            body = body.PadLeft(digits, '0');

        return prefix + body;
    }

    /// <summary>
    /// 读剪贴板纯文本；剪贴板被别的进程占用时（CLIPBRD_E_CANT_OPEN）返回空串而不是崩掉编辑器。
    /// </summary>
    /// <returns>剪贴板文本，取不到时为空串</returns>
    private string SafeClipboardText()
    {
        try { return System.Windows.Clipboard.GetText(); }
        catch { return string.Empty; }
    }

    // ==================== 多列编辑 / 列模式 ====================

    /// <summary>
    /// 列编辑模式：Scintilla 本身没有「列模式」开关，鼠标拖选切矩形由
    /// MouseSelectionRectangularSwitch 承担（Ctrl 拖选 / Alt 拖选仍然有效）。
    /// 打开后不加 Ctrl 直接拖选就是矩形选区，再配合下面的多光标方法做列编辑。
    /// </summary>
    public bool ColumnMode
    {
        get => _scintilla.MouseSelectionRectangularSwitch;
        set => _scintilla.MouseSelectionRectangularSwitch = value;
    }

    /// <summary>
    /// 给选区每一行的行首加一个光标。
    /// 先 ClearSelections 把原有选区全清（AddSelection 是追加语义，不清就会留下一个旧选区凑数），
    /// 再逐行追加零长选区当光标；最后 SetSelection 把主选区的 anchor/caret 设成首行首与末行首。
    /// </summary>
    public void AddCaretsToLineStarts()
    {
        var (first, last) = WorkLineRange();
        _scintilla.ClearSelections();
        // 每个光标是零长选区 (pos, pos)，anchor 与 caret 都落在该行行首
        for (int i = first; i <= last; i++)
            _scintilla.AddSelection(LineStartPos(i), LineStartPos(i));
        // 注意：这一步把主选区改成「首行行首 → 末行行首」的跨行选区，
        // Scintilla 内部是否会顺带清掉上面追加的附加光标，需要实测确认
        if (first <= last) _scintilla.SetSelection(LineStartPos(first), LineStartPos(last));
    }

    /// <summary>
    /// 把当前选区复制到选区覆盖的每一行行首（原注释的说法）。
    /// 实现是直接转发 SCI_MULTIPLESELECTADDEACH，不传行号参数，由引擎决定补哪些行的光标，
    /// 具体语义取决于当前选区分布，调用前一般先用 <see cref="AddCaretsToLineStarts"/> 铺好每行光标。
    /// </summary>
    public void PasteToEachSelectedLine() => _scintilla.MultipleSelectAddEach();

    // ==================== 书签批量操作（照 ND2 scintillaeditview.cpp:1479-1640） ====================

    /// <summary>
    /// ND2 的 getMarkedLine 走 SCI_LINELENGTH，取到的是含行尾的整行，所以行间天然带换行。
    /// 这里在 <see cref="LineFull"/>（已带本行行尾）之后又补了一个 <see cref="EolString"/>，
    /// 于是每行末尾都有行尾，收集结果的最后还会多出一个空行。
    /// </summary>
    /// <param name="line">0 起的行号</param>
    /// <returns>整行内容 + 原行尾 + 一个额外行尾</returns>
    private string MarkedLine(int line) => LineFull(line) + EolString;

    /// <summary>
    /// ND2 deleteMarkedline：先摘掉书签标记，再连行尾一起把整行替换成空。
    /// 顺序很重要——先 RemoveBookmark 再删文本，否则 ReplaceTarget 之后行号已经前移，标记会摘错行。
    /// </summary>
    /// <param name="line">0 起的行号（调用方需保证从后往前遍历，行号才不会失效）</param>
    private void DeleteMarkedLine(int line)
    {
        // begin 必须在删之前取；右端用 LineEndPos（含行尾符），这样删除后不会残留空行
        int begin = LineStartPos(line);
        RemoveBookmark(line);
        _scintilla.TargetStart = begin;
        _scintilla.TargetEnd = LineEndPos(line);
        _scintilla.ReplaceTarget(string.Empty);
    }

    /// <summary>
    /// ND2 replaceMarkedline：SCI_POSITIONFROMLINE → SCI_GETLINEENDPOSITION，不含行尾，粘贴不会把下一行吸上来。
    /// 也就是说替换的是「行内容」，书签行自己的换行保持不变；若剪贴板文本自带换行，粘完会变成多行。
    /// </summary>
    /// <param name="line">0 起的行号</param>
    /// <param name="text">要写入的新行内容（不含本行行尾）</param>
    private void ReplaceMarkedLine(int line, string text)
    {
        _scintilla.TargetStart = LineStartPos(line);
        _scintilla.TargetEnd = LineContentEndPos(line);
        _scintilla.ReplaceTarget(text);
    }

    /// <summary>
    /// 收集所有带书签的行，拼成一段文本（每行都带行尾），供复制/剪切写进剪贴板。
    /// 正序遍历，输出顺序与文档顺序一致。
    /// </summary>
    /// <returns>书签行拼接结果；没有任何书签时为空串</returns>
    private string CollectMarkedLines()
    {
        var sb = new StringBuilder();
        for (int i = 0; i < _scintilla.Lines.Count; i++)
        {
            if (HasBookmark(i)) sb.Append(MarkedLine(i));
        }
        return sb.ToString();
    }

    /// <summary>
    /// 写剪贴板。Windows 剪贴板可能被其他进程锁住，这里静默忽略失败，不向上抛异常。
    /// </summary>
    /// <param name="text">要写入的纯文本，空串表示「清空剪贴板文本」</param>
    private static void SetClipboard(string text)
    {
        try { System.Windows.Clipboard.SetText(text); }
        catch { /* 剪贴板被占用时忽略 */ }
    }

    /// <summary>
    /// 复制书签行。没有书签时 ND2 也会把剪贴板写成空串，这里照做
    /// （所以这个操作会覆盖剪贴板内容，即使一个书签都没有）。
    /// </summary>
    public void CopyMarkedLines() => SetClipboard(CollectMarkedLines());

    /// <summary>
    /// 剪切书签行：先把文本收好，倒序删除（行号不失效），整体一个撤销组，最后写剪贴板。
    /// 倒序是必须的：删掉第 n 行后，n 之后的行号全部前移，正序会跳行、漏删。
    /// </summary>
    public void CutMarkedLines()
    {
        // 收集要在删除之前做，删完就读不到那些行了
        string text = CollectMarkedLines();
        WithUndoGroup(() =>
        {
            for (int i = _scintilla.Lines.Count - 1; i >= 0; i--)
            {
                if (HasBookmark(i)) DeleteMarkedLine(i);
            }
        });
        // 剪贴板写在撤销组外：撤销只回滚文档，不会把剪贴板还原
        SetClipboard(text);
    }

    /// <summary>
    /// 粘贴(替换)书签行：把剪贴板**整块**文本替换进每一个书签行，不是按行配对；
    /// 书签数为 0 什么都不做，粘完书签标记仍留在原行上。
    /// 倒序遍历同样是为了行号稳定——剪贴板文本含换行时会增加行数，倒着改不影响还没处理的上方行。
    /// </summary>
    public void PasteToMarkedLines()
    {
        string text;
        try
        {
            if (!System.Windows.Clipboard.ContainsText()) return;     // ND2 的 mimeData->hasText() 门槛
            text = System.Windows.Clipboard.GetText();
        }
        catch { return; }

        WithUndoGroup(() =>
        {
            for (int i = _scintilla.Lines.Count - 1; i >= 0; i--)
            {
                if (HasBookmark(i)) ReplaceMarkedLine(i, text);
            }
        });
    }

    /// <summary>
    /// 删除书签行（isMarked=true）/ 删除未标记行（isMarked=false），两者是同一个函数的两个方向。
    /// 都按行号从后往前删；「未标记」那一侧删除时同样会调 RemoveBookmark（摘一个不存在的标记是无害的 no-op）。
    /// </summary>
    /// <param name="isMarked">true = 只删有书签的行；false = 只删没有书签的行</param>
    public void DeleteMarkedLines(bool isMarked)
    {
        WithUndoGroup(() =>
        {
            for (int i = _scintilla.Lines.Count - 1; i >= 0; i--)
            {
                // HasBookmark 越界返回 false，所以 i 走到 0 也没问题
                if (HasBookmark(i) == isMarked) DeleteMarkedLine(i);
            }
        });
    }

    /// <summary>
    /// 反转所有书签（有书签的行取消，没书签的行加上）。ND2 这里不包撤销组，我们包了，算刻意偏离。
    /// 正序遍历：只改 marker 位图、不动文本，所以行号不会变，前后顺序无所谓。
    /// </summary>
    public void ToggleBookmarksOthers()
    {
        WithUndoGroup(() =>
        {
            for (int i = 0; i < _scintilla.Lines.Count; i++)
            {
                var line = _scintilla.Lines[i];
                // MarkerGet 返回该行所有 marker 的位掩码，用书签位单独判断
                if ((line.MarkerGet() & BookmarkMarkerMask) != 0)
                    line.MarkerDelete(BookmarkMarkerIndex);
                else
                    line.MarkerAdd(BookmarkMarkerIndex);
            }
        });
    }

    // ==================== 查找结果标记 ====================

    /// <summary>
    /// 按关键词给整篇文档的行打书签（「标记全部」），不区分大小写。
    /// 只要行内含有关键词就打一个书签，不会重复累加（MarkerAdd 是位或，同位重复无副作用）。
    /// </summary>
    /// <param name="keyword">要匹配的文本；空或 null 时直接返回 0 且不动文档</param>
    /// <returns>命中关键词的行数（这些行原本就带书签的也照样计数）</returns>
    public int MarkLinesContaining(string keyword)
    {
        if (string.IsNullOrEmpty(keyword)) return 0;

        int count = 0;
        WithUndoGroup(() =>
        {
            for (int i = 0; i < _scintilla.Lines.Count; i++)
            {
                // LineBody 不含行尾符，所以关键词里带换行永远匹配不上，这是「按行标记」的固有语义
                if (LineBody(i).Contains(keyword, StringComparison.OrdinalIgnoreCase))
                {
                    _scintilla.Lines[i].MarkerAdd(BookmarkMarkerIndex);
                    count++;
                }
            }
        });
        return count;
    }

    // ==================== 只读 ====================

    /// <summary>
    /// 文档只读开关。
    /// 开只读时挂上 ModifyAttempt 订阅、关只读时先摘掉，一挂一摘成对做（先 -= 再 += 防重复订阅导致事件双发）。
    /// 只读只拦编辑，不拦选区/滚动/标记，所以上面的排序、书签类操作仍可正常调用。
    /// </summary>
    public bool ReadOnly
    {
        get => _scintilla.ReadOnly;
        set
        {
            _scintilla.ReadOnly = value;
            _scintilla.ModifyAttempt -= OnModifyAttempted;
            if (value) _scintilla.ModifyAttempt += OnModifyAttempted;
        }
    }

    /// <summary>只读文档被尝试编辑时回调（主窗口用来弹提示）</summary>
    public event EventHandler? ReadOnlyEditAttempted;

    /// <summary>Scintilla 的 SCI_MODIFYATTEMPTRO 通知处理器：把 Win32 事件转成托管事件，无参数可传就只发空 EventArgs</summary>
    private void OnModifyAttempted(object? sender, EventArgs e) => ReadOnlyEditAttempted?.Invoke(this, EventArgs.Empty);

    // ==================== URL 检测 ====================

    /// <summary>
    /// URL/邮箱识别正则：前半段匹配 scheme 显式的链接（http/https/ftp/mailto），
    /// 后半段匹配没有 scheme 的邮箱。字符类里排掉了空白与 &lt; &gt; " ' ) ] } 这些「不该属于 URL」的边界字符，
    /// 所以 Markdown 链接、HTML 属性里的 URL 都能干净切出来。Compiled 是因为鼠标每次悬停/点击都可能跑一遍。
    /// </summary>
    private static readonly Regex UrlPattern =
        new(@"\b(?:https?|ftp|mailto)://[^\s<>""'\)\]\}]+|\b[\w.-]+@[\w.-]+\.\w{2,}\b", RegexOptions.Compiled);

    /// <summary>
    /// 返回光标 / 点击位置所在行的 URL，找不到返回 null。
    /// 匹配是在「行内容」上做的，所以 m.Index 是行内码元偏移，加回 <see cref="LineStartPos"/> 才是文档绝对位置；
    /// 判定用闭区间 [起, 止]，紧贴 URL 右边界的位置也算命中。
    /// </summary>
    /// <param name="position">文档内的 UTF-16 码元绝对位置（通常是光标或点击点）</param>
    /// <returns>命中的 URL 字符串；本行没有任何 URL 时返回 null</returns>
    public string? UrlAtPosition(int position)
    {
        int line = _scintilla.LineFromPosition(position);
        int lineStart = LineStartPos(line);
        string text = LineBody(line);

        foreach (Match m in UrlPattern.Matches(text))
        {
            // 行内偏移 → 文档绝对码元位置；m.Length 是码元个数，与绝对位置同口径，可直接相加
            int s = lineStart + m.Index;
            int e = s + m.Length;
            if (position >= s && position <= e) return m.Value;
        }

        // 不在任何 URL 内时，返回本行第一个 URL，方便 Ctrl+点空白处也能开
        var first = UrlPattern.Match(text);
        return first.Success ? first.Value : null;
    }

    /// <summary>
    /// 打开光标所在的 URL（交给系统默认浏览器/邮件客户端）。
    /// UseShellExecute=true 才能把任意 scheme 丢给 Shell 解析；失败（无关联程序、路径非法）一律返回 false，不弹异常。
    /// </summary>
    /// <returns>是否成功启动外部程序</returns>
    public bool OpenUrlAtCaret()
    {
        var url = UrlAtPosition(_scintilla.CurrentPosition);
        if (string.IsNullOrEmpty(url)) return false;

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true,
            });
            return true;
        }
        catch
        {
            return false;
        }
    }

    // ==================== 单词自动补全 ====================

    /// <summary>
    /// 用文档中已有的单词 + 语言关键字做补全候选。
    /// 实现上目前只扫整篇文档（下面的正则），并没有真的并入语言关键字，摘要里的「关键字」尚未落地。
    /// 词形为「字母或下划线开头、总长至少 3 个码元」，用 HashSet 去重、按 Ordinal 排序后以空格分隔喂给 Scintilla
    /// （SCI_AUTOCSHOW 的候选列表分隔符是空格，所以词本身不能含空格）。
    /// </summary>
    public void AutoCompleteFromDocument()
    {
        var words = new HashSet<string>(StringComparer.Ordinal);
        // 4000 词上限：大文档全量喂进去会让补全弹窗卡住，先到先收（因此取的是「首次出现」的一批，非最短前 N）
        foreach (Match m in Regex.Matches(_scintilla.Text, @"[\p{L}_][\p{L}\p{Nd}_]{2,}"))
        {
            words.Add(m.Value);
            if (words.Count >= 4000) break;
        }

        if (words.Count == 0) return;

        string list = string.Join(' ', words.OrderBy(w => w, StringComparer.Ordinal));
        // 弹窗高度限制（像素）与「输入前缀时忽略大小写」
        _scintilla.AutoCMaxHeight = 200;
        _scintilla.AutoCIgnoreCase = true;
        // SCI_AUTOCSHOW 的第一个参数照 ScintillaNET 透传（含义是起始/已输入长度），这里固定传 0，
        // 表示不预设已输入字符数，由引擎按当前插入点自己匹配前缀
        _scintilla.AutoCShow(0, list);
    }

    // ==================== 对齐 / 补位 ====================

    /// <summary>
    /// 把行尾补空格对齐到同一列。
    /// 宽度用的是 <see cref="LineBody"/> 的 UTF-16 码元长度，不是显示列宽：
    /// 含 TAB 或中文（全角）的行看起来仍会不齐，需要先用 TAB 互转/等宽字体处理。
    /// 单行或空范围（last &lt;= first）不动作。
    /// </summary>
    public void PadLinesToEqualWidth()
    {
        var (first, last) = WorkLineRange();
        if (last <= first) return;

        // 第一趟：把行内容缓存下来并求最大宽度，避免边写边读导致行号失效
        int max = 0;
        var lines = new List<string>();
        for (int i = first; i <= last; i++)
        {
            string body = LineBody(i);
            lines.Add(body);
            max = Math.Max(max, body.Length);
        }

        // 第二趟：一次性写回，PadRight 用空格补到 max 列
        WithUndoGroup(() => SetLineRange(first, last, lines.Select(x => x.PadRight(max))));
    }
}
