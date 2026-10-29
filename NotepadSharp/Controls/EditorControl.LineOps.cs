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

    public const int WriteMarkerIndex = 16;
    public const int ModifyMarkerIndex = 18;
    public const int DeleteMarkerIndex = 20;
    public const int FindMarkMarkerIndex = 24;
    public const int Bookmark1MarkerIndex = 1;

    // ==================== 差异标记（文件对比窗口用） ====================

    /// <summary>差异底色标记本身（SC_MARK_BACKGROUND 直接铺满整行，不占边距）</summary>
    public void ConfigureDiffMarkers()
    {
        ConfigureDiffMarker(WriteMarkerIndex, System.Drawing.Color.FromArgb(255, 200, 235, 205));
        ConfigureDiffMarker(ModifyMarkerIndex, System.Drawing.Color.FromArgb(255, 250, 230, 155));
        ConfigureDiffMarker(DeleteMarkerIndex, System.Drawing.Color.FromArgb(255, 250, 200, 195));
    }

    private void ConfigureDiffMarker(int index, System.Drawing.Color color)
    {
        var marker = _scintilla.Markers[index];
        marker.Symbol = MarkerSymbol.Background;
        marker.SetBackColor(color);
        marker.SetForeColor(color);
    }

    /// <summary>给整行打差异底色</summary>
    public void MarkDiffLine(int line, int markerIndex)
    {
        if (line < 0 || line >= _scintilla.Lines.Count) return;
        _scintilla.Lines[line].MarkerAdd(markerIndex);
    }

    public void ClearDiffMarkers()
    {
        for (int i = 0; i < _scintilla.Lines.Count; i++)
        {
            _scintilla.Lines[i].MarkerAddSet(0u);
        }
    }

    // ==================== 工作行范围 ====================
    /// <summary>要处理的行区间（含端点）；无跨行选区时为整个文档</summary>
    private (int First, int Last) WorkLineRange()
    {
        int lineCount = _scintilla.Lines.Count;
        if (lineCount == 0) return (0, -1);

        int anchorLine = _scintilla.LineFromPosition(_scintilla.SelectionStart);
        int caretLine = _scintilla.LineFromPosition(_scintilla.SelectionEnd);

        if (_scintilla.SelectionStart == _scintilla.SelectionEnd || anchorLine == caretLine)
            return (0, lineCount - 1);

        return (Math.Min(anchorLine, caretLine), Math.Max(anchorLine, caretLine));
    }

    /// <summary>行起点（SCI_POSITIONFROMLINE）</summary>
    private int LineStartPos(int line) => _scintilla.Lines[line].Position;

    /// <summary>行尾位置（含该行换行符，等于下一行起始）</summary>
    private int LineEndPos(int line)
        => line + 1 < _scintilla.Lines.Count ? _scintilla.Lines[line + 1].Position : _scintilla.TextLength;

    /// <summary>
    /// 本行内容末尾，不含行尾符（SCI_GETLINEENDPOSITION）。
    /// ScintillaNET 的 Line.EndPosition 是 Position+Length、长度里带行尾符，直接用会多吃掉一个换行。
    /// 这里的下标都是托管 API 的 UTF-16 码元位（ScintillaNET 内部再换算成 UTF-8 字节），
    /// 别再套 GetByteCount，否则行里有中文就会多推几个位置。
    /// </summary>
    private int LineContentEndPos(int line)
    {
        int start = LineStartPos(line);
        string text = _scintilla.GetTextRange(start, LineEndPos(line) - start);
        int cut = text.Length;
        if (cut > 0 && text[cut - 1] == '\n') cut--;
        if (cut > 0 && text[cut - 1] == '\r') cut--;
        return start + cut;
    }

    /// <summary>整行文本（不含换行符）</summary>
    private string LineBody(int line)
    {
        int s = LineStartPos(line);
        return _scintilla.GetTextRange(s, LineEndPos(line) - s).TrimEnd('\r', '\n');
    }

    /// <summary>整行文本（含换行符）</summary>
    private string LineFull(int line)
    {
        int s = LineStartPos(line);
        return _scintilla.GetTextRange(s, LineEndPos(line) - s);
    }

    private string EolString => _scintilla.EolMode switch
    {
        Eol.Cr => "\r",
        Eol.Lf => "\n",
        _ => "\r\n",
    };

    private void SetLineRange(int first, int last, IEnumerable<string> lines)
    {
        if (first < 0 || last < first) return;

        string[] arr = lines.ToArray();
        string text = string.Join(EolString, arr);
        if (arr.Length > 0) text += EolString;

        _scintilla.TargetStart = LineStartPos(first);
        _scintilla.TargetEnd = LineEndPos(last);
        _scintilla.ReplaceTarget(text);
    }

    private void WithUndoGroup(Action body)
    {
        _scintilla.BeginUndoAction();
        try { body(); }
        finally { _scintilla.EndUndoAction(); }
    }

    /// <summary>读出工作行 → 变换 → 写回</summary>
    private void TransformLines(Func<List<string>, List<string>?> transform)
    {
        var (first, last) = WorkLineRange();
        if (last < first) return;

        var lines = new List<string>(last - first + 1);
        for (int i = first; i <= last; i++) lines.Add(LineBody(i));

        var result = transform(lines);
        if (result == null) return;

        WithUndoGroup(() => SetLineRange(first, last, result));
    }

    // ==================== ND2 行操作作用域（dealLineSort / removeAnyDuplicateLines） ====================

    /// <summary>
    /// ND2 这类整段行操作的作用域，逐条照 <c>dealLineSort</c>（ccnotepad.cpp:9589）：
    /// 多光标只有矩形选区才处理并且按列切片，非矩形直接不动作；
    /// 普通选区必须跨行（单行选区 return），无选区才处理全文。
    /// </summary>
    private readonly record struct LineScope(bool Ok, bool HasSelection, int FromLine, int ToLine, int FromColumn, int ToColumn);

    private LineScope CurrentLineScope()
    {
        if (_scintilla.Selections.Count > 1)
        {
            if (!IsRectangleSelection) return new LineScope(false, false, 0, 0, 0, 0);

            int anchor = _scintilla.RectangularSelectionAnchor;
            int caret = _scintilla.RectangularSelectionCaret;
            int anchorLine = _scintilla.LineFromPosition(anchor);
            int caretLine = _scintilla.LineFromPosition(caret);
            int anchorOffset = anchor - LineStartPos(anchorLine) + _scintilla.RectangularSelectionAnchorVirtualSpace;
            int caretOffset = caret - LineStartPos(caretLine) + _scintilla.RectangularSelectionCaretVirtualSpace;

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
            return new LineScope(true, true, first, last, 0, 0);
        }

        return new LineScope(true, false, 0, _scintilla.Lines.Count - 1, 0, 0);
    }

    /// <summary>SCI_SELECTIONISRECTANGLE。ScintillaNET 没有把它包成属性，只能走 DirectMessage</summary>
    private bool IsRectangleSelection
        => _scintilla.DirectMessage(2372, IntPtr.Zero, IntPtr.Zero).ToInt64() != 0;

    /// <summary>ND2 getSelectionLinesRange：选区尾正好落在下一行行首时，不算进下一行</summary>
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
    /// </summary>
    private void RewriteLineScope(LineScope scope, Func<List<string>, List<string>> transform)
    {
        if (!scope.Ok || scope.FromLine >= scope.ToLine) return;

        int startPos = LineStartPos(scope.FromLine);
        int endPos = LineEndPos(scope.ToLine);
        string text = _scintilla.GetTextRange(startPos, endPos - startPos);
        string eol = EolString;

        var lines = new List<string>(text.Split(new[] { eol }, StringSplitOptions.None));
        bool entireDocument = scope.ToLine == _scintilla.Lines.Count - 1;
        if (!entireDocument && lines.Count > 0 && lines[^1].Length == 0)
        {
            // 选区末尾的 EOL 被 split 成了空段，它其实属于上一行；补回去会多出一行，先摘掉
            lines.RemoveAt(lines.Count - 1);
        }

        var result = transform(lines);

        string joined = string.Join(eol, result);
        if (!entireDocument) joined += eol;
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
            _scintilla.SetSelection(LineStartPos(scope.FromLine), LineContentEndPos(scope.ToLine));
        }
    }

    /// <summary>ND2 ISorter::getSortKey：toColumn!=0 时只有这一段列宽参与比较</summary>
    private static string SortKey(string line, int fromColumn, int toColumn)
    {
        if (toColumn == 0) return line;
        if (line.Length < fromColumn) return string.Empty;
        if (fromColumn == toColumn) return line[fromColumn..];
        return line.Substring(fromColumn, toColumn - fromColumn);
    }

    /// <summary>compare 为 null 时就是 ND2 的 ReverseSorter（std::reverse，列切片对它没意义）</summary>
    private void SortLinesNd(Comparison<string>? compare)
    {
        var scope = CurrentLineScope();
        RewriteLineScope(scope, lines =>
        {
            if (compare == null) lines.Reverse();
            else lines.Sort((a, b) => compare(SortKey(a, scope.FromColumn, scope.ToColumn),
                                               SortKey(b, scope.FromColumn, scope.ToColumn)));
            return lines;
        });
    }

    // ==================== 排序 ====================

    public enum SortMode
    {
        LexAsc, LexDesc,
        LexAscIgnoreCase, LexDescIgnoreCase,
        ReverseOrder,
        IntegerAsc, IntegerDesc,
        DecimalDotAsc, DecimalDotDesc,
        DecimalCommaAsc, DecimalCommaDesc,
        Randomize,
    }

    private static readonly Regex NumberPattern = new(@"[-+]?\d[\d.,]*", RegexOptions.Compiled);

    public void SortLines(SortMode mode)
    {
        // 前五个是 ND2 行编辑里真实接线的排序项，走它自己的作用域管线
        switch (mode)
        {
            case SortMode.LexAsc:
                SortLinesNd(string.CompareOrdinal);
                return;
            case SortMode.LexDesc:
                SortLinesNd((a, b) => -string.CompareOrdinal(a, b));
                return;
            case SortMode.LexAscIgnoreCase:
                SortLinesNd((a, b) => string.Compare(a, b, StringComparison.OrdinalIgnoreCase));
                return;
            case SortMode.LexDescIgnoreCase:
                SortLinesNd((a, b) => -string.Compare(a, b, StringComparison.OrdinalIgnoreCase));
                return;
            case SortMode.ReverseOrder:
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

    private static long ExtractInteger(string line)
    {
        var m = NumberPattern.Match(line);
        if (!m.Success) return long.MaxValue;

        string digits = new(m.Value.Where(char.IsDigit).ToArray());
        if (digits.Length == 0) return long.MaxValue;
        if (!long.TryParse(digits, out long v)) return long.MaxValue;
        return m.Value.StartsWith('-') ? -v : v;
    }

    private static double ExtractDecimal(string line, char decimalSeparator)
    {
        var m = NumberPattern.Match(line);
        if (!m.Success) return double.MaxValue;

        string raw = m.Value.Replace(decimalSeparator, '.');
        // 只保留第一个小数点，其余按千位分隔符丢弃
        int firstDot = raw.IndexOf('.');
        if (firstDot >= 0)
            raw = raw[..(firstDot + 1)] + raw[(firstDot + 1)..].Replace(".", "");

        bool negative = raw.StartsWith('-');
        string cleaned = new(raw.Where(c => char.IsDigit(c) || c == '.').ToArray());
        return double.TryParse(cleaned, NumberStyles.Float, CultureInfo.InvariantCulture, out double v)
            ? (negative ? -v : v)
            : double.MaxValue;
    }

    private static List<string> Shuffle(List<string> lines)
    {
        var rnd = new Random();
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
    /// </summary>
    public void RemoveDuplicateLines()
    {
        var scope = CurrentLineScope();
        RewriteLineScope(scope, lines =>
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            return lines.Where(seen.Add).ToList();
        });
    }

    /// <summary>
    /// ND2「移除空行」/「移除空行(包括空白字符)」（ccnotepad.cpp:9137/9143 → findwin.cpp:916）：
    /// 静默调用替换所有的正则引擎，无视选区处理整篇，返回替换次数好让宿主照 ND2 那样报状态栏。
    /// </summary>
    public int RemoveEmptyLines(bool containingBlankChars)
        => SilentRegexReplaceAll(containingBlankChars ? @"^[\t ]*$(\r\n|\r|\n)" : @"^$(\r\n|\r|\n)");

    /// <summary>连续空行压成一行（ND2 没有这一项，本地增强）</summary>
    public void RemoveConsecutiveEmptyLines()
        => TransformLines(l =>
        {
            var result = new List<string>();
            bool prevBlank = false;
            foreach (var line in l)
            {
                bool blank = line.Trim().Length == 0;
                if (blank && prevBlank) continue;
                result.Add(line);
                prevBlank = blank;
            }
            return result;
        });

    /// <summary>删除文档首尾的空行（ND2 没有这一项，本地增强）</summary>
    public void RemoveHeadEndEmptyLines()
        => TransformLines(l =>
        {
            int first = 0, last = l.Count - 1;
            while (first <= last && l[first].Trim().Length == 0) first++;
            while (last >= first && l[last].Trim().Length == 0) last--;
            return l.GetRange(first, Math.Max(0, last - first + 1));
        });

    /// <summary>以下三条是 ND2 的死控件（连续去重 / 去重后排序），保留为本地增强</summary>
    public void RemoveConsecutiveDuplicateLines()
        => TransformLines(l => l
            .Select((line, idx) => new { line, idx })
            .Where(x => x.idx == 0 || x.line != l[x.idx - 1])
            .Select(x => x.line)
            .ToList());

    /// <summary>去重并排序（「删除重复行」的另一变体）</summary>
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
    public int RemoveLineHeadBlank() => SilentRegexReplaceAll("^[ \t]+");

    /// <summary>ND2「去除行尾空白」(:9042 → removeLineHeadEndBlank(2))：<c>[ \t]+$</c></summary>
    public int RemoveLineEndBlank() => SilentRegexReplaceAll("[ \t]+$");

    /// <summary>ND2「去除行首尾空白」(:9051)：连着调上面两条，各报各的次数（状态栏后一条盖掉前一条）</summary>
    public (int Head, int End) RemoveLineHeadEndBlank() => (RemoveLineHeadBlank(), RemoveLineEndBlank());

    /// <summary>ND2 的静默调用通道：m_isStatic=true 的 slot_replaceAll，正则、整篇、无视选区、不弹确认</summary>
    private int SilentRegexReplaceAll(string pattern)
        => ReplaceAll(new SearchCondition(pattern, SearchMode.Regex, MatchCase: true, WholeWord: false), string.Empty);

    // ==================== 单行操作 ====================

    /// <summary>ND2 insertNewLineAboveCurrentLine（scintillaeditview.cpp:3014）：在第 N 行起点前插一个行尾符</summary>
    public void InsertBlankLineAbove()
    {
        string newline = EolString;
        int currentLine = CurrentLine;

        if (currentLine == 0)
            _scintilla.InsertText(0, newline);                       // 光标在第一行：直接在文档最前面垫一个行尾符
        else
            _scintilla.InsertText(LineStartPos(currentLine) - newline.Length, newline);

        // 插完当前行号还没变，它指向的就是新出来的那条空行
        _scintilla.SetEmptySelection(LineStartPos(currentLine));
    }

    /// <summary>ND2 insertNewLineBelowCurrentLine（:3032）：末行只能 append，其余在下一行起点前插</summary>
    public void InsertBlankLineBelow()
    {
        string newline = EolString;
        int currentLine = CurrentLine;

        if (currentLine == _scintilla.Lines.Count - 1)
            _scintilla.AppendText(newline);
        else
            _scintilla.InsertText(LineStartPos(currentLine + 1), newline);

        _scintilla.SetEmptySelection(LineStartPos(currentLine + 1));
    }

    /// <summary>ND2「复制当前行」Ctrl+D = SCI_LINEDUPLICATE（ccnotepad.cpp:9480）</summary>
    public void DuplicateCurrentLine() => _scintilla.DirectMessage(2404, IntPtr.Zero, IntPtr.Zero);

    /// <summary>ND2 上移/下移都是 SCI_MOVESELECTEDLINESUP/DOWN：有选区时整组选区行一起走</summary>
    public void MoveSelectedLinesUp() => _scintilla.DirectMessage(2620, IntPtr.Zero, IntPtr.Zero);

    public void MoveSelectedLinesDown()
    {
        _scintilla.DirectMessage(2621, IntPtr.Zero, IntPtr.Zero);
        // ND2 下移后还要 SCI_SCROLLRANGE 把选区拉回可视区
        _scintilla.ScrollRange(_scintilla.SelectionEnd, _scintilla.SelectionStart);
    }

    public void DeleteCurrentLine()
    {
        int line = CurrentLine;
        WithUndoGroup(() =>
        {
            _scintilla.TargetStart = LineStartPos(line);
            _scintilla.TargetEnd = LineEndPos(line);
            _scintilla.ReplaceTarget(string.Empty);
        });
        GoToLine(line);
    }

    /// <summary>删除光标前的内容（行首到光标）</summary>
    public void DeleteToLineStart()
    {
        int line = CurrentLine;
        _scintilla.SetSelection(LineStartPos(line), _scintilla.CurrentPosition);
        _scintilla.ReplaceSelection(string.Empty);
    }

    /// <summary>删除光标后的内容（光标到行尾）</summary>
    public void DeleteToLineEnd()
    {
        int line = CurrentLine;
        _scintilla.SetSelection(_scintilla.CurrentPosition, LineEndPos(line));
        _scintilla.ReplaceSelection(string.Empty);
    }

    /// <summary>剪切整行</summary>
    public void CutCurrentLine()
    {
        int line = CurrentLine;
        _scintilla.SetSelection(LineStartPos(line), LineEndPos(line));
        SafeCut();
        GoToLine(line);
    }

    /// <summary>复制整行到剪贴板</summary>
    public void CopyCurrentLine()
    {
        int line = CurrentLine;
        _scintilla.SetSelection(LineStartPos(line), LineEndPos(line));
        SafeCopy();
    }

    /// <summary>复制光标到行尾</summary>
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
    /// </summary>
    public void SplitLines()
    {
        if (_scintilla.Selections.Count != 1) return;

        var (first, last) = SelectionLinesRange();
        _scintilla.SetSelection(LineStartPos(first), LineContentEndPos(last));
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

        _scintilla.SetSelection(LineStartPos(first), LineContentEndPos(last));
        _scintilla.TargetFromSelection();
        _scintilla.DirectMessage(2288, IntPtr.Zero, IntPtr.Zero);
    }

    /// <summary>把选区中的转义 \n 变成真正换行（ND2 没有这一项，本地增强，挂在「更多行操作」）</summary>
    public void ExpandEscapedNewlines()
    {
        string sel = SelectedText;
        if (string.IsNullOrEmpty(sel)) return;

        string replaced = sel.Replace("\\r\\n", "\n").Replace("\\n", "\n").Replace("\\t", "\t");
        if (replaced == sel) return;

        _scintilla.ReplaceSelection(replaced);
    }

    // ==================== 大小写 ====================

    /// <summary>对应 ND2 的 TextCaseType（scintillaeditview.h:39）</summary>
    public enum CaseMode { Upper, Lower, Proper, ProperBlend, Sentence, SentenceBlend, Invert, Random }

    /// <summary>
    /// ND2 convertSelectedTextTo（scintillaeditview.cpp:2813）：只吃选区，
    /// 没有选区就什么都不做（不动当前行、也不提示），换完把选区原样还回去。
    /// </summary>
    public void ChangeCase(CaseMode mode)
    {
        int start = _scintilla.SelectionStart;
        int end = _scintilla.SelectionEnd;
        if (start == end) return;

        string text = _scintilla.GetTextRange(start, end - start);
        string result = ConvertCase(text, mode);

        _scintilla.TargetStart = start;
        _scintilla.TargetEnd = end;
        WithUndoGroup(() => _scintilla.ReplaceTarget(result));
        _scintilla.SetSelection(start, end);
    }

    /// <summary>逐条照 ND2 changeCase（scintillaeditview.cpp:2695-2811）搬过来，字符判断按 UTF-16 码元，和 Qt 的 QChar 一致</summary>
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
                bool force = mode == CaseMode.Proper;
                char[] a = text.ToCharArray();
                for (int i = 0; i < a.Length; i++)
                {
                    if (!char.IsLetter(a[i])) continue;

                    // 前一个字符不是字母也不是数字才算词首
                    if (i < 1 || !char.IsLetterOrDigit(a[i - 1])) a[i] = char.ToUpperInvariant(a[i]);
                    else if (force) a[i] = char.ToLowerInvariant(a[i]);

                    // 撇号例外：it's / rock'n'roll 里的词首字母要压回小写（BLEND 也生效）
                    if (i >= 2 && a[i - 1] == '\'' && char.IsLetter(a[i - 2])) a[i] = char.ToLowerInvariant(a[i]);
                }
                return new string(a);
            }

            case CaseMode.Sentence:
            case CaseMode.SentenceBlend:
            {
                bool force = mode == CaseMode.Sentence;
                char[] a = text.ToCharArray();
                bool isNewSentence = true;
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
                        if (wasEolR) isNewSentence = true; else wasEolR = true;
                    }
                    else if (a[i] == '\n')
                    {
                        if (wasEolN) isNewSentence = true; else wasEolN = true;
                    }
                }
                return new string(a);
            }

            case CaseMode.Invert:
            {
                char[] a = text.ToCharArray();
                for (int i = 0; i < a.Length; i++)
                    a[i] = char.IsLower(a[i]) ? char.ToUpperInvariant(a[i]) : char.ToLowerInvariant(a[i]);
                return new string(a);
            }

            default:
            {
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
    private static readonly Random CaseRandom = new();
    private static bool RandomBit() => (CaseRandom.Next(2) & 1) == 1;

    // ==================== Tab / 空格 / 行尾符 ====================

    /// <summary>ND2 <c>SpaceTab</c> 三态（ccnotepad.cpp:9161/9166/9171 → spaceTabConvert）</summary>
    public enum SpaceTabConversion { TabToSpace, SpaceToTabAll, SpaceToTabLeading }

    public void TabsToSpaces() => ConvertTabsSpaces(SpaceTabConversion.TabToSpace);

    public void SpacesToTabs(bool leadingOnly)
        => ConvertTabsSpaces(leadingOnly ? SpaceTabConversion.SpaceToTabLeading : SpaceTabConversion.SpaceToTabAll);

    /// <summary>
    /// 照 ND2 <c>spaceTabConvert</c>（ccnotepad.cpp:9270-9477）搬的列感知互转：
    /// 读全文 → 按制表位重排 → SCI_SETTEXT 写回 → GOTOPOS，**无视选区**，各包一个 undo group。
    /// TAB→空格是 <c>tabWidth - (column % tabWidth)</c> 个空格（不是一个 TAB 换 tabWidth 个）；
    /// 空格→TAB 是**制表位对齐**（<c>tabStop</c> 从 tabWidth-1 起、每跨一档加 tabWidth），不是每 tabWidth 个空格并一根 TAB。
    /// 列只让「一个字符」计一次：ND2 判 UTF-8 首字节 <c>(c &amp; 0xC0) != 0x80</c>，这里等价地跳过代理项低半区。
    /// </summary>
    public void ConvertTabsSpaces(SpaceTabConversion type)
    {
        var (text, newCurrentPos) = SpaceTabConvert(_scintilla.Text, _scintilla.TabWidth, _scintilla.CurrentPosition, type);
        if (text == null) return;

        WithUndoGroup(() =>
        {
            _scintilla.Text = text;
            _scintilla.SetEmptySelection(newCurrentPos);
        });
    }

    /// <summary>
    /// 上面那条的纯函数部分（拆出来只为能把状态机单独跑一遍）。返回 null = ND2 的那两个提前 return。
    /// </summary>
    public static (string? Text, int Caret) SpaceTabConvert(string source, int tabWidth, int currentPos, SpaceTabConversion type)
    {
        if (source.Length == 0) return (null, 0);     // ND2: docLength < 2 直接 return

        // ND2 对 Tab2Space 先数 TAB，一个都没有就连 undo 都不开、直接返回
        if (type == SpaceTabConversion.TabToSpace && source.IndexOf('\t') < 0) return (null, 0);

        // ND2 的 source 是 NUL 结尾的缓冲，越界读拿到的是 '\0'；这里用同样的读法兜住边界
        char Ch(int index) => index >= 0 && index < source.Length ? source[index] : '\0';

        var dest = new StringBuilder(source.Length + 16);
        int column = 0;
        int newCurrentPos = 0;

        if (type == SpaceTabConversion.TabToSpace)
        {
            for (int i = 0; i < source.Length; i++)
            {
                char c = Ch(i);
                if (c == '\t')
                {
                    int insertTabs = tabWidth - (column % tabWidth);
                    for (int j = 0; j < insertTabs; j++)
                    {
                        dest.Append(' ');
                        if (i <= currentPos) newCurrentPos++;
                    }
                    column += insertTabs;
                }
                else
                {
                    dest.Append(c);
                    if (i <= currentPos) newCurrentPos++;
                    if (c == '\n' || c == '\r') column = 0;
                    else if (!char.IsLowSurrogate(c)) column++;
                }
            }
        }
        else
        {
            bool onlyLeading = type == SpaceTabConversion.SpaceToTabLeading;
            // 三个变量都活在循环外，这是 ND2 的原样（counter 只在特定分支里归零）
            bool nextChar = false;
            int counter = 0;
            int tabStop = tabWidth - 1;   // remember, counting from zero!
            bool nonSpaceFound = false;

            for (int i = 0; i < source.Length; i++)
            {
                if (!nonSpaceFound)
                {
                    while (Ch(i + counter) == ' ')
                    {
                        if (column + counter == tabStop)
                        {
                            tabStop += tabWidth;
                            if (counter >= 1)
                            {
                                dest.Append('\t');
                                i += counter;
                                column += counter + 1;
                                counter = 0;
                                nextChar = true;
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
                    dest.Append(c);
                    column = 0;
                    tabStop = tabWidth - 1;
                    nonSpaceFound = false;
                }
                else if (c == '\t')
                {
                    dest.Append(c);
                    column = tabStop + 1;
                    tabStop += tabWidth;
                    counter = 0;
                }
                else
                {
                    dest.Append(c);
                    counter = 0;
                    if (!char.IsLowSurrogate(c))
                    {
                        column++;
                        if (column > 0 && column % tabWidth == 0) tabStop += tabWidth;
                    }
                }

                if (i <= currentPos) newCurrentPos++;
            }
        }

        return (dest.ToString(), newCurrentPos);
    }

    public int TabWidth
    {
        get => _scintilla.TabWidth;
        set => _scintilla.TabWidth = Math.Clamp(value, 1, 64);
    }

    public bool UseTabs
    {
        get => _scintilla.UseTabs;
        set => _scintilla.UseTabs = value;
    }

    public void ConvertEol(Eol eol)
    {
        WithUndoGroup(() =>
        {
            _scintilla.ConvertEols(eol);
            _scintilla.EolMode = eol;
        });
    }

    // ==================== 列编辑器 ====================

    public enum ColumnNumberFormat { Decimal, HexUpper, HexLower, Octal, Binary }

    /// <summary>
    /// 列编辑器：在选区的同一列上批量插入文本或编号。
    /// mode: 0=插入文本 1=复制首行 2=粘贴剪贴板 4=递增编号
    /// repeat: 每个编号重复的行数（&lt;1 视作 1）
    /// </summary>
    public void ColumnEditor(int mode, string text, int initial, int increment, int repeat, int numberDigits,
                             ColumnNumberFormat format, bool leadingZero, string prefix)
    {
        var (first, last) = WorkLineRange();
        if (last < first) return;
        if (repeat < 1) repeat = 1;

        int col = _scintilla.SelectionStart - LineStartPos(first);
        WithUndoGroup(() =>
        {
            for (int i = first; i <= last; i++)
            {
                string insert = mode switch
                {
                    1 => LineBody(first).Substring(Math.Min(col, LineBody(first).Length)),
                    2 => SafeClipboardText(),
                    3 => FormatColumnNumber(initial + ((i - first) / repeat) * increment, numberDigits, format, leadingZero, prefix),
                    _ => text,
                };

                int pos = LineStartPos(i) + Math.Min(col, LineEndPos(i) - LineStartPos(i));
                _scintilla.InsertText(pos, insert);
            }
        });
    }

    private static string FormatColumnNumber(int value, int digits, ColumnNumberFormat format,
                                             bool leadingZero, string prefix)
    {
        string body = format switch
        {
            ColumnNumberFormat.HexUpper => value.ToString("X", CultureInfo.InvariantCulture),
            ColumnNumberFormat.HexLower => value.ToString("x", CultureInfo.InvariantCulture),
            ColumnNumberFormat.Octal => Convert.ToString(value, 8),
            ColumnNumberFormat.Binary => Convert.ToString(value, 2),
            _ => value.ToString(CultureInfo.InvariantCulture),
        };

        if (leadingZero && digits > 0)
            body = body.PadLeft(digits, '0');

        return prefix + body;
    }

    private string SafeClipboardText()
    {
        try { return System.Windows.Clipboard.GetText(); }
        catch { return string.Empty; }
    }

    // ==================== 多列编辑 / 列模式 ====================

    /// <summary>
    /// 列编辑模式：Scintilla 本身没有「列模式」开关，鼠标拖选切矩形由
    /// MouseSelectionRectangularSwitch 承担（Ctrl 拖选 / Alt 拖选仍然有效）。
    /// </summary>
    public bool ColumnMode
    {
        get => _scintilla.MouseSelectionRectangularSwitch;
        set => _scintilla.MouseSelectionRectangularSwitch = value;
    }

    /// <summary>给选区每一行的行首加一个光标</summary>
    public void AddCaretsToLineStarts()
    {
        var (first, last) = WorkLineRange();
        _scintilla.ClearSelections();
        for (int i = first; i <= last; i++)
            _scintilla.AddSelection(LineStartPos(i), LineStartPos(i));
        if (first <= last) _scintilla.SetSelection(LineStartPos(first), LineStartPos(last));
    }

    /// <summary>把当前选区复制到选区覆盖的每一行行首</summary>
    public void PasteToEachSelectedLine() => _scintilla.MultipleSelectAddEach();

    // ==================== 书签批量操作（照 ND2 scintillaeditview.cpp:1479-1640） ====================

    /// <summary>ND2 的 getMarkedLine 走 SCI_LINELENGTH，取到的是含行尾的整行，所以行间天然带换行</summary>
    private string MarkedLine(int line) => LineFull(line) + EolString;

    /// <summary>ND2 deleteMarkedline：先摘掉书签标记，再连行尾一起把整行替换成空</summary>
    private void DeleteMarkedLine(int line)
    {
        int begin = LineStartPos(line);
        RemoveBookmark(line);
        _scintilla.TargetStart = begin;
        _scintilla.TargetEnd = LineEndPos(line);
        _scintilla.ReplaceTarget(string.Empty);
    }

    /// <summary>ND2 replaceMarkedline：SCI_POSITIONFROMLINE → SCI_GETLINEENDPOSITION，不含行尾，粘贴不会把下一行吸上来</summary>
    private void ReplaceMarkedLine(int line, string text)
    {
        _scintilla.TargetStart = LineStartPos(line);
        _scintilla.TargetEnd = LineContentEndPos(line);
        _scintilla.ReplaceTarget(text);
    }

    private string CollectMarkedLines()
    {
        var sb = new StringBuilder();
        for (int i = 0; i < _scintilla.Lines.Count; i++)
        {
            if (HasBookmark(i)) sb.Append(MarkedLine(i));
        }
        return sb.ToString();
    }

    private static void SetClipboard(string text)
    {
        try { System.Windows.Clipboard.SetText(text); }
        catch { /* 剪贴板被占用时忽略 */ }
    }

    /// <summary>复制书签行。没有书签时 ND2 也会把剪贴板写成空串，这里照做</summary>
    public void CopyMarkedLines() => SetClipboard(CollectMarkedLines());

    /// <summary>剪切书签行：先把文本收好，倒序删除（行号不失效），整体一个撤销组，最后写剪贴板</summary>
    public void CutMarkedLines()
    {
        string text = CollectMarkedLines();
        WithUndoGroup(() =>
        {
            for (int i = _scintilla.Lines.Count - 1; i >= 0; i--)
            {
                if (HasBookmark(i)) DeleteMarkedLine(i);
            }
        });
        SetClipboard(text);
    }

    /// <summary>
    /// 粘贴(替换)书签行：把剪贴板**整块**文本替换进每一个书签行，不是按行配对；
    /// 书签数为 0 什么都不做，粘完书签标记仍留在原行上。
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

    /// <summary>删除书签行（isMarked=true）/ 删除未标记行（isMarked=false），两者是同一个函数的两个方向</summary>
    public void DeleteMarkedLines(bool isMarked)
    {
        WithUndoGroup(() =>
        {
            for (int i = _scintilla.Lines.Count - 1; i >= 0; i--)
            {
                if (HasBookmark(i) == isMarked) DeleteMarkedLine(i);
            }
        });
    }

    /// <summary>反转所有书签（有书签的行取消，没书签的行加上）。ND2 这里不包撤销组，我们包了，算刻意偏离</summary>
    public void ToggleBookmarksOthers()
    {
        WithUndoGroup(() =>
        {
            for (int i = 0; i < _scintilla.Lines.Count; i++)
            {
                var line = _scintilla.Lines[i];
                if ((line.MarkerGet() & BookmarkMarkerMask) != 0)
                    line.MarkerDelete(BookmarkMarkerIndex);
                else
                    line.MarkerAdd(BookmarkMarkerIndex);
            }
        });
    }

    // ==================== 查找结果标记 ====================

    /// <summary>按关键词给整篇文档的行打书签（「标记全部」），不区分大小写</summary>
    public int MarkLinesContaining(string keyword)
    {
        if (string.IsNullOrEmpty(keyword)) return 0;

        int count = 0;
        WithUndoGroup(() =>
        {
            for (int i = 0; i < _scintilla.Lines.Count; i++)
            {
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

    private void OnModifyAttempted(object? sender, EventArgs e) => ReadOnlyEditAttempted?.Invoke(this, EventArgs.Empty);

    // ==================== URL 检测 ====================

    private static readonly Regex UrlPattern =
        new(@"\b(?:https?|ftp|mailto)://[^\s<>""'\)\]\}]+|\b[\w.-]+@[\w.-]+\.\w{2,}\b", RegexOptions.Compiled);

    /// <summary>返回光标 / 点击位置所在行的 URL，找不到返回 null</summary>
    public string? UrlAtPosition(int position)
    {
        int line = _scintilla.LineFromPosition(position);
        int lineStart = LineStartPos(line);
        string text = LineBody(line);

        foreach (Match m in UrlPattern.Matches(text))
        {
            int s = lineStart + m.Index;
            int e = s + m.Length;
            if (position >= s && position <= e) return m.Value;
        }

        // 不在任何 URL 内时，返回本行第一个 URL，方便 Ctrl+点空白处也能开
        var first = UrlPattern.Match(text);
        return first.Success ? first.Value : null;
    }

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

    /// <summary>用文档中已有的单词 + 语言关键字做补全候选</summary>
    public void AutoCompleteFromDocument()
    {
        var words = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match m in Regex.Matches(_scintilla.Text, @"[\p{L}_][\p{L}\p{Nd}_]{2,}"))
        {
            words.Add(m.Value);
            if (words.Count >= 4000) break;
        }

        if (words.Count == 0) return;

        string list = string.Join(' ', words.OrderBy(w => w, StringComparer.Ordinal));
        _scintilla.AutoCMaxHeight = 200;
        _scintilla.AutoCIgnoreCase = true;
        _scintilla.AutoCShow(0, list);
    }

    // ==================== 对齐 / 补位 ====================

    /// <summary>把行尾补空格对齐到同一列</summary>
    public void PadLinesToEqualWidth()
    {
        var (first, last) = WorkLineRange();
        if (last <= first) return;

        int max = 0;
        var lines = new List<string>();
        for (int i = first; i <= last; i++)
        {
            string body = LineBody(i);
            lines.Add(body);
            max = Math.Max(max, body.Length);
        }

        WithUndoGroup(() => SetLineRange(first, last, lines.Select(x => x.PadRight(max))));
    }
}
