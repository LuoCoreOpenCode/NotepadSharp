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

    /// <summary>正则零长匹配时显示在原地的气泡文字（ND2 的 "find-regex-zero-length-match"）</summary>
    private const string ZeroLengthMatchTip = "正则查找零长匹配";

    // 标记色使用的 indicator 起始索引与数量，编号沿用 ND2 的 SCE_UNIVERSAL_FOUND_STYLE_EXT1..EXT5
    private const int MarkIndicatorBase = 21;
    public const int MarkColorCount = 5;

    /// <summary>5 种标记色的默认值（0xRRGGBB），下标 i 对应 indicator 21+i，即 ND2 的 mark_style_(i+1)</summary>
    public static readonly int[] MarkColors = { 0x00FFFF, 0xFFFF00, 0x0080FF, 0xFF0080, 0xFF8000 };

    /// <summary>一条标记记录（对应 ND2 每个编辑器上的 m_curMarkList），F8 靠它判断要不要取消</summary>
    private sealed class MarkRecord
    {
        public required string Text { get; init; }
        public int Slot { get; set; }
    }

    private readonly List<MarkRecord> _markRecords = new();

    // 位置回退 / 前进双栈，照 ND2 的 m_prePosRecord / m_nextPosRecord：只有跨行才入栈，
    // 上限 MAX_PRE_NEXT_TIMES=30，满了丢最旧的一条；仅内存，不落配置。
    private const int MaxPreNextTimes = 30;
    private readonly List<int> _prePosRecord = new();
    private readonly List<int> _nextPosRecord = new();
    private int _curPos = 0;                    // ND2 构造函数里 m_curPos(0)，所以文档刚建好时光标在 0 不会压栈
    private int _lastTrackedLine = -1;          // 对齐 QsciScintilla 的 oldLine(-1)

    /// <summary>
    /// 当前语言的注释符号，跟着词法器一起下发（ND2 也是把这三个值挂在词法器实例上）。
    /// 初值取「纯文本」那一档：ND2 新建文档挂的就是 QsciLexerText，行注释符是 #。
    /// 全空 = 这门语言没有注释动作，<see cref="CommentOps"/> 会直接不动作。
    /// </summary>
    public LexerService.CommentSymbols CommentSymbols { get; private set; } =
        LexerService.GetCommentSymbols("null", null);

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

        // Scintilla 的句柄创建后才真正能接收样式/词法器设置，否则在控件加入可视树之前调用会被丢弃。
        // 订阅必须早于 SetupEditor：里面的样式调用可能就地触发句柄创建，晚了这个事件就再也收不到。
        _scintilla.HandleCreated += OnScintillaHandleCreated;

        SetupEditor();
        Child = _scintilla;

        // 句柄也可能在构造/对象初始化阶段就被样式类属性就地创建（那时事件还没订阅上），
        // 这里补一次同样的初始化，避免词法器、主题、拖放接收器统统漏掉
        if (_scintilla.IsHandleCreated) OnScintillaHandleCreated(_scintilla, EventArgs.Empty);
    }

    private void OnScintillaHandleCreated(object? sender, EventArgs e)
    {
        ApplyLexer();
        ApplyTheme();
        AttachFileDropSink();
    }

    private void SetupEditor()
    {
        // ===== 基本设置 =====
        // ND2 出厂是 WrapNone + 缩进参考线关（键 warp / indent 默认都 0），勾不勾都由视图菜单与配置决定
        _scintilla.WrapMode = ScintillaNET.WrapMode.None;
        _scintilla.IndentationGuides = IndentView.None;
        // ND2 在 ScintillaEditView 构造里按平台钉死换行符格式（scintillaeditview.cpp:1139-1145），
        // 紧跟着就是 setAutoIndent(true)；不设的话回车会插入单个 CR，和 ND2 不一致
        _scintilla.EolMode = Eol.CrLf;
        ApplyTabSettings();
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

        // ===== 标记色 indicator：槽位 n 用 indicator 20+n，颜色照 ND2 mark_style_1..5 的默认色 =====
        for (int i = 0; i < MarkColorCount; i++)
        {
            var mark = _scintilla.Indicators[MarkIndicatorBase + i];
            mark.Style = IndicatorStyle.RoundBox;
            mark.ForeColor = DrawingColor.FromArgb(MarkColors[i]);
            mark.Alpha = 130;
            mark.Under = false;
        }

        // ===== 右键菜单 =====
        _scintilla.ContextMenuStrip = BuildContextMenu();

        // ===== Ctrl + 滚轮缩放 =====
        _scintilla.MouseWheel += OnMouseWheel;

        // 编辑区是 Win32 子 HWND，点击不会走 WPF 输入管线，主窗口只能靠这个通知补一次抬层
        _scintilla.MouseDown += (_, e) =>
        {
            EditorMouseDown?.Invoke();
            // ND2 在 mouseDoubleClickEvent 里先判 URL 热点（:2639），命中就直接开浏览器；
            // WinForms 的第二次按下走的是同一条消息，Clicks==2 就是它
            if (e.Button == MouseButtons.Left && e.Clicks == 2) OnEditorDoubleClick();
        };

        // ===== 事件 =====
        _scintilla.MarginClick += OnMarginClick;
        _scintilla.CursorChanged += OnCursorChanged;
        _scintilla.UpdateUI += OnUpdateUI;
        _scintilla.Insert += OnTextChanged;
        _scintilla.Delete += OnTextChanged;
        _scintilla.SavePointLeft += OnSavePointLeft;
        _scintilla.SavePointReached += OnSavePointReached;
        _scintilla.KeyDown += OnScintillaKeyDown;
        _scintilla.CharAdded += OnCharAdded;
    }

    /// <summary>
    /// 把全局 Tab 设置写进本编辑器（ND2 scintillaeditview.cpp:1055-1058：
    /// SCI_SETTABWIDTH(s_tabLens) 与 setIndentationsUseTabs(!s_noUseTab)）
    /// </summary>
    public void ApplyTabSettings()
    {
        var s = SettingsService.Current;
        _scintilla.TabWidth = s.TabWidth;
        _scintilla.UseTabs = !s.ReplaceTabWithSpace;
    }

    /// <summary>编辑器内按下的快捷键（命令名），由主窗口处理</summary>
    public event EventHandler<string>? ShortcutPressed;

    // 外部文件拖放接收器：挂在 Scintilla 子窗口上，句柄重建（浮动/停靠）后要重挂
    private FileDropSink? _dropSink;

    // 括号配对高亮当前填了的格子（指示器编号 + 位置），光标挪走时只清这几个位置
    private readonly List<(int Index, int Pos)> _braceFill = new();

    /// <summary>外部文件被拖到本编辑器上并松手（主窗口据此打开文件）</summary>
    public event Action<string[]>? FilesDropped;

    private void AttachFileDropSink()
    {
        if (!_scintilla.IsHandleCreated) return;
        _dropSink?.Release();          // 句柄重建时会再次进来，先把旧接收器摘掉
        _dropSink = new FileDropSink(_scintilla);
        _dropSink.FilesDropped += paths => FilesDropped?.Invoke(paths);
    }

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
        // ND2 的三条注释快捷键（shortcutkeymgr.cpp:112-114），焦点在编辑器时由这里转给主窗口
        else if (e.Control && e.Shift && e.KeyCode == Keys.K) cmd = "cbkcom";
        else if (e.Control && e.KeyCode == Keys.Q) cmd = "linecom";
        else if (e.Control && e.KeyCode == Keys.K) cmd = "bkcom";
        else if (e.Control && (e.KeyCode == Keys.Oemplus || e.KeyCode == Keys.Add)) cmd = "zoomin";
        else if (e.Control && (e.KeyCode == Keys.OemMinus || e.KeyCode == Keys.Subtract)) cmd = "zoomout";
        else if (e.Control && (e.KeyCode == Keys.D0 || e.KeyCode == Keys.NumPad0)) cmd = "zoomreset";
        // ND2「行编辑」在 .ui 里定义的四个快捷键，焦点在编辑器时也得照样走主窗口那套（只读判断在宿主侧）
        else if (e.Control && !e.Alt && !e.Shift && e.KeyCode == Keys.D) cmd = "dupline";
        else if (e.Control && e.Shift && e.KeyCode == Keys.Up) cmd = "movelineup";
        else if (e.Control && e.Shift && e.KeyCode == Keys.Down) cmd = "movelinedown";
        else if (e.Control && e.Alt && e.Shift && e.KeyCode == Keys.Return) cmd = "blankbelow";
        else if (e.Control && e.Alt && e.KeyCode == Keys.Return) cmd = "blankabove";
        else if (e.KeyCode == Keys.F2 && e.Control) cmd = "bookmark";            // ND2：Ctrl+F2 设置/取消
        else if (e.KeyCode == Keys.F2 && e.Shift) cmd = "bookmarkprev";          // ND2：Shift+F2 上一书签
        else if (e.KeyCode == Keys.F2) cmd = "bookmarknext";                     // ND2：F2 下一书签
        else if (e.KeyCode == Keys.F3 && e.Shift) cmd = "findprev";
        else if (e.KeyCode == Keys.F3) cmd = "findnext";

        if (cmd != null)
        {
            ShortcutPressed?.Invoke(this, cmd);
            e.Handled = true;
            e.SuppressKeyPress = true;
        }
    }

    // ==================== 回车自动缩进 ====================
    // ND2 无条件 setAutoIndent(true)（scintillaeditview.cpp:1149），实现在 QScintilla：
    // 字符已经插进缓冲区之后再处理，且只在「无选区、光标不在文档开头」时生效
    // （qsciscintilla.cpp:245-284 handleCharAdded）。本仓库里没有任何词法器覆写
    // blockStart/blockEnd/blockStartKeyword，autoIndentStyle() 恒为 AiMaintain，
    // 所以所有语言走的都是 maintainIndentation 这一条路。

    private void OnCharAdded(object? sender, CharAddedEventArgs e)
    {
        int pos = _scintilla.SelectionStart;
        if (pos != _scintilla.SelectionEnd || pos == 0) return;
        MaintainIndentation(e.Char, pos);
    }

    /// <summary>照抄 QsciScintilla::maintainIndentation：沿用最近一条非空行的缩进</summary>
    private void MaintainIndentation(int ch, int pos)
    {
        if (ch != '\r' && ch != '\n') return;

        int currLine = _scintilla.LineFromPosition(pos);

        int ind = 0;
        for (int line = currLine - 1; line >= 0; line--)
        {
            if (LineContentEndPos(line) > _scintilla.Lines[line].Position)
            {
                ind = _scintilla.Lines[line].Indentation;
                break;
            }
        }

        if (ind > 0) AutoIndentLine(pos, currLine, ind);
    }

    /// <summary>照抄 QsciScintilla::autoIndentLine：改缩进后把光标按同样的位移带过去</summary>
    private void AutoIndentLine(int pos, int line, int indent)
    {
        var ln = _scintilla.Lines[line];
        int posBefore = ln.IndentPosition;
        ln.Indentation = indent;
        int posAfter = ln.IndentPosition;

        int newPos = -1;
        if (posAfter > posBefore)
        {
            newPos = pos + (posAfter - posBefore);
        }
        else if (posAfter < posBefore && pos >= posAfter)
        {
            newPos = pos >= posBefore ? pos + (posAfter - posBefore) : posAfter;
        }

        if (newPos >= 0) _scintilla.SetSelection(newPos, newPos);
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
        // ND2 contextUserDefineMenuEvent（scintillaeditview.cpp:1964-1972）追加的三条注释项，文案照 realcompare_zh.ts:7478-7489
        menu.Items.Add("添加/删除当行注释", null, (_, _) => Comment(CommentAction.AddDelLineComment));
        menu.Items.Add("区块注释", null, (_, _) => Comment(CommentAction.AddBlockComment));
        menu.Items.Add("清除区块注释", null, (_, _) => Comment(CommentAction.DelBlockComment));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("转成大写", null, (_, _) => ChangeCase(CaseMode.Upper));
        menu.Items.Add("转成小写", null, (_, _) => ChangeCase(CaseMode.Lower));
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
        TrackPosHistory();
        // ND2 的 handleUpdateUI 顺序是 cursorPosChange → addHotSpot → braceMatch（qsciscintilla.cpp:3745-3766）
        AddHotSpot();
        UpdateBraceMatch();
        CursorPositionChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// 位置历史入栈：ND2 的 cursorPosChange 只在「 caret 变了且行号也变了」时才 emit
    /// （qsciscintilla.cpp:3747-3760），所以同一行内横向移动不入栈；去重只看 m_curPos。
    /// </summary>
    private void TrackPosHistory()
    {
        int line = _scintilla.CurrentLine;
        if (line == _lastTrackedLine) return;
        _lastTrackedLine = line;

        int pos = _scintilla.CurrentPosition;
        if (pos == _curPos) return;

        _prePosRecord.Add(pos);
        if (_prePosRecord.Count >= MaxPreNextTimes) _prePosRecord.RemoveAt(0);
    }

    /// <summary>
    /// 回退一个位置（ND2 gotoPrePos）：栈里至少 2 条才动——最后一条就是「现在站的地方」；
    /// 弹顶塞进前进栈头部，再跳到新栈顶。返回 false 表示没有前一个位置了（调用方蜂鸣）。
    /// </summary>
    public bool GotoPrePos()
    {
        if (_prePosRecord.Count <= 1) return false;

        int v = _prePosRecord[^1];
        _prePosRecord.RemoveAt(_prePosRecord.Count - 1);
        _nextPosRecord.Insert(0, v);
        if (_nextPosRecord.Count >= MaxPreNextTimes) _nextPosRecord.RemoveAt(_nextPosRecord.Count - 1);

        JumpToPos(_prePosRecord[^1]);
        return true;
    }

    /// <summary>前进一个位置（ND2 gotoNextPos）：前进栈只逐条消耗，从不整体清空</summary>
    public bool GotoNextPos()
    {
        if (_nextPosRecord.Count == 0) return false;

        int pos = _nextPosRecord[0];
        _nextPosRecord.RemoveAt(0);
        _prePosRecord.Add(pos);
        JumpToPos(pos);
        return true;
    }

    // C# 里 UpdateUI 是同步回调，必须在移动光标之前先记下 _curPos，否则跳过去会被自己再压一次栈
    private void JumpToPos(int pos)
    {
        _curPos = pos;
        _scintilla.CurrentPosition = pos;       // SCI_GOTOPOS 自带把光标滚进可视区
    }

    /// <summary>
    /// 光标贴到括号边上时点亮配对的另一半（配色与匹配模式对齐参考编辑器）：
    /// 宽松匹配——光标上、前 1、前 2 个字符都算贴着；包含 () [] {} &lt;&gt;（HTML 标签也配对）。
    /// 配上的另一半垫方框底色，配不上的那只把字符画成警示色。
    /// </summary>
    private void UpdateBraceMatch()
    {
        var sci = _scintilla;
        var caret = sci.CurrentPosition;
        int bracePos = -1;

        if (caret < sci.TextLength && IsBrace((char)sci.GetCharAt(caret))) bracePos = caret;
        else if (caret > 0 && IsBrace((char)sci.GetCharAt(caret - 1))) bracePos = caret - 1;
        else if (caret > 1 && IsBrace((char)sci.GetCharAt(caret - 2))) bracePos = caret - 2;

        ClearBraceFill();

        if (bracePos < 0) return;

        var match = sci.BraceMatch(bracePos);
        var bad = match < 0;
        var other = bad ? bracePos : match;

        // 指示器只画在已着色的区域上，先把两个括号附近的着色补齐，避免刚载入的大文件里框不出来
        sci.Colorize(Math.Min(bracePos, other), Math.Abs(bracePos - other) + 1);

        var fillIndex = bad ? ThemeService.BraceBadForeIndicatorIndex : ThemeService.BraceIndicatorIndex;
        sci.Indicators[fillIndex].ForeColor =
            bad ? ThemeService.BraceBadFore : ThemeService.BraceMatchedFore;
        sci.IndicatorCurrent = fillIndex;
        sci.IndicatorFillRange(bracePos, 1);
        _braceFill.Add((fillIndex, bracePos));
        if (!bad)
        {
            sci.IndicatorFillRange(match, 1);
            _braceFill.Add((fillIndex, match));
        }
    }

    /// <summary>只清掉自己填的那两个格子，不动别的指示器</summary>
    private void ClearBraceFill()
    {
        foreach (var (index, pos) in _braceFill)
        {
            _scintilla.IndicatorCurrent = index;
            _scintilla.IndicatorClearRange(pos, 1);
        }
        _braceFill.Clear();
    }

    private static bool IsBrace(char c) => c is '(' or ')' or '[' or ']' or '{' or '}' or '<' or '>';

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

    private string? _pendingFilePath;

    /// <summary>当前词法器名称（自行记录，不依赖 Scintilla 的读回）</summary>
    public string CurrentLexerName { get; private set; } = "null";

    /// <summary>
    /// 状态栏「语法」栏要显示的语言名。ND2 显示的是当前词法器的 lexerTag（ccnotepad.cpp:7939
    /// syncCurDocLexerToMenu），手动切语法后不会被选区刷新改回去；我们这边多个语言共用一个
    /// Lexilla 词法器（cpp 同时服务 C#/Java/Go…），光看 CurrentLexerName 反推不出来，所以单独记。
    /// </summary>
    public string LanguageDisplay { get; set; } = "Text";

    /// <summary>按当前主题给编辑器上色</summary>
    public void ApplyTheme()
    {
        ThemeService.ApplyToScintilla(_scintilla, CurrentLexerName);
    }

    private void ApplyLexer()
    {
        if (!_scintilla.IsHandleCreated || _pendingFilePath == null) return;

        SetLexerByNameInternal(LexerService.GetLexerName(_pendingFilePath), _pendingFilePath);
        LanguageDisplay = LexerService.GetLanguageName(_pendingFilePath);
        ApplyTheme();
        RestyleNow();
    }

    public void SetLexerByName(string lexerName, string? keywordPath = null)
    {
        SetLexerByNameInternal(lexerName, keywordPath);
        ApplyTheme();
        RestyleNow();
    }

    /// <summary>词法器换好后强制整篇重新着色，避免「先填文本后设词法器」时留着旧样式</summary>
    private void RestyleNow()
    {
        if (_scintilla.TextLength > 0) _scintilla.Colorize(0, _scintilla.TextLength);
    }

    private void SetLexerByNameInternal(string lexerName, string? keywordPath)
    {
        if (string.IsNullOrEmpty(lexerName)) lexerName = "null";

        // 下发的名字若不在 Lexilla 目录里，Scintilla 只会「静默不装词法器」——
        // 文本照常显示但整篇没有颜色。所以装完必须读回校验，失败就老实退回纯文本。
        bool installed = TryInstallLexer(lexerName);
        if (!installed && !string.Equals(lexerName, "null", StringComparison.OrdinalIgnoreCase))
        {
            lexerName = "null";
            installed = TryInstallLexer(lexerName);
        }
        CurrentLexerName = installed ? lexerName : "null";

        // 注释符号跟着「真正生效的那个词法器」走，和 ND2 把符号挂在词法器实例上同一个口径
        CommentSymbols = LexerService.GetCommentSymbols(CurrentLexerName, keywordPath);

        // 注入关键字表（cpp 这类词法器不带 C#/JS/Go 等语言的关键字，不注入则关键字不上色）
        if (keywordPath != null)
        {
            var kw = LexerService.GetKeywords(keywordPath);
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

        // 词法器换了，缩进参考线的模式可能跟着换（Python 那一档不一样），补一次
        ApplyIndentGuide();
    }

    /// <summary>下发词法器并读回校验：以控件里真正生效的那个为准</summary>
    private bool TryInstallLexer(string lexerName)
    {
        try { _scintilla.LexerName = lexerName; }
        catch
        {
            // 部分版本/名字大小写差异下换用旧字符串属性
            try { _scintilla.LexerLanguage = lexerName; } catch { return false; }
        }

        try
        {
            string readback = _scintilla.LexerLanguage ?? string.Empty;
            return string.Equals(readback, lexerName, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
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

    /// <summary>选中某行内以「字符」为单位的区间；托管位置本来就是 UTF-16 码元，直接相加即可</summary>
    public void SelectInLine(int lineIndex, int charColumn, int charLength)
    {
        if (lineIndex < 0 || lineIndex >= _scintilla.Lines.Count) return;
        string body = LineBody(lineIndex);

        int column = Math.Clamp(charColumn, 0, body.Length);
        int length = Math.Clamp(charLength, 0, body.Length - column);
        int start = LineStartPos(lineIndex) + column;
        int end = start + length;

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

    /// <summary>上一次命中的零长匹配位置，用于下一次查找时强行前进一格（-1 表示没有）</summary>
    private int _zeroMatchPos = -1;

    /// <summary>
    /// 在 [startPos, endPos) 内查找，返回匹配区间的字节偏移。
    /// Scintilla 文档内部恒为 UTF-8，位置是字节偏移，所以必须用 TargetEnd 而不是
    /// 「起点 + 字符串长度」，否则含中文的匹配会选错范围。
    /// </summary>
    private (int Start, int End)? FindRange(SearchCondition condition, int startPos, int endPos,
                                            FindVerb verb = FindVerb.FindNext)
    {
        string needle = condition.EffectiveText;
        if (needle.Length == 0) return null;

        _scintilla.SearchFlags = condition.ToScintillaFlags(verb);
        _scintilla.TargetStart = startPos;
        _scintilla.TargetEnd = endPos;
        int pos = _scintilla.SearchInTarget(needle);
        return pos >= 0 ? (pos, _scintilla.TargetEnd) : null;
    }

    /// <summary>
    /// 查找下一处/上一处。backward 对应 ND2 的 Backward 复选框（同一个「查找下一个」按钮换方向），
    /// wrap 对应 Wrap around：关掉后走到文档尽头就不再绕回另一端。
    /// </summary>
    public bool FindNext(SearchCondition condition, bool wrap, bool backward)
    {
        if (condition.IsEmpty) return false;

        int length = _scintilla.TextLength;
        int caret = _scintilla.SelectionStart;
        int tail = _scintilla.SelectionEnd;

        // 上一次命中的是零长匹配：ND2 dealWithZeroFound 把 startpos 强行加 1，否则同一个位置会被反复找到
        bool stepOverZero = _zeroMatchPos >= 0 && tail == _zeroMatchPos;
        _zeroMatchPos = -1;
        int from = stepOverZero ? tail + 1 : tail;
        int to = Math.Max(0, stepOverZero ? caret - 1 : caret);

        (int Start, int End)? hit = backward
            ? LastInRange(condition, 0, to) ?? (wrap ? LastInRange(condition, 0, length) : null)
            : FindRange(condition, from, length) ?? (wrap ? FindRange(condition, 0, to) : null);

        if (hit == null) return false;

        // 零宽匹配（如正则 ^）会把同一处反复找到，光标前移一格保证每次按键都有进展
        if (hit.Value.Start == hit.Value.End)
        {
            _zeroMatchPos = hit.Value.Start;
            _scintilla.SetSelection(hit.Value.Start, hit.Value.Start);
            _scintilla.ScrollCaret();
            // ND2 dealWithZeroFoundShowTip：零长匹配看不见，用 calltip 在原地说明一下
            _scintilla.CallTipShow(hit.Value.Start, ZeroLengthMatchTip);
            return true;
        }

        SelectRange(hit.Value.Start, hit.Value.End);
        return true;
    }

    private (int Start, int End)? LastInRange(SearchCondition condition, int startPos, int endPos)
    {
        (int Start, int End)? best = null;
        int searchPos = startPos;

        // 每轮从上一处匹配的下一个字节继续，总扫描量仍是线性
        while (searchPos < endPos)
        {
            var found = FindRange(condition, searchPos, endPos);
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

    public int CountMatches(SearchCondition condition)
    {
        if (condition.IsEmpty) return 0;

        int count = 0;
        string needle = condition.EffectiveText;
        _scintilla.SearchFlags = condition.ToScintillaFlags(FindVerb.FindNext);
        _scintilla.TargetStart = 0;
        _scintilla.TargetEnd = _scintilla.TextLength;

        while (_scintilla.SearchInTarget(needle) >= 0)
        {
            count++;
            // 零长匹配（正则 EMPTYMATCH_ALL 会给出）不前进就会原地打转，与 ND2 dealWithZeroFound 一样加 1
            int newStart = _scintilla.TargetEnd > _scintilla.TargetStart
                ? _scintilla.TargetEnd
                : _scintilla.TargetStart + 1;
            if (newStart >= _scintilla.TextLength) break;
            _scintilla.TargetStart = newStart;
            _scintilla.TargetEnd = _scintilla.TextLength;
        }
        return count;
    }

    /// <summary>
    /// ND2「提取正则结果到剪切板」取的就是每处匹配的文本本身（targstart..targend），
    /// 零长匹配不算结果，与 addCurFindRecord 里 getResult 分支的判断一致。
    /// </summary>
    public IReadOnlyList<string> CollectMatchTexts(SearchCondition condition)
    {
        var texts = new List<string>();
        foreach (var (start, end) in FindRanges(condition))
        {
            if (end <= start) continue;
            texts.Add(_scintilla.GetTextRange(start, end - start));
        }
        return texts;
    }

    public bool ReplaceCurrent(SearchCondition find, string replaceText)
    {
        if (find.IsEmpty) return false;

        _scintilla.SearchFlags = find.ToScintillaFlags(FindVerb.ReplaceNext);
        _scintilla.TargetStart = _scintilla.SelectionStart;
        _scintilla.TargetEnd = _scintilla.SelectionEnd;

        int pos = _scintilla.SearchInTarget(find.EffectiveText);
        if (pos < 0) return false;

        string replacement = find.PrepareReplacement(replaceText);
        if (find.Mode == SearchMode.Regex)
            _scintilla.ReplaceTargetRe(replacement);
        else
            _scintilla.ReplaceTarget(replacement);
        return true;
    }

    public int ReplaceAll(SearchCondition find, string replaceText)
    {
        if (find.IsEmpty) return 0;

        int count = 0;
        string needle = find.EffectiveText;
        string replacement = find.PrepareReplacement(replaceText);
        _scintilla.BeginUndoAction();
        try
        {
            _scintilla.SearchFlags = find.ToScintillaFlags(FindVerb.ReplaceNext);
            _scintilla.TargetStart = 0;
            _scintilla.TargetEnd = _scintilla.TextLength;

            while (_scintilla.SearchInTarget(needle) >= 0)
            {
                if (find.Mode == SearchMode.Regex)
                    _scintilla.ReplaceTargetRe(replacement);
                else
                    _scintilla.ReplaceTarget(replacement);
                count++;

                // ReplaceTarget 会把 target 挪到替换结果末尾，零长匹配时再往前让一格
                int newStart = _scintilla.TargetEnd > _scintilla.TargetStart
                    ? _scintilla.TargetEnd
                    : _scintilla.TargetStart + 1;
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

    /// <summary>
    /// 一次性换掉整篇文本（对应 ND2 replaceAtBack 的 REPLACETARGET 写法）：
    /// 撤销栈里只留一个动作，且保留历史，不像 LoadContent 那样清空缓冲。
    /// </summary>
    public void ReplaceWholeText(string newText)
    {
        _scintilla.BeginUndoAction();
        try
        {
            _scintilla.TargetStart = 0;
            _scintilla.TargetEnd = _scintilla.TextLength;
            _scintilla.ReplaceTarget(newText);
        }
        finally
        {
            _scintilla.EndUndoAction();
        }
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

    /// <summary>
    /// ND2 的 blank 位域（ccnotepad.cpp:1059-1081 setEditShowBlankStatus）：
    /// 0 全关 / 1 只空格 / 2 只行尾 / 3 都开。空格走的是 WsVisible（一直可见），不是「缩进之后才显示」。
    /// </summary>
    public int BlankShow
    {
        get => (_scintilla.ViewWhitespace != WhitespaceMode.Invisible ? 1 : 0)
             | (_scintilla.ViewEol ? 2 : 0);
        set
        {
            _scintilla.ViewWhitespace = (value & 1) != 0 ? WhitespaceMode.VisibleAlways : WhitespaceMode.Invisible;
            _scintilla.ViewEol = (value & 2) != 0;
        }
    }

    /// <summary>
    /// 自动换行。ND2 用的是 WrapCharacter（SCI_SETWRAPMODE = SC_WRAP_CHAR，按字符断），
    /// 不是按单词断 —— 长串无空格的文本也会断开，这里照它。
    /// </summary>
    public bool WordWrap
    {
        get => _scintilla.WrapMode != ScintillaNET.WrapMode.None;
        set => _scintilla.WrapMode = value ? ScintillaNET.WrapMode.Char : ScintillaNET.WrapMode.None;
    }

    /// <summary>当前行尾符名称</summary>
    public string EolName => _scintilla.EolMode switch
    {
        Eol.CrLf => "Windows(CR LF)",
        Eol.Cr => "Mac(CR)",
        _ => "Unix(LF)",
    };

    private bool _indentGuide;

    /// <summary>
    /// 缩进参考线，照 ND2 ScintillaEditView::setIndentGuide（scintillaeditview.cpp:990-1001）：
    /// Python 用 LOOKFORWARD，其余语言用 LOOKBOTH，关掉是 NONE。
    /// ND2 换词法器会把这一项冲掉（ccnotepad.cpp:4313 的注释就是为这个才把缩进线放到装词法器之后），
    /// 所以我们也在装完词法器之后补一次。
    /// </summary>
    public bool ShowIndentGuides
    {
        get => _indentGuide;
        set { _indentGuide = value; ApplyIndentGuide(); }
    }

    private void ApplyIndentGuide()
    {
        if (!CurrentLexerName.Equals("python", StringComparison.OrdinalIgnoreCase))
            _scintilla.IndentationGuides = _indentGuide ? IndentView.LookBoth : IndentView.None;
        else
            _scintilla.IndentationGuides = _indentGuide ? IndentView.LookForward : IndentView.None;
    }

    // ==================== 字体 ====================

    public void SetFont(string family, double size)
    {
        var f = new System.Drawing.Font(family, (float)size);
        _scintilla.Styles[ScintillaNET.Style.Default].Font = family;
        _scintilla.Styles[ScintillaNET.Style.Default].Size = (int)Math.Round(size);
        _scintilla.Font = f;
    }

    // ==================== 查找高亮与标记 ====================

    /// <summary>高亮所有匹配项</summary>
    public int HighlightAllMatches(SearchCondition condition)
    {
        ClearFindHighlight();
        if (condition.IsEmpty) return 0;
        return FillIndicator(FindIndicatorIndex, condition);
    }

    /// <summary>清除查找高亮</summary>
    public void ClearFindHighlight() => ClearIndicator(FindIndicatorIndex);

    /// <summary>
    /// 标记全部匹配（ND2 查找窗 Mark tab）：colorSlot 取 1..5，对应五种标记色。
    /// 标记走独立 indicator，不会把「查找」高亮一起冲掉，两者可以同时看到。
    /// 命中的位置会记进 _markRecords，供 F8 的「同词再按一次取消高亮」使用。
    /// </summary>
    public int MarkAll(SearchCondition condition, int colorSlot)
    {
        if (condition.IsEmpty) return 0;
        int slot = Math.Clamp(colorSlot, 1, MarkColorCount);
        int count = FillIndicator(MarkIndicatorBase - 1 + slot, condition);
        if (count > 0)
            _markRecords.Add(new MarkRecord { Text = condition.Text, Slot = slot });
        return count;
    }

    /// <summary>标记全部匹配并给这些行加书签（ND2 的 Mark &amp; Bookmark）</summary>
    public int MarkAllWithBookmark(SearchCondition condition, int colorSlot)
    {
        int count = MarkAll(condition, colorSlot);
        _scintilla.BeginUndoAction();
        try
        {
            foreach (var (start, _) in FindRanges(condition))
            {
                _scintilla.Lines[_scintilla.LineFromPosition(start)].MarkerAdd(BookmarkMarkerIndex);
            }
        }
        finally
        {
            _scintilla.EndUndoAction();
        }
        return count;
    }

    /// <summary>
    /// 清除全部标记色并释放标记记录，对应 ND2 的 Clear All / 工具栏「清除所有高亮」(F7)。
    /// 与 ND2 一致：这里不动书签，书签由书签菜单的 ClearAll BookMark 负责。
    /// </summary>
    public void ClearAllMarks()
    {
        for (int i = 0; i < MarkColorCount; i++)
            ClearIndicator(MarkIndicatorBase + i);
        _markRecords.Clear();
    }

    /// <summary>
    /// 只清掉某个词的标记，对应 ND2 Mark tab 的 Clear Mark
    /// （clearHighlightWord(markTextBox 里的词)）。返回清掉的区间数。
    /// </summary>
    public int ClearMarkOf(string text)
    {
        if (text.Length == 0) return 0;
        int cleared = 0;
        foreach (var record in _markRecords.Where(r => string.Equals(r.Text, text, StringComparison.Ordinal)).ToList())
        {
            cleared += ClearIndicatorRanges(record.Slot, record.Text);
            _markRecords.Remove(record);
        }
        return cleared;
    }

    /// <summary>
    /// F8「单词高亮」（ND2 slot_wordHighlight）：有选区按选区，否则取光标处的词；
    /// 已经用当前色标记过就取消，用别的色标记过就换成当前色重标。
    /// </summary>
    public int ToggleWordMark(int colorSlot)
    {
        string word = SelectedText;
        if (word.Length == 0 || word.Contains('\n')) word = WordAtCaret();
        if (word.Length == 0) return 0;

        var record = _markRecords.LastOrDefault(r => string.Equals(r.Text, word, StringComparison.Ordinal));
        if (record != null)
        {
            // 只清这一条记录占的区间，别的词的标记要留着
            ClearIndicatorRanges(record.Slot, record.Text);
            _markRecords.Remove(record);
            if (record.Slot == colorSlot) return 0;
        }

        // ND2 走 markAllWord：区分大小写、不做全词匹配（中文全词匹配会漏）
        return MarkAll(new SearchCondition(word, SearchMode.Normal, true, false), colorSlot);
    }

    private int ClearIndicatorRanges(int slot, string text)
    {
        var indicator = MarkIndicatorBase - 1 + Math.Clamp(slot, 1, MarkColorCount);
        _scintilla.IndicatorCurrent = indicator;
        int cleared = 0;
        foreach (var (start, end) in FindRanges(new SearchCondition(text, SearchMode.Normal, true, false)))
        {
            if (end <= start) continue;
            _scintilla.IndicatorClearRange(start, end - start);
            cleared++;
        }
        return cleared;
    }

    /// <summary>光标处（或紧邻左侧）的词</summary>
    public string WordAtCaret()
    {
        int caret = _scintilla.CurrentPosition;
        if (caret <= 0 || caret > _scintilla.TextLength) return string.Empty;
        int start = _scintilla.WordStartPosition(caret, true);
        int end = _scintilla.WordEndPosition(caret, true);
        if (end <= start) return string.Empty;
        return _scintilla.GetTextRange(start, end - start);
    }

    private IEnumerable<(int Start, int End)> FindRanges(SearchCondition condition)
    {
        var ranges = new List<(int, int)>();
        string needle = condition.EffectiveText;
        if (needle.Length == 0) return ranges;

        _scintilla.SearchFlags = condition.ToScintillaFlags();
        _scintilla.TargetStart = 0;
        _scintilla.TargetEnd = _scintilla.TextLength;

        while (_scintilla.SearchInTarget(needle) >= 0)
        {
            int s = _scintilla.TargetStart;
            int e = _scintilla.TargetEnd;
            if (e < s) break;
            ranges.Add((s, e));
            if (e >= _scintilla.TextLength) break;
            // 零宽匹配也要前进，否则这里会死循环
            _scintilla.TargetStart = e > s ? e : s + 1;
            _scintilla.TargetEnd = _scintilla.TextLength;
        }
        return ranges;
    }

    private int FillIndicator(int indicatorIndex, SearchCondition condition)
    {
        int count = 0;
        _scintilla.IndicatorCurrent = indicatorIndex;
        foreach (var (start, end) in FindRanges(condition))
        {
            if (end <= start) continue;
            _scintilla.IndicatorFillRange(start, end - start);
            count++;
        }
        return count;
    }

    private void ClearIndicator(int indicatorIndex)
    {
        _scintilla.IndicatorCurrent = indicatorIndex;
        _scintilla.IndicatorClearRange(0, _scintilla.TextLength);
    }
}
