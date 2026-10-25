using System.Windows.Forms;
using System.Windows.Forms.Integration;
using DrawingColor = System.Drawing.Color;
using NotepadSharp.Services;
using ScintillaNET;

namespace NotepadSharp.Controls;

/// <summary>
/// 编辑器控件：用 WPF WindowsFormsHost 包裹 Scintilla 控件
/// </summary>
public partial class EditorControl : WindowsFormsHost
{
    private readonly Scintilla _scintilla;

    /// <summary>编辑区收到鼠标按下（每个编辑器实例共用，主窗口用它把程序抬到所有窗口最前）</summary>
    public static event Action? EditorMouseDown;

    // 书签使用的 marker 索引
    public const int BookmarkMarkerIndex = 2;
    private const uint BookmarkMarkerMask = 1u << BookmarkMarkerIndex;

    // 查找高亮使用的 indicator 索引
    private const int FindIndicatorIndex = 8;

    /// <summary>当前语言的注释前缀（由主窗口根据文件类型设置）</summary>
    public string CommentPrefix { get; set; } = "//";

    // Scintilla 标准折叠标记索引
    private const int SC_MARKNUM_FOLDEREND = 25;
    private const int SC_MARKNUM_FOLDEROPENMID = 26;
    private const int SC_MARKNUM_FOLDERMIDTAIL = 27;
    private const int SC_MARKNUM_FOLDERTAIL = 28;
    private const int SC_MARKNUM_FOLDERSUB = 29;
    private const int SC_MARKNUM_FOLDER = 30;
    private const int SC_MARKNUM_FOLDEROPEN = 31;
    private const uint SC_MASK_FOLDERS = 0xFE000000u;

    public EditorControl()
    {
        _scintilla = new Scintilla
        {
            Dock = DockStyle.Fill,
            Font = new System.Drawing.Font("Consolas", 10),
        };

        SetupEditor();
        Child = _scintilla;

        // Scintilla 的句柄创建后才真正能接收样式/词法器设置，
        // 否则在控件加入可视树之前调用会被丢弃
        _scintilla.HandleCreated += (_, _) =>
        {
            ApplyLexer();
            ApplyTheme();
        };
    }

