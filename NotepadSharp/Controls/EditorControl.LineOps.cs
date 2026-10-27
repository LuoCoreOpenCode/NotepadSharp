using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using ScintillaNET;

namespace NotepadSharp.Controls;

/// <summary>
/// 编辑器行 / 列批量操作，对应「格式」菜单。
/// 语义：有跨行选区时只处理选区内的行，
/// 否则处理整个文档；批量改动都包在一个 undo action 内，可一次撤销。
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

    private int LineStartPos(int line) => _scintilla.Lines[line].Position;

    /// <summary>行尾位置（含该行换行符，等于下一行起始）</summary>
    private int LineEndPos(int line)
        => line + 1 < _scintilla.Lines.Count ? _scintilla.Lines[line + 1].Position : _scintilla.TextLength;

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

    // ==================== 排序 ====================

    public enum SortMode
    {
        LexAsc, LexDesc,
        LexAscIgnoreCase, LexDescIgnoreCase,
        IntegerAsc, IntegerDesc,
        DecimalDotAsc, DecimalDotDesc,
        DecimalCommaAsc, DecimalCommaDesc,
        ReverseOrder, Randomize,
    }

    private static readonly Regex NumberPattern = new(@"[-+]?\d[\d.,]*", RegexOptions.Compiled);

    public void SortLines(SortMode mode)
    {
        switch (mode)
        {
            case SortMode.LexAsc:
                TransformLines(l => l.OrderBy(x => x, StringComparer.Ordinal).ToList());
                break;
            case SortMode.LexDesc:
                TransformLines(l => l.OrderByDescending(x => x, StringComparer.Ordinal).ToList());
                break;
            case SortMode.LexAscIgnoreCase:
                TransformLines(l => l.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList());
                break;
            case SortMode.LexDescIgnoreCase:
                TransformLines(l => l.OrderByDescending(x => x, StringComparer.OrdinalIgnoreCase).ToList());
                break;
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
            case SortMode.ReverseOrder:
                TransformLines(l => { l.Reverse(); return l; });
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

    public void RemoveConsecutiveDuplicateLines()
        => TransformLines(l => l
            .Select((line, idx) => new { line, idx })
            .Where(x => x.idx == 0 || x.line != l[x.idx - 1])
            .Select(x => x.line)
            .ToList());

    public void RemoveDuplicateLines()
        => TransformLines(l =>
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            return l.Where(seen.Add).ToList();
        });

    /// <summary>去重并排序（「删除重复行」的另一变体）</summary>
    public void RemoveDuplicateLinesSorted()
        => TransformLines(l =>
        {
            var sorted = l.OrderBy(x => x, StringComparer.Ordinal).ToList();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            return sorted.Where(seen.Add).ToList();
        });

    /// <summary>删除所有空行</summary>
    public void RemoveEmptyLines()
        => TransformLines(l => l.Where(x => x.Trim().Length != 0).ToList());

    /// <summary>连续空行折叠成一行</summary>
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

    /// <summary>删除文档首尾的空行</summary>
    public void RemoveHeadEndEmptyLines()
        => TransformLines(l =>
        {
            int first = 0, last = l.Count - 1;
            while (first <= last && l[first].Trim().Length == 0) first++;
            while (last >= first && l[last].Trim().Length == 0) last--;
            return l.GetRange(first, Math.Max(0, last - first + 1));
        });

    /// <summary>删除行首空白</summary>
    public void RemoveLeadingSpace()
        => TransformLines(l => l.Select(x => x.TrimStart()).ToList());

    /// <summary>删除行尾空白</summary>
    public void RemoveTrailingSpace()
        => TransformLines(l => l.Select(x => x.TrimEnd()).ToList());

    /// <summary>删除所有空白（行首行尾）</summary>
    public void RemoveAllSpace()
        => TransformLines(l => l.Select(x => x.Trim()).ToList());

    // ==================== 单行操作 ====================

    public void InsertBlankLineAbove()
    {
        int line = CurrentLine;
        WithUndoGroup(() => _scintilla.InsertText(LineStartPos(line), EolString));
        GoToLine(line);
    }

    public void InsertBlankLineBelow()
    {
        int line = CurrentLine;
        WithUndoGroup(() => _scintilla.InsertText(LineEndPos(line), EolString));
        GoToLine(line + 1);
    }

    public void DuplicateCurrentLine()
    {
        int line = CurrentLine;
        string text = LineFull(line);
        WithUndoGroup(() => _scintilla.InsertText(LineEndPos(line), text));
        GoToLine(line + 1);
    }

    public void MoveCurrentLineUp()
    {
        int line = CurrentLine;
        if (line <= 0) return;
        string cur = LineFull(line);
        string prev = LineFull(line - 1);

        WithUndoGroup(() =>
        {
            SetLineRange(line - 1, line, new[] { cur.TrimEnd('\r', '\n'), prev.TrimEnd('\r', '\n') });
        });
        GoToLine(line - 1);
    }

    public void MoveCurrentLineDown()
    {
        int line = CurrentLine;
        if (line >= _scintilla.Lines.Count - 1) return;
        string cur = LineFull(line);
        string next = LineFull(line + 1);

        WithUndoGroup(() =>
        {
            SetLineRange(line, line + 1, new[] { next.TrimEnd('\r', '\n'), cur.TrimEnd('\r', '\n') });
        });
        GoToLine(line + 1);
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

    /// <summary>合并选区内的行</summary>
    public void JoinLines()
    {
        var (first, last) = WorkLineRange();
        if (last <= first) return;

        var merged = new StringBuilder(LineBody(first));
        for (int i = first + 1; i <= last; i++)
        {
            merged.Append(' ');
            merged.Append(LineBody(i).TrimStart());
        }

        WithUndoGroup(() => SetLineRange(first, last, new[] { merged.ToString() }));
        GoToLine(first);
    }

    /// <summary>把选区中的转义 \n 变成真正换行</summary>
    public void SplitLines()
    {
        string sel = SelectedText;
        if (string.IsNullOrEmpty(sel)) return;

        string replaced = sel.Replace("\\r\\n", "\n").Replace("\\n", "\n").Replace("\\t", "\t");
        if (replaced == sel) return;

        _scintilla.ReplaceSelection(replaced);
    }

    // ==================== 大小写 ====================

    public enum CaseMode { Upper, Lower, Proper, ProperBlend, Sentence, SentenceBlend, Invert, Random }

    /// <summary>作用于选区；无选区时作用于光标所在单词</summary>
    public void ChangeCase(CaseMode mode)
    {
        int start = _scintilla.SelectionStart;
        int end = _scintilla.SelectionEnd;

        if (start == end)
        {
            int wordStart = _scintilla.WordStartPosition(_scintilla.CurrentPosition, true);
            int wordEnd = _scintilla.WordEndPosition(_scintilla.CurrentPosition, true);
            if (wordEnd <= wordStart) return;
            start = wordStart;
            end = wordEnd;
        }

        string text = _scintilla.GetTextRange(start, end - start);
        string result = mode switch
        {
            CaseMode.Upper => text.ToUpperInvariant(),
            CaseMode.Lower => text.ToLowerInvariant(),
            CaseMode.Proper => ToProper(text, true),
            CaseMode.ProperBlend => ToProper(text, false),
            CaseMode.Sentence => ToSentence(text, true),
            CaseMode.SentenceBlend => ToSentence(text, false),
            CaseMode.Invert => ToInvert(text),
            CaseMode.Random => ToRandomCase(text),
            _ => text,
        };

        WithUndoGroup(() =>
        {
            _scintilla.TargetStart = start;
            _scintilla.TargetEnd = end;
            _scintilla.ReplaceTarget(result);
        });
    }

    private static string ToProper(string text, bool lowerRest)
    {
        var sb = new StringBuilder(text.Length);
        bool atWordStart = true;
        foreach (char c in text)
        {
            if (char.IsLetter(c))
            {
                sb.Append(atWordStart ? char.ToUpperInvariant(c)
                      : (lowerRest ? char.ToLowerInvariant(c) : c));
                atWordStart = false;
            }
            else
            {
                sb.Append(c);
                atWordStart = !char.IsDigit(c);
            }
        }
        return sb.ToString();
    }

    private static string ToSentence(string text, bool lowerRest)
    {
        var sb = new StringBuilder(text.Length);
        bool atSentenceStart = true;
        foreach (char c in text)
        {
            if (char.IsLetter(c))
            {
                sb.Append(atSentenceStart ? char.ToUpperInvariant(c)
                      : (lowerRest ? char.ToLowerInvariant(c) : c));
                atSentenceStart = false;
            }
            else
            {
                sb.Append(c);
                if (c is '.' or '!' or '?' or '。' or '！' or '？') atSentenceStart = true;
            }
        }
        return sb.ToString();
    }

    private static string ToInvert(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (char c in text)
            sb.Append(char.IsUpper(c) ? char.ToLowerInvariant(c)
                    : char.IsLower(c) ? char.ToUpperInvariant(c) : c);
        return sb.ToString();
    }

    private static string ToRandomCase(string text)
    {
        var rnd = new Random();
        var sb = new StringBuilder(text.Length);
        foreach (char c in text)
        {
            if (!char.IsLetter(c)) { sb.Append(c); continue; }
            sb.Append(rnd.Next(2) == 0 ? char.ToUpperInvariant(c) : char.ToLowerInvariant(c));
        }
        return sb.ToString();
    }

    // ==================== Tab / 空格 / 行尾符 ====================

    public void TabsToSpaces(bool leadingOnly)
    {
        int tabWidth = _scintilla.TabWidth;
        TransformLines(lines => lines.Select(l =>
        {
            if (leadingOnly)
            {
                int i = 0;
                while (i < l.Length && (l[i] == ' ' || l[i] == '\t')) i++;
                return l[..i].Replace("\t", new string(' ', tabWidth)) + l[i..];
            }
            return l.Replace("\t", new string(' ', tabWidth));
        }).ToList());
    }

    public void SpacesToTabs(bool leadingOnly)
    {
        int tabWidth = _scintilla.TabWidth;
        string run = new(' ', tabWidth);
        TransformLines(lines => lines.Select(l =>
        {
            if (leadingOnly)
            {
                int i = 0;
                while (i < l.Length && (l[i] == ' ' || l[i] == '\t')) i++;
                return l[..i].Replace(run, "\t") + l[i..];
            }
            return l.Replace(run, "\t");
        }).ToList());
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

    // ==================== 书签批量操作 ====================

    /// <summary>把所有带书签的行复制到剪贴板</summary>
    public void CopyLinesWithBookmarks()
    {
        var sb = new StringBuilder();
        for (int i = 0; i < _scintilla.Lines.Count; i++)
        {
            if (HasBookmark(i)) sb.Append(LineFull(i));
        }
        if (sb.Length == 0) return;

        try { System.Windows.Clipboard.SetText(sb.ToString()); }
        catch { /* 剪贴板被占用时忽略 */ }
    }

    public void DeleteLinesWithBookmarks()
    {
        var keep = new List<string>();
        for (int i = 0; i < _scintilla.Lines.Count; i++)
        {
            if (!HasBookmark(i)) keep.Add(LineBody(i));
        }
        WithUndoGroup(() => SetLineRange(0, _scintilla.Lines.Count - 1, keep));
    }

    /// <summary>反转所有书签（有书签的行取消，没书签的行加上）</summary>
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

    // ==================== 块注释 / 流式注释 ====================

    /// <summary>给选区每行加行注释前缀</summary>
    public void BlockComment(string prefix)
    {
        if (string.IsNullOrEmpty(prefix)) return;
        var (first, last) = WorkLineRange();
        if (last < first) return;

        WithUndoGroup(() =>
        {
            for (int i = first; i <= last; i++)
                _scintilla.InsertText(LineStartPos(i), prefix);
        });
    }

    /// <summary>去掉选区每行的行注释前缀</summary>
    public void BlockUncomment(string prefix)
    {
        if (string.IsNullOrEmpty(prefix)) return;
        var (first, last) = WorkLineRange();
        if (last < first) return;

        WithUndoGroup(() =>
        {
            for (int i = first; i <= last; i++)
            {
                string body = LineBody(i);
                int idx = body.IndexOf(prefix, StringComparison.Ordinal);
                if (idx < 0) continue;
                int len = prefix.Length;
                if (idx + len < body.Length && body[idx + len] == ' ') len++;
                _scintilla.TargetStart = LineStartPos(i) + idx;
                _scintilla.TargetEnd = LineStartPos(i) + idx + len;
                _scintilla.ReplaceTarget(string.Empty);
            }
        });
    }

    /// <summary>把选区包进流式注释（/* ... *\/）</summary>
    public void StreamComment(string begin, string end)
    {
        if (string.IsNullOrEmpty(begin) || string.IsNullOrEmpty(end)) return;
        if (_scintilla.SelectionStart == _scintilla.SelectionEnd) return;

        int s = _scintilla.SelectionStart;
        int e = _scintilla.SelectionEnd;
        string text = _scintilla.GetTextRange(s, e - s);

        bool already = text.StartsWith(begin) && text.EndsWith(end);
        string result = already
            ? text[begin.Length..^end.Length]
            : begin + text + end;

        WithUndoGroup(() =>
        {
            _scintilla.TargetStart = s;
            _scintilla.TargetEnd = e;
            _scintilla.ReplaceTarget(result);
        });
    }

    /// <summary>切换选区内每一行的注释状态</summary>
    public void ToggleLineComment(string prefix)
    {
        var (first, last) = WorkLineRange();
        if (last < first || string.IsNullOrEmpty(prefix)) return;

        bool anyUncommented = false;
        for (int i = first; i <= last; i++)
        {
            if (!LineBody(i).TrimStart().StartsWith(prefix, StringComparison.Ordinal))
            {
                anyUncommented = true;
                break;
            }
        }

        if (anyUncommented) BlockComment(prefix);
        else BlockUncomment(prefix);
    }
}