    private void SetupEditor()
    {
        // ===== 基本设置 =====
        _scintilla.WrapMode = ScintillaNET.WrapMode.None;
        _scintilla.IndentationGuides = IndentView.LookBoth;
        _scintilla.TabWidth = 4;
        _scintilla.UseTabs = false;
        _scintilla.CaretWidth = 2;

        // ===== 边距设置 =====
        // 0: 行号
        _scintilla.Margins[0].Type = MarginType.Number;
        _scintilla.Margins[0].Width = 50;
        _scintilla.Margins[0].Sensitive = false;
        // 1: 折叠
        _scintilla.Margins[1].Type = MarginType.Symbol;
        _scintilla.Margins[1].Mask = SC_MASK_FOLDERS;
        _scintilla.Margins[1].Width = 16;
        _scintilla.Margins[1].Sensitive = true;
        // 2: 书签
        _scintilla.Margins[2].Type = MarginType.Symbol;
        _scintilla.Margins[2].Mask = BookmarkMarkerMask;
        _scintilla.Margins[2].Width = 14;
        _scintilla.Margins[2].Sensitive = true;
        // 3+: 关闭
        for (int i = 3; i < 5; i++)
        {
            _scintilla.Margins[i].Width = 0;
        }

        // ===== 折叠标记样式 =====
        _scintilla.SetFoldFlags(FoldFlags.LineAfterContracted);

        ConfigureFoldMarker(_scintilla.Markers[SC_MARKNUM_FOLDEROPEN], MarkerSymbol.BoxMinusConnected);
        ConfigureFoldMarker(_scintilla.Markers[SC_MARKNUM_FOLDER], MarkerSymbol.BoxPlusConnected);
        ConfigureFoldMarker(_scintilla.Markers[SC_MARKNUM_FOLDERSUB], MarkerSymbol.VLine);
        ConfigureFoldMarker(_scintilla.Markers[SC_MARKNUM_FOLDERTAIL], MarkerSymbol.LCorner);
        ConfigureFoldMarker(_scintilla.Markers[SC_MARKNUM_FOLDEREND], MarkerSymbol.BoxPlusConnected);
        ConfigureFoldMarker(_scintilla.Markers[SC_MARKNUM_FOLDEROPENMID], MarkerSymbol.BoxMinusConnected);
        ConfigureFoldMarker(_scintilla.Markers[SC_MARKNUM_FOLDERMIDTAIL], MarkerSymbol.TCorner);

        // ===== 书签标记样式 =====
        var bookmark = _scintilla.Markers[BookmarkMarkerIndex];
        bookmark.Symbol = MarkerSymbol.Bookmark;
        bookmark.SetForeColor(DrawingColor.FromArgb(255, 220, 160, 60));
        bookmark.SetBackColor(DrawingColor.FromArgb(255, 220, 160, 60));

        // ===== 自动折叠 =====
        _scintilla.AutomaticFold = AutomaticFold.Show | AutomaticFold.Click | AutomaticFold.Change;

        // ===== 空白字符 / 行尾显示 =====
        _scintilla.WhitespaceSize = 1;
        _scintilla.ViewWhitespace = WhitespaceMode.Invisible;
        _scintilla.ViewEol = false;

        // ===== 查找高亮 indicator =====
        var indicator = _scintilla.Indicators[FindIndicatorIndex];
        indicator.Style = IndicatorStyle.StraightBox;
        indicator.ForeColor = DrawingColor.FromArgb(0xFF, 0x2A, 0x7D, 0xD2);
        indicator.Alpha = 80;
        indicator.Under = true;

        // ===== 右键菜单 =====
        _scintilla.ContextMenuStrip = BuildContextMenu();

        // ===== Ctrl + 滚轮缩放 =====
        _scintilla.MouseWheel += OnMouseWheel;

        // 编辑区是 Win32 子 HWND，点击不会走 WPF 输入管线，主窗口只能靠这个通知补一次抬层
        _scintilla.MouseDown += (_, _) => EditorMouseDown?.Invoke();

        // ===== 事件 =====
        _scintilla.MarginClick += OnMarginClick;
        _scintilla.CursorChanged += OnCursorChanged;
        _scintilla.UpdateUI += OnUpdateUI;
        _scintilla.Insert += OnTextChanged;
        _scintilla.Delete += OnTextChanged;
        _scintilla.SavePointLeft += OnSavePointLeft;
        _scintilla.SavePointReached += OnSavePointReached;
        _scintilla.KeyDown += OnScintillaKeyDown;
    }

    /// <summary>编辑器内按下的快捷键（命令名），由主窗口处理</summary>
    public event EventHandler<string>? ShortcutPressed;

    private void OnScintillaKeyDown(object? sender, System.Windows.Forms.KeyEventArgs e)
    {
        string? cmd = null;

        if (e.Control && e.Shift && e.KeyCode == Keys.S) cmd = "saveas";
        else if (e.Control && e.KeyCode == Keys.S) cmd = "save";
        else if (e.Control && e.KeyCode == Keys.N) cmd = "new";
        else if (e.Control && e.KeyCode == Keys.O) cmd = "open";
        else if (e.Control && e.KeyCode == Keys.F) cmd = "find";
        else if (e.Control && e.KeyCode == Keys.G) cmd = "gotoline";
        else if (e.Control && e.KeyCode == Keys.W) cmd = "closefile";
        else if (e.Control && e.KeyCode == Keys.T) cmd = "theme";
        else if (e.Control && (e.KeyCode == Keys.OemQuestion || e.KeyCode == Keys.Oem2)) cmd = "comment";
        else if (e.Control && (e.KeyCode == Keys.Oemplus || e.KeyCode == Keys.Add)) cmd = "zoomin";
        else if (e.Control && (e.KeyCode == Keys.OemMinus || e.KeyCode == Keys.Subtract)) cmd = "zoomout";
        else if (e.Control && (e.KeyCode == Keys.D0 || e.KeyCode == Keys.NumPad0)) cmd = "zoomreset";
        else if (e.KeyCode == Keys.F2) cmd = "bookmark";
        else if (e.KeyCode == Keys.F3 && e.Shift) cmd = "findprev";
        else if (e.KeyCode == Keys.F3) cmd = "findnext";

        if (cmd != null)
        {
            ShortcutPressed?.Invoke(this, cmd);
            e.Handled = true;
            e.SuppressKeyPress = true;
        }
    }

    private ContextMenuStrip BuildContextMenu()
    {
        var menu = new ContextMenuStrip { ShowImageMargin = false };

        menu.Items.Add("撤销", null, (_, _) => _scintilla.Undo());
        menu.Items.Add("重做", null, (_, _) => _scintilla.Redo());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("剪切", null, (_, _) => _scintilla.Cut());
        menu.Items.Add("复制", null, (_, _) => _scintilla.Copy());
        menu.Items.Add("粘贴", null, (_, _) => _scintilla.Paste());
        menu.Items.Add("全选", null, (_, _) => _scintilla.SelectAll());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("切换注释", null, (_, _) => ToggleComment(CommentPrefix));
        menu.Items.Add("转为大写", null, (_, _) => UpperCaseSelection());
        menu.Items.Add("转为小写", null, (_, _) => LowerCaseSelection());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("切换书签", null, (_, _) =>
        {
            int line = CurrentLine;
            ToggleBookmark(line);
            BookmarkToggled?.Invoke(this, line);
        });
        menu.Items.Add("展开/折叠", null, (_, _) => _scintilla.Lines[CurrentLine].ToggleFold());

        return menu;
    }

    private void OnMouseWheel(object? sender, MouseEventArgs e)
    {
        if ((Control.ModifierKeys & Keys.Control) == Keys.Control)
        {
            ZoomLevel += e.Delta > 0 ? 1 : -1;
        }
    }

    private static void ConfigureFoldMarker(Marker marker, MarkerSymbol symbol)
    {
        marker.Symbol = symbol;
        marker.SetForeColor(DrawingColor.FromArgb(255, 240, 240, 240));
        marker.SetBackColor(DrawingColor.FromArgb(255, 160, 160, 160));
    }

    public Scintilla Scintilla => _scintilla;

    // ==================== 事件 ====================

    public event EventHandler? CursorPositionChanged;
    public event EventHandler? TextModified;
    public event EventHandler<bool>? SavePointChanged;
    public event EventHandler<int>? BookmarkToggled;

    private void OnMarginClick(object? sender, MarginClickEventArgs e)
    {
        int line = _scintilla.LineFromPosition(e.Position);

        if (e.Margin == 1)
        {
            // 折叠边距
            _scintilla.Lines[line].ToggleFold();
        }
        else if (e.Margin == 2)
        {
            // 书签边距
            ToggleBookmark(line);
            BookmarkToggled?.Invoke(this, line);
        }
    }

    private void OnCursorChanged(object? sender, EventArgs e)
    {
        CursorPositionChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnUpdateUI(object? sender, UpdateUIEventArgs e)
    {
        CursorPositionChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnTextChanged(object? sender, EventArgs e)
    {
        TextModified?.Invoke(this, EventArgs.Empty);
    }

    private void OnSavePointLeft(object? sender, EventArgs e)
    {
        SavePointChanged?.Invoke(this, false);
    }

    private void OnSavePointReached(object? sender, EventArgs e)
    {
        SavePointChanged?.Invoke(this, true);
    }

    // ==================== 内容 ====================

    public void LoadContent(string content)
    {
        _scintilla.ClearAll();
        _scintilla.AddText(content);
        _scintilla.EmptyUndoBuffer();
        // 载入即视为已保存状态，否则启动后每个标签都带 ● 脏标记
        _scintilla.SetSavePoint();
    }

    public string GetContent()
    {
        return _scintilla.Text;
    }

    // ==================== 列编辑 ====================

    /// <summary>选区覆盖的行范围与列位置；选区停在下一行行首时不算那行</summary>
    public (int StartLine, int EndLine, int StartColumn, int CaretColumn) GetColumnEditContext()
    {
        int start = _scintilla.SelectionStart, end = _scintilla.SelectionEnd;
        int startLine = _scintilla.LineFromPosition(start);
        int endLine = _scintilla.LineFromPosition(end);
        if (endLine > startLine && end == _scintilla.Lines[endLine].Position) endLine--;
        return (startLine, endLine, _scintilla.GetColumn(start), _scintilla.GetColumn(_scintilla.CurrentPosition));
    }

    /// <summary>在指定行、列插入文本（列超出该行长度时贴到行尾）</summary>
    public void InsertColumnText(int line, int column, string text)
    {
        var ln = _scintilla.Lines[line];
        _scintilla.InsertText(ln.Position + Math.Min(column, ln.Length), text);
    }

    public void BeginEditGroup() => _scintilla.BeginUndoAction();

    public void EndEditGroup() => _scintilla.EndUndoAction();

    // ==================== 语法高亮 ====================

    /// <summary>
    /// 根据文件路径设置 lexer（使用 Lexilla 名称，大小写不敏感匹配）
    /// </summary>
    public void SetLexerForFile(string filePath)
    {
        _pendingFilePath = filePath;
        ApplyLexer();
    }

    /// <summary>当前词法器名称（自行记录，不依赖 Scintilla 的读回）</summary>
    public string CurrentLexerName { get; private set; } = "null";

    // Lexilla 名称 → Legacy Lexer 枚举
    private static readonly System.Collections.Generic.Dictionary<string, ScintillaNET.Lexer> EnumLexers =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["cpp"] = ScintillaNET.Lexer.Cpp,
            ["python"] = ScintillaNET.Lexer.Python,
            ["hypertext"] = ScintillaNET.Lexer.Html,
            ["html"] = ScintillaNET.Lexer.Html,
            ["xml"] = ScintillaNET.Lexer.Xml,
            ["css"] = ScintillaNET.Lexer.Css,
            ["json"] = ScintillaNET.Lexer.Json,
            ["sql"] = ScintillaNET.Lexer.Sql,
            ["markdown"] = ScintillaNET.Lexer.Markdown,
            ["batch"] = ScintillaNET.Lexer.Batch,
            ["props"] = ScintillaNET.Lexer.Properties,
            ["properties"] = ScintillaNET.Lexer.Properties,
            ["powershell"] = ScintillaNET.Lexer.PowerShell,
            ["lua"] = ScintillaNET.Lexer.Lua,
            ["perl"] = ScintillaNET.Lexer.Perl,
            ["ruby"] = ScintillaNET.Lexer.Ruby,
            ["pascal"] = ScintillaNET.Lexer.Pascal,
            ["matlab"] = ScintillaNET.Lexer.Matlab,
            ["asm"] = ScintillaNET.Lexer.Asm,
            ["fortran"] = ScintillaNET.Lexer.Fortran,
            ["lisp"] = ScintillaNET.Lexer.Lisp,
            ["verilog"] = ScintillaNET.Lexer.Verilog,
            ["r"] = ScintillaNET.Lexer.R,
        };

    private string? _pendingFilePath;

    /// <summary>按当前主题给编辑器上色</summary>
    public void ApplyTheme()
    {
        ThemeService.ApplyToScintilla(_scintilla, CurrentLexerName);
    }

    private void ApplyLexer()
    {
        if (!_scintilla.IsHandleCreated || _pendingFilePath == null) return;

        SetLexerByNameInternal(LexerService.GetLexerName(_pendingFilePath), _pendingFilePath);
        ApplyTheme();
    }

    public void SetLexerByName(string lexerName)
    {
        SetLexerByNameInternal(lexerName, null);
        ApplyTheme();
    }

    private void SetLexerByNameInternal(string lexerName, string? filePath)
    {
        if (string.IsNullOrEmpty(lexerName)) lexerName = "null";

        // 触发 Lexilla 原生库初始化
        try { ScintillaNET.Lexilla.GetLexerNames(); }
        catch { /* 原生库缺失时退回内置 lexer */ }

        // 优先用 Lexer 枚举属性（旧版标识，最可靠）
        string applied = lexerName;
        bool ok = false;
        if (EnumLexers.TryGetValue(lexerName, out var lx))
        {
            try { _scintilla.Lexer = lx; ok = true; }
            catch { ok = false; }
        }

        if (!ok)
        {
            // 回退：字符串属性
            try { _scintilla.LexerLanguage = lexerName; ok = true; } catch { }
            if (!ok)
            {
                try { _scintilla.LexerLanguage = lexerName.ToLowerInvariant(); applied = lexerName.ToLowerInvariant(); }
                catch { }
            }
            if (!ok) applied = "null";
        }
        CurrentLexerName = applied;

        // 注入关键字表（cpp / python 等 lexer 本身不带这些语言的关键字）
        if (filePath != null)
        {
            var kw = LexerService.GetKeywords(filePath);
            if (kw != null)
            {
                _scintilla.SetKeywords(0, kw.Primary);
                _scintilla.SetKeywords(1, kw.Secondary);
            }
        }

        // 启用折叠（大部分 Lexilla 词法器靠 fold 属性生成折叠层级）
        _scintilla.SetProperty("fold", "1");
        _scintilla.SetProperty("fold.comment", "1");
        _scintilla.SetProperty("fold.compact", "1");
        _scintilla.SetProperty("fold.html", "1");
        _scintilla.SetProperty("fold.hypertext.comment", "1");
    }

    // ==================== 光标位置 ====================

    public int CurrentLine => _scintilla.CurrentLine;

    public int CurrentColumn => _scintilla.CurrentPosition - _scintilla.Lines[_scintilla.CurrentLine].Position;

    public int LineCount => _scintilla.Lines.Count;

    public int TextLength => _scintilla.TextLength;

    public string SelectedText => _scintilla.SelectedText;

    public int SelectionLength => Math.Abs(_scintilla.SelectionEnd - _scintilla.SelectionStart);

    public void FocusEditor()
    {
        _scintilla.Focus();
    }

    public void GoToLine(int line)
    {
        if (line < 0) line = 0;
        if (line >= _scintilla.Lines.Count) line = _scintilla.Lines.Count - 1;
        _scintilla.Lines[line].Goto();
        _scintilla.Lines[line].EnsureVisible();
        _scintilla.ScrollCaret();
    }

    /// <summary>选中某行内以「字符」为单位的区间；Scintilla 位置是 UTF-8 字节偏移，需换算</summary>
    public void SelectInLine(int lineIndex, int charColumn, int charLength)
    {
        if (lineIndex < 0 || lineIndex >= _scintilla.Lines.Count) return;
        string body = LineBody(lineIndex);

        int column = Math.Clamp(charColumn, 0, body.Length);
        int length = Math.Clamp(charLength, 0, body.Length - column);
        int start = LineStartPos(lineIndex) + System.Text.Encoding.UTF8.GetByteCount(body[..column]);
        int end = start + System.Text.Encoding.UTF8.GetByteCount(body.Substring(column, length));

        // SetSelection 的 caret 参数已经是 end，再调 GotoPosition 会把选区压扁
        _scintilla.SetSelection(start, end);
        _scintilla.ScrollCaret();
    }

    // ==================== 书签 ====================

    public void AddBookmark(int line)
    {
        if (line >= 0 && line < _scintilla.Lines.Count)
            _scintilla.Lines[line].MarkerAdd(BookmarkMarkerIndex);
    }

    public void RemoveBookmark(int line)
    {
        if (line >= 0 && line < _scintilla.Lines.Count)
            _scintilla.Lines[line].MarkerDelete(BookmarkMarkerIndex);
    }

    public void ToggleBookmark(int line)
    {
        if (line < 0 || line >= _scintilla.Lines.Count) return;
        var l = _scintilla.Lines[line];
        if ((l.MarkerGet() & BookmarkMarkerMask) != 0)
            l.MarkerDelete(BookmarkMarkerIndex);
        else
            l.MarkerAdd(BookmarkMarkerIndex);
    }

    public bool HasBookmark(int line)
    {
        if (line < 0 || line >= _scintilla.Lines.Count) return false;
        return (_scintilla.Lines[line].MarkerGet() & BookmarkMarkerMask) != 0;
    }

    public List<int> GetBookmarks()
    {
        var result = new List<int>();
        for (int i = 0; i < _scintilla.Lines.Count; i++)
        {
            if ((_scintilla.Lines[i].MarkerGet() & BookmarkMarkerMask) != 0)
                result.Add(i);
        }
        return result;
    }

    /// <summary>
    /// 查找下一个书签行（从 fromLine+1 开始，含环绕）
    /// </summary>
    public int? NextBookmark(int fromLine)
    {
        int count = _scintilla.Lines.Count;
        for (int i = fromLine + 1; i < count; i++)
        {
            if (HasBookmark(i)) return i;
        }
        for (int i = 0; i <= fromLine && i < count; i++)
        {
            if (HasBookmark(i)) return i;
        }
        return null;
    }

    /// <summary>
    /// 查找上一个书签行（从 fromLine-1 开始，含环绕）
    /// </summary>
    public int? PreviousBookmark(int fromLine)
    {
        int count = _scintilla.Lines.Count;
        for (int i = fromLine - 1; i >= 0; i--)
        {
            if (HasBookmark(i)) return i;
        }
        for (int i = count - 1; i >= fromLine && i >= 0; i--)
        {
            if (HasBookmark(i)) return i;
        }
        return null;
    }

    public void ClearBookmarks()
    {
        _scintilla.Markers[BookmarkMarkerIndex].DeleteAll();
    }

    // ==================== 折叠 ====================

    public void CollapseAll()
    {
        _scintilla.FoldAll(FoldAction.Contract);
    }

    public void ExpandAll()
    {
        _scintilla.FoldAll(FoldAction.Expand);
    }

    public void FoldCurrentLine()
    {
        _scintilla.Lines[_scintilla.CurrentLine].ToggleFold();
    }

    // ==================== 查找替换 ====================

    private static SearchFlags BuildFlags(bool matchCase, bool matchWord, bool regex)
    {
        var flags = SearchFlags.None;
        if (matchCase) flags |= SearchFlags.MatchCase;
        if (matchWord) flags |= SearchFlags.WholeWord;
        if (regex) flags |= SearchFlags.Regex;
        return flags;
    }

    public int CountMatches(string text, bool matchCase, bool matchWord, bool regex)
    {
        if (string.IsNullOrEmpty(text)) return 0;

        int count = 0;
        _scintilla.SearchFlags = BuildFlags(matchCase, matchWord, regex);
        _scintilla.TargetStart = 0;
        _scintilla.TargetEnd = _scintilla.TextLength;

        while (_scintilla.SearchInTarget(text) >= 0)
        {
            count++;
            int newStart = _scintilla.TargetEnd;
            if (newStart >= _scintilla.TextLength) break;
            _scintilla.TargetStart = newStart;
            _scintilla.TargetEnd = _scintilla.TextLength;
        }
        return count;
    }

    /// <summary>
    /// 在 [startPos, endPos) 内查找，返回匹配区间的字节偏移。
    /// Scintilla 文档内部恒为 UTF-8，位置是字节偏移，所以必须用 TargetEnd 而不是
    /// 「起点 + 字符串长度」，否则含中文的匹配会选错范围。
    /// </summary>
    private (int Start, int End)? FindRange(string text, bool matchCase, bool matchWord, bool regex, int startPos, int endPos)
    {
        if (string.IsNullOrEmpty(text)) return null;

        _scintilla.SearchFlags = BuildFlags(matchCase, matchWord, regex);
        _scintilla.TargetStart = startPos;
        _scintilla.TargetEnd = endPos;
        int pos = _scintilla.SearchInTarget(text);
        return pos >= 0 ? (pos, _scintilla.TargetEnd) : null;
    }

    public int? FindNext(string text, bool matchCase, bool matchWord, bool regex)
    {
        var range = FindRange(text, matchCase, matchWord, regex, _scintilla.SelectionEnd, _scintilla.TextLength)
                    ?? FindRange(text, matchCase, matchWord, regex, 0, _scintilla.TextLength);
        if (range == null) return null;

        var (start, end) = range.Value;
        SelectRange(start, end);
        return start;
    }

    public int? FindPrevious(string text, bool matchCase, bool matchWord, bool regex)
    {
        if (string.IsNullOrEmpty(text)) return null;

        // 向前搜索 = 在 0..SelectionStart 内找最后一个匹配
        int selStart = _scintilla.SelectionStart;
        var best = LastInRange(text, matchCase, matchWord, regex, 0, selStart);

        // 环绕：退而取后半段的最后一个匹配
        best ??= LastInRange(text, matchCase, matchWord, regex, selStart, _scintilla.TextLength);

        if (best == null) return null;

        var (start, end) = best.Value;
        SelectRange(start, end);
        return start;
    }

    private (int Start, int End)? LastInRange(string text, bool matchCase, bool matchWord, bool regex, int startPos, int endPos)
    {
        (int Start, int End)? best = null;
        int searchPos = startPos;

        // 每轮都从上一处匹配的下一个字节继续，总扫描量仍是线性
        while (searchPos < endPos)
        {
            var found = FindRange(text, matchCase, matchWord, regex, searchPos, endPos);
            if (found == null) break;

            best = found;
            searchPos = found.Value.End > found.Value.Start ? found.Value.End : found.Value.Start + 1;
        }
        return best;
    }

    private void SelectRange(int start, int end)
    {
        _scintilla.SetSelection(start, end);
        _scintilla.ScrollCaret();
    }

    public bool ReplaceCurrent(string findText, string replaceText, bool matchCase, bool matchWord, bool regex)
    {
        if (string.IsNullOrEmpty(findText)) return false;

        _scintilla.SearchFlags = BuildFlags(matchCase, matchWord, regex);
        _scintilla.TargetStart = _scintilla.SelectionStart;
        _scintilla.TargetEnd = _scintilla.SelectionEnd;

        int pos = _scintilla.SearchInTarget(findText);
        if (pos < 0) return false;

        if (regex)
            _scintilla.ReplaceTargetRe(replaceText);
        else
            _scintilla.ReplaceTarget(replaceText);
        return true;
    }

    public int ReplaceAll(string findText, string replaceText, bool matchCase, bool matchWord, bool regex)
    {
        if (string.IsNullOrEmpty(findText)) return 0;

        int count = 0;
        _scintilla.BeginUndoAction();
        try
        {
            _scintilla.SearchFlags = BuildFlags(matchCase, matchWord, regex);
            _scintilla.TargetStart = 0;
            _scintilla.TargetEnd = _scintilla.TextLength;

            while (_scintilla.SearchInTarget(findText) >= 0)
            {
                if (regex)
                    _scintilla.ReplaceTargetRe(replaceText);
                else
                    _scintilla.ReplaceTarget(replaceText);
                count++;

                int newStart = _scintilla.TargetEnd;
                if (newStart >= _scintilla.TextLength) break;
                _scintilla.TargetStart = newStart;
                _scintilla.TargetEnd = _scintilla.TextLength;
            }
        }
        finally
        {
            _scintilla.EndUndoAction();
        }
        return count;
    }

    // ==================== 剪贴板安全包装 ====================

    /// <summary>复制（剪贴板异常时静默失败，避免崩溃弹窗）</summary>
    public void SafeCopy()
    {
        try { _scintilla.Copy(); } catch { }
    }

    /// <summary>剪切</summary>
    public void SafeCut()
    {
        try { _scintilla.Cut(); } catch { }
    }

    /// <summary>粘贴</summary>
    public void SafePaste()
    {
        try { _scintilla.Paste(); } catch { }
    }

    // ==================== 大小写 / 注释 ====================

    public void UpperCaseSelection()
    {
        string text = _scintilla.SelectedText;
        if (string.IsNullOrEmpty(text)) return;
        _scintilla.TargetStart = _scintilla.SelectionStart;
        _scintilla.TargetEnd = _scintilla.SelectionEnd;
        _scintilla.ReplaceTarget(text.ToUpperInvariant());
    }

    public void LowerCaseSelection()
    {
        string text = _scintilla.SelectedText;
        if (string.IsNullOrEmpty(text)) return;
        _scintilla.TargetStart = _scintilla.SelectionStart;
        _scintilla.TargetEnd = _scintilla.SelectionEnd;
        _scintilla.ReplaceTarget(text.ToLowerInvariant());
    }

    /// <summary>
    /// 切换当前行注释
    /// </summary>
    public void ToggleComment(string commentPrefix)
    {
        if (string.IsNullOrEmpty(commentPrefix)) return;

        int line = CurrentLine;
        var ln = _scintilla.Lines[line];
        string text = ln.Text ?? string.Empty;

        if (text.TrimStart().StartsWith(commentPrefix, StringComparison.Ordinal))
        {
            // 取消注释
            int idx = text.IndexOf(commentPrefix, StringComparison.Ordinal);
            string newText = text.Remove(idx, commentPrefix.Length);
            if (newText.StartsWith(" ", StringComparison.Ordinal))
                newText = newText.Substring(1);

            _scintilla.TargetStart = ln.Position;
            _scintilla.TargetEnd = ln.Position + ln.Length;
            _scintilla.ReplaceTarget(newText);
        }
        else
        {
            // 加注释
            int indent = text.Length - text.TrimStart().Length;
            _scintilla.InsertText(ln.Position + indent, commentPrefix + " ");
        }
    }

    // ==================== 缩放 ====================

    public event EventHandler<int>? ZoomChanged;

    public int ZoomLevel
    {
        get => _scintilla.Zoom;
        set
        {
            int v = Math.Clamp(value, -10, 30);
            _scintilla.Zoom = v;
            ZoomChanged?.Invoke(this, v);
        }
    }

    public void ZoomIn() => ZoomLevel += 1;

    public void ZoomOut() => ZoomLevel -= 1;

    public void ResetZoom() => ZoomLevel = 0;

    // ==================== 显示选项 ====================

    public bool ShowWhitespace
    {
        get => _scintilla.ViewWhitespace != WhitespaceMode.Invisible;
        set => _scintilla.ViewWhitespace = value ? WhitespaceMode.VisibleAlways : WhitespaceMode.Invisible;
    }

    public bool ShowEol
    {
        get => _scintilla.ViewEol;
        set => _scintilla.ViewEol = value;
    }

    public bool WordWrap
    {
        get => _scintilla.WrapMode != ScintillaNET.WrapMode.None;
        set => _scintilla.WrapMode = value ? ScintillaNET.WrapMode.Word : ScintillaNET.WrapMode.None;
    }

    /// <summary>当前行尾符名称</summary>
    public string EolName => _scintilla.EolMode switch
    {
        Eol.CrLf => "Windows(CR LF)",
        Eol.Cr => "Mac(CR)",
        _ => "Unix(LF)",
    };

    public bool ShowIndentGuides
    {
        get => _scintilla.IndentationGuides != IndentView.None;
        set => _scintilla.IndentationGuides = value ? IndentView.LookBoth : IndentView.None;
    }

    // ==================== 字体 ====================

    public void SetFont(string family, double size)
    {
        var f = new System.Drawing.Font(family, (float)size);
        _scintilla.Styles[ScintillaNET.Style.Default].Font = family;
        _scintilla.Styles[ScintillaNET.Style.Default].Size = (int)Math.Round(size);
        _scintilla.Font = f;
    }

    // ==================== 查找高亮 ====================

    /// <summary>高亮所有匹配项</summary>
    public int HighlightAllMatches(string text, bool matchCase, bool matchWord, bool regex)
    {
        _scintilla.IndicatorCurrent = FindIndicatorIndex;
        _scintilla.IndicatorClearRange(0, _scintilla.TextLength);

        if (string.IsNullOrEmpty(text)) return 0;

        int count = 0;
        _scintilla.SearchFlags = BuildFlags(matchCase, matchWord, regex);
        _scintilla.TargetStart = 0;
        _scintilla.TargetEnd = _scintilla.TextLength;

        while (_scintilla.SearchInTarget(text) >= 0)
        {
            int s = _scintilla.TargetStart;
            int e = _scintilla.TargetEnd;
            if (e <= s) break;
            _scintilla.IndicatorFillRange(s, e - s);
            count++;
            if (e >= _scintilla.TextLength) break;
            _scintilla.TargetStart = e;
            _scintilla.TargetEnd = _scintilla.TextLength;
        }
        return count;
    }

    /// <summary>清除查找高亮</summary>
    public void ClearFindHighlight()
    {
        _scintilla.IndicatorCurrent = FindIndicatorIndex;
        _scintilla.IndicatorClearRange(0, _scintilla.TextLength);
    }
}
