using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using Button = System.Windows.Controls.Button;
using DataGrid = System.Windows.Controls.DataGrid;
using Panel = System.Windows.Controls.Panel;
using TextBox = System.Windows.Controls.TextBox;
using Application = System.Windows.Application;
using Orientation = System.Windows.Controls.Orientation;
using NotepadSharp.Controls;
using NotepadSharp.Services;

namespace NotepadSharp.Views;

/// <summary>
/// 批量查找替换（复刻 ND2 BatchFindReplace）：两个多行关键词输入区 + Keyword|Replace 表格。
/// 动作每次作用在「点击时的当前文档」上，所以窗口是非模态的，可边切标签边操作。
/// </summary>
/// <remarks>
/// 布局是纯 C# 手搭的（本窗口没有对应的 xaml）：根 Grid 四行 = 输入区 / 表格 / 按钮条 / 状态文本。
/// 数据流固定是「输入区切词 → 表格行 → 动作读表格」，表格是唯一的真相来源，
/// 因此用户在表格里手动改的词同样会被查找/替换/标记读到，无需再点刷新。
/// </remarks>
public sealed class BatchFindReplaceWindow : Window
{
    /// <summary>ND2 tranInputKeyword 的上限：切出来的关键字超过它就拒绝刷新</summary>
    /// <remarks>上限按「切完之后的条数」判定，不是按输入字符数；超限不截断，而是整批拒绝并要求用户改输入。</remarks>
    private const int MaxKeywordCount = 20000;

    /// <summary>
    /// 表格的一行数据 = 一对「关键词 -&gt; 替换词」。
    /// 只当 <see cref="DataGrid"/> 的绑定载体用，不实现变更通知：
    /// 行集合永远由「刷新」按钮整体重建（<see cref="Fresh"/>），行内文本则由表格自己双向写回这两个属性。
    /// </summary>
    /// <remarks>对应 ND2 批量查找替换窗 Keyword|Replace 两列的每行数据。</remarks>
    private sealed class Pair
    {
        /// <summary>第一列「关键词」：要在文档里查找的字面串，由查找输入区按空白符切出来</summary>
        public string Keyword { get; set; } = string.Empty;

        /// <summary>第二列「替换」：与 Keyword 同行的替换串；留空表示这一行只参与查找/标记、不参与替换</summary>
        public string Replacement { get; set; } = string.Empty;
    }

    /// <summary>左上「查找关键字」多行输入区：一段空白符分隔一个词，刷新时切词填表格第一列</summary>
    private readonly TextBox _findEdit = MakeKeywordEdit();

    /// <summary>右上「替换关键字」多行输入区：切出的词按行号填表格第二列，多出来的词会提示无对应匹配项</summary>
    private readonly TextBox _replaceEdit = MakeKeywordEdit();

    /// <summary>表格的数据源（可观察集合）：每次「刷新」整体 Clear 再重建，用户也可在表格里直接改单元格</summary>
    private readonly ObservableCollection<Pair> _rows = new();

    /// <summary>窗口底部的状态文本：所有成功/失败/提示文案都写这里，不弹对话框（对应 ND2 的状态提示区）</summary>
    private readonly TextBlock _status = new()
    {
        Margin = new Thickness(0, 6, 0, 0),
        TextWrapping = TextWrapping.Wrap,
        Foreground = Application.Current.Resources["TextSecondaryBrush"] as System.Windows.Media.Brush,
    };

    /// <summary>取当前文档编辑器，每次点击时重新解析（对应 ND2 autoAdjustCurrentEditWin）</summary>
    private readonly Func<EditorControl?> _getEditor;

    /// <summary>取本次标记用的颜色槽（1–5），与查找面板共用同一套轮转规则</summary>
    private readonly Func<int> _takeMarkSlot;

    /// <summary>
    /// 创建批量查找替换窗口（非模态，用 Show 显示）。窗口本身不持有文档，
    /// 所有动作都在点击那一刻通过 <paramref name="getEditor"/> 取「当前标签页的编辑器」，
    /// 因此可以先切标签再点按钮，操作对象自动跟着主窗口走。
    /// </summary>
    /// <param name="getEditor">取当前文档编辑器的回调；返回 null 表示当前没有打开文档</param>
    /// <param name="takeMarkSlot">取标记颜色槽（1–5）的回调，与查找面板共用同一套轮转规则</param>
    public BatchFindReplaceWindow(Func<EditorControl?> getEditor, Func<int> takeMarkSlot)
    {
        _getEditor = getEditor;
        _takeMarkSlot = takeMarkSlot;

        // ---- 窗口基础属性：固定初始尺寸 + 首次居中 + 不占任务栏（工具窗口的通用姿态） ----
        Title = "批量查找替换";
        Width = 902;
        Height = 737;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ShowInTaskbar = false;
        Background = Application.Current.Resources["PanelBackground"] as System.Windows.Media.Brush;
        // 上次关闭时存下来的位置/大小优先，覆盖上面的 CenterScreen 与默认尺寸
        RestoreGeometry();

        var text = Application.Current.Resources["TextBrush"] as System.Windows.Media.Brush;

        // ---- 根布局：4 行 Grid（0=两个输入区，1=词表表格占满剩余，2=按钮行，3=状态文本） ----
        var root = new Grid { Margin = new Thickness(3, 6, 3, 3) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // ---- 第 0 行：左右两栏输入区，中间留 6 DIP 间隙；两栏各占一半宽度 ----
        var edits = new Grid();
        edits.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        edits.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(6) });
        edits.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        // EditColumn 会把说明文本 dock 在顶部、多行输入框占其余空间，返回的即整栏容器
        edits.Children.Add(EditColumn("请输入多个查找关键字，使用空白符分隔开", _findEdit));
        var replaceColumn = EditColumn("请输入多个替换关键字，使用空白符分隔开", _replaceEdit);
        Grid.SetColumn(replaceColumn, 2);
        edits.Children.Add(replaceColumn);
        Grid.SetRow(edits, 0);
        root.Children.Add(edits);

        // ---- 第 1 行：Keyword|Replace 两列表格；禁止增删行/排序列，只允许编辑单元格文本 ----
        var table = new DataGrid
        {
            ItemsSource = _rows,
            AutoGenerateColumns = false,
            HeadersVisibility = DataGridHeadersVisibility.Column,
            CanUserAddRows = false,
            CanUserDeleteRows = false,
            CanUserReorderColumns = false,
            SelectionMode = DataGridSelectionMode.Single,
            GridLinesVisibility = DataGridGridLinesVisibility.Horizontal,
            Margin = new Thickness(0, 6, 0, 0),
            Foreground = text,
            RowHeaderWidth = 0,
        };
        // 两列都用双向绑定，用户在表格里改的词会立刻写回 _rows 里的 Pair 对象
        table.Columns.Add(Column("关键词", nameof(Pair.Keyword)));
        table.Columns.Add(Column("替换", nameof(Pair.Replacement)));
        Grid.SetRow(table, 1);
        root.Children.Add(table);

        // ---- 第 2 行：8 个动作按钮，水平居中排开，顺序与 ND2 的面板一致 ----
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            Margin = new Thickness(0, 6, 0, 0),
        };
        AddButton(buttons, "刷新", (_, _) => Fresh());
        AddButton(buttons, "交换", (_, _) => SwapFindReplace());
        AddButton(buttons, "查找", (_, _) => BatchFind());
        AddButton(buttons, "替换", (_, _) => BatchReplace());
        AddButton(buttons, "标记", (_, _) => BatchMark());
        AddButton(buttons, "清除标记", (_, _) => ClearMark());
        AddButton(buttons, "导入", (_, _) => Import());
        AddButton(buttons, "导出", (_, _) => Export());
        Grid.SetRow(buttons, 2);
        root.Children.Add(buttons);

        // ---- 第 3 行：状态提示文本（复用 _status 字段，见 Status 方法） ----
        Grid.SetRow(_status, 3);
        root.Children.Add(_status);

        Content = root;
    }

    /// <summary>
    /// 关窗时把当前位置与尺寸写回设置（对应 ND2 的 bfpos 持久化键），
    /// 下次打开直接恢复成用户上次摆的样子。
    /// </summary>
    /// <param name="e">WPF 传入的关闭事件参数，这里不用，只透传给基类</param>
    protected override void OnClosed(EventArgs e)
    {
        // 先落盘几何再交给基类收尾，避免关闭流程里出异常时丢掉这次的位置记录
        RememberGeometry();
        base.OnClosed(e);
    }

    // ==================== 表格刷新 ====================

    /// <summary>ND2 on_freshBtClick：查找区重建表格，替换区按行号填第二列</summary>
    /// <remarks>
    /// 完整口径：
    /// 1) 查找区为空 → 提示并清空表格；
    /// 2) 查找区按空白符切词（超上限也会清空表格并中止）；
    /// 3) 用切出的词重建表格，替换列先留空；
    /// 4) 替换区也切一次词，按「第 i 个词填第 i 行」对齐；多出来的替换词没有对应行，逐个提示。
    /// 注意这里只填数据，不做任何文档操作——真正的查找/替换/标记要再点对应按钮。
    /// </remarks>
    private void Fresh()
    {
        // 空输入是硬错误：直接清表并给提示，跟 ND2 一样不做「静默保留旧表」
        if (_findEdit.Text.Length == 0)
        {
            Status("请输入批量查找的关键字！");
            _rows.Clear();
            return;
        }

        // 切词失败（超过 20000 条上限）时 TrySplitKeywords 已经把提示写好并清空列表，这里只需清表
        if (!TrySplitKeywords(_findEdit.Text, out var findWords))
        {
            _rows.Clear();
            return;
        }

        // 每个查找词建一行，替换列此时留空，等下面按行号回填
        _rows.Clear();
        foreach (var word in findWords)
            _rows.Add(new Pair { Keyword = word });

        // 替换区可以是空的（表示只查找/只标记）；切不出词就直接结束，表格保持「只有关键词」的状态
        if (!TrySplitKeywords(_replaceEdit.Text, out var replaceWords)) return;

        // 按位置一一对齐：第 i 个替换词写进第 i 行；行数不够就说明替换词多了，逐个提示后丢弃
        for (int i = 0; i < replaceWords.Count; i++)
        {
            if (i < _rows.Count) _rows[i].Replacement = replaceWords[i];
            else Status($"{replaceWords[i]} 没有对应的查找匹配项！");
        }
    }

    /// <summary>
    /// 「交换」按钮：把两个输入区的原文对调，然后立刻刷新表格。
    /// 用途是把上一次的替换词当作新的查找词再跑一轮（对应 ND2 的 swap 按钮）。
    /// </summary>
    private void SwapFindReplace()
    {
        // 交换的是「原始输入文本」而不是表格行，所以关键词里的空白差异会被原样保留
        string findText = _findEdit.Text;
        _findEdit.Text = _replaceEdit.Text;
        _replaceEdit.Text = findText;
        Fresh();
    }

    /// <summary>
    /// ND2 tranInputKeyword：先把所有空白字符换成空格再按空格切，
    /// 条数超过上限直接拒绝，空项丢弃。
    /// C# 侧等价写法：给 <c>string.Split</c> 传 null 分隔符 + <c>RemoveEmptyEntries</c>，
    /// 即按「所有 Unicode 空白字符」（空格/制表/换行/全角空格等）切，且自动丢掉空项。
    /// </summary>
    /// <param name="input">输入区的原始多行文本</param>
    /// <param name="words">切出来的词表：成功时为非空列表；超过上限时被清空</param>
    /// <returns>
    /// true 表示拿到至少一个词；false 表示一个词都没切出来（输入全是空白），
    /// 或者条数超过 <see cref="MaxKeywordCount"/> 上限（两种情况都已写状态提示）。
    /// </returns>
    private bool TrySplitKeywords(string input, out List<string> words)
    {
        words = input
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .ToList();

        // 上限校验优先于「有没有词」：超限直接清空并拒绝，调用方据此清表
        if (words.Count > MaxKeywordCount)
        {
            Status("最多支持 20000 条关键字！");
            words.Clear();
            return false;
        }
        return words.Count > 0;
    }

    // ==================== 三个动作 ====================

    /// <summary>
    /// 「查找」按钮：批量查找只做计数，不写文档、不填查找结果面板。
    /// 与 ND2 的 findAtBack 一致（见 功能对照.md：批量查找是字面 + 区分大小写的纯计数）。
    /// </summary>
    private void BatchFind()
    {
        // 表空先拒绝；再解析当前文档，两者任一失败都已写好状态提示
        if (!EnsureTableReady()) return;
        var editor = CurrentEditor();
        if (editor == null) return;

        // 一次性取全文再逐词数出现次数，避免每词都向 Scintilla 重复拉全文
        int found = CountOccurrences(editor.GetContent(), TableKeywords());
        Status($"批量查找完成！一共发现 {found} 处。");
    }

    /// <summary>
    /// 「替换」按钮：把表格里「关键词与替换词不同」的行逐条在整篇文本上做字面替换，
    /// 最后一次性写回编辑器（撤销栈里只留一个动作）。
    /// 状态栏报的是词对数而不是出现次数，与 ND2 的汇报口径一致。
    /// </summary>
    private void BatchReplace()
    {
        if (!EnsureTableReady()) return;
        var editor = CurrentEditor();
        if (editor == null) return;

        // 只读文档不改内容：提示 + 蜂鸣后中止（对应 ND2 对只读编辑器的写操作拦截）
        if (editor.ReadOnly)
        {
            Status("只读文档不允许替换。");
            System.Media.SystemSounds.Beep.Play();
            return;
        }

        // ND2 只处理「替换词与查找词不同」的行，逐条在整篇文本里换完再一次性写回
        var pairs = new List<(string Find, string Replace)>();
        foreach (var row in _rows)
        {
            // 关键词为空（无效行）或两者相同（换了等于没换）的行都跳过
            if (row.Keyword.Length == 0 || row.Keyword == row.Replacement) continue;
            pairs.Add((row.Keyword, row.Replacement));
        }
        if (pairs.Count == 0)
        {
            Status("没有需要替换的条目。");
            return;
        }

        // 按表格行顺序串行替换：后一条作用在前一条的结果之上，所以词之间有覆盖关系
        string text = editor.GetContent();
        foreach (var (find, replace) in pairs)
            text = text.Replace(find, replace);

        editor.ReplaceWholeText(text);
        Status($"批量替换完成，一共替换 {pairs.Count} 处！");
    }

    /// <summary>
    /// 「标记」按钮：把表格里每个关键词在当前文档里全部填上同一种标记色。
    /// 一次点击只申请一个色槽（多个关键词共用它），想换色再点一次即可。
    /// </summary>
    private void BatchMark()
    {
        if (!EnsureTableReady()) return;
        var editor = CurrentEditor();
        if (editor == null) return;
        if (editor.ReadOnly)
        {
            Status("只读文档不允许标记。");
            System.Media.SystemSounds.Beep.Play();
            return;
        }

        // 色槽在循环外取一次，保证本轮所有关键词落在同一档颜色上
        int slot = _takeMarkSlot();
        int marked = 0;
        foreach (var keyword in TableKeywords())
            // 固定按「普通模式 + 区分大小写 + 非整词」标记，与批量查找的字面口径保持同一套规则
            marked += editor.MarkAll(new SearchCondition(keyword, SearchMode.Normal, true, false), slot);
        Status($"批量标记完成，一共标记 {marked} 处！");
    }

    /// <summary>
    /// 「清除标记」按钮：清掉当前文档的全部标记色（不动书签），与查找面板的「清除所有高亮」同源。
    /// 即使当前没有文档也照样给提示，避免点了没反应像是坏了。
    /// </summary>
    private void ClearMark()
    {
        _getEditor()?.ClearAllMarks();
        Status("已清除全部标记。");
    }

    /// <summary>
    /// 解析「点击时的当前文档」编辑器；拿不到就写状态提示，调用方据此直接返回。
    /// </summary>
    /// <returns>当前编辑器；没有打开的文档时为 null（提示已给出）</returns>
    private EditorControl? CurrentEditor()
    {
        var editor = _getEditor();
        if (editor == null) Status("当前没有打开文档。");
        return editor;
    }

    /// <summary>
    /// 动作前置校验：表格必须有内容才允许查找/替换/标记，否则提示「请先刷新」。
    /// </summary>
    /// <returns>true 表示表格可用；false 表示表为空（提示已给出）</returns>
    private bool EnsureTableReady()
    {
        if (_rows.Count > 0) return true;
        Status("查找替换表为空，请先刷新！");
        return false;
    }

    /// <summary>
    /// 取表格里所有非空关键词（保持行顺序），供计数与标记使用。
    /// </summary>
    /// <returns>过滤掉空行后的关键词列表</returns>
    private List<string> TableKeywords()
        => _rows.Where(r => r.Keyword.Length > 0).Select(r => r.Keyword).ToList();

    /// <summary>ND2 findAtBack：整篇文档逐个关键词数出现次数，字面且区分大小写</summary>
    /// <remarks>
    /// 字面比较用 <see cref="StringComparison.Ordinal"/>，所以不做区域化折叠、也不吃通配/正则；
    /// 每命中一次就把游标推进整个关键词的长度，即统计的是「不重叠出现次数」。
    /// </remarks>
    /// <param name="text">当前文档全文</param>
    /// <param name="keywords">关键词列表；其中空串会被跳过（否则会无限命中零长位置）</param>
    /// <returns>所有关键词出现次数之和</returns>
    private static int CountOccurrences(string text, IReadOnlyList<string> keywords)
    {
        int times = 0;
        foreach (var keyword in keywords)
        {
            // 空关键词零长度会让 IndexOf 原地不动，必须跳过
            if (keyword.Length == 0) continue;
            int index = 0;
            while (true)
            {
                // 从 index 开始找下一处；找不到返回 -1 就换下一个关键词
                index = text.IndexOf(keyword, index, StringComparison.Ordinal);
                if (index < 0) break;
                times++;
                // 跳过整个已匹配串：同一处不重复计数，也不统计互相重叠的匹配
                index += keyword.Length;
            }
        }
        return times;
    }

    // ==================== 词表导入导出 ====================

    /// <summary>
    /// 「导出」按钮：把表格词表写成明文 INI 风格文件——先一组 <c>find=</c> 行、再一组
    /// <c>replace=</c> 行，靠「同序号配对」而不是嵌套结构还原，人可以直接用记事本改。
    /// 这是刻意的等价偏差（ND2 用 QSettings 的 @Variant 序列化，见 功能对照.md）。
    /// </summary>
    private void Export()
    {
        // 另存为对话框：默认文件名与 ND2 一致，只给 txt 和所有类型两项过滤
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = "另存为文件 ...",
            Filter = "文本文件 (*.txt)|*.txt|所有类型 (*.*)|*.*",
            FileName = "batchfindreplace.txt",
        };
        if (dlg.ShowDialog(this) != true) return;

        // 段头只是给人看的可读标记，导入端按 key 名识别，不校验它
        var lines = new List<string> { "[BatchFindReplace]" };
        int exported = 0;
        foreach (var row in _rows)
        {
            // 关键词为空的行整条丢掉（连同它的替换词），保证两个列表的下标能对齐
            if (row.Keyword.Length == 0) continue;
            lines.Add($"find={row.Keyword}");
            exported++;
        }
        // 第二轮才写替换列：这样 find 块和 replace 块各自连续，序号一一对应
        foreach (var row in _rows)
        {
            if (row.Keyword.Length == 0) continue;
            lines.Add($"replace={row.Replacement}");
        }

        // 表为空（或全是空行）时不产生文件，也绝不覆盖用户选中的目标
        if (exported == 0)
        {
            Status("没有内容以供导出！");
            return;
        }

        try
        {
            // 行尾统一用环境换行，且写 UTF-8 无 BOM，方便被别的工具直接读
            File.WriteAllText(dlg.FileName, string.Join(Environment.NewLine, lines) + Environment.NewLine,
                new UTF8Encoding(false));
            Status("导出配置文件成功！");
        }
        catch (Exception ex)
        {
            // 只把异常文本回显到状态栏，不弹框：批量工具里提示打断越少越好
            Status("导出失败：" + ex.Message);
        }
    }

    /// <summary>
    /// 「导入」按钮：读回 <see cref="Export"/> 写出的词表文件，按 key 名分成 find/replace 两组，
    /// 用空格拼回两个输入区后立刻走一次刷新——所以导入完的表格状态与手输一致。
    /// 解析宽容：没有等号、等号在最前的行（含段头）都跳过，不认识的其他 key 直接忽略。
    /// </summary>
    private void Import()
    {
        // 打开对话框：强制文件存在，过滤口径与导出一一对应
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "打开",
            Filter = "文本文件 (*.txt)|*.txt|所有类型 (*.*)|*.*",
            CheckFileExists = true,
        };
        if (dlg.ShowDialog(this) != true) return;

        var findWords = new List<string>();
        var replaceWords = new List<string>();
        try
        {
            // 逐行按「第一个等号」切 key/value：值里再含等号不受影响，两侧空白都去掉
            foreach (var raw in File.ReadAllLines(dlg.FileName))
            {
                int eq = raw.IndexOf('=');
                if (eq <= 0) continue;                       // 无等号或以等号开头（段头/注释）都跳过
                string key = raw.Substring(0, eq).Trim();
                string value = raw.Substring(eq + 1).Trim();
                // key 大小写不敏感，只认 find / replace 两种，其余留给以后扩展
                if (key.Equals("find", StringComparison.OrdinalIgnoreCase)) findWords.Add(value);
                else if (key.Equals("replace", StringComparison.OrdinalIgnoreCase)) replaceWords.Add(value);
            }
        }
        catch (Exception ex)
        {
            // 读盘/解码失败就中止，绝不把半截结果填进输入区，避免用户以为导入成功
            Status("导入失败：" + ex.Message);
            return;
        }

        // ND2 把列表用空格拼回输入区，然后走一次刷新
        _findEdit.Text = string.Join(" ", findWords);
        _replaceEdit.Text = string.Join(" ", replaceWords);
        Fresh();
    }

    // ==================== 窗口大小位置记忆 ====================

    /// <summary>
    /// 恢复上次关闭时的窗口位置与大小：设置里存的是 "Left,Top,Width,Height" 四个固定区域性数字。
    /// 任何一段缺失或解析失败都直接放弃（保持构造里设的默认尺寸 + 居中），不抛不提示。
    /// </summary>
    private void RestoreGeometry()
    {
        var parts = SettingsService.Current.BatchFindGeometry.Split(',');
        if (parts.Length != 4) return;
        // 用固定区域性逐段 TryParse：任何一段坏掉就整体放弃，避免只恢复到半个正确几何
        var values = new double[4];
        for (int i = 0; i < 4; i++)
        {
            if (!double.TryParse(parts[i], System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out values[i]))
                return;
        }
        // 坐标必须在改 WindowStartupLocation 之前赋值：Manual 之后 Left/Top 才真正生效
        Left = values[0];
        Top = values[1];
        Width = values[2];
        Height = values[3];
        WindowStartupLocation = WindowStartupLocation.Manual;
    }

    /// <summary>
    /// 保存当前窗口几何到设置并落盘（关闭时调用）。
    /// 用 <see cref="FormattableString.Invariant"/> 保证小数点写法固定是圆点，
    /// 不受系统「用逗号当小数点」之类区域设置影响，否则读回来会解析失败。
    /// </summary>
    private void RememberGeometry()
    {
        SettingsService.Current.BatchFindGeometry = FormattableString.Invariant($"{Left},{Top},{Width},{Height}");
        SettingsService.Save();
    }

    // ==================== 小工具 ====================

    /// <summary>
    /// 造一个「多行关键词输入区」：允许回车与制表符输入，不换行、按需出滚动条。
    /// 不换行是刻意的——横向滚动能让用户看清一整行塞了很多词时的边界，便于确认切词结果。
    /// </summary>
    /// <returns>配置好滚动与前景色的 TextBox（前景取主题的 TextBrush）</returns>
    private static TextBox MakeKeywordEdit() => new()
    {
        AcceptsReturn = true,
        AcceptsTab = true,
        TextWrapping = TextWrapping.NoWrap,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        Foreground = Application.Current.Resources["TextBrush"] as System.Windows.Media.Brush,
    };

    /// <summary>
    /// 把说明文本 + 输入框包成「上标签、下输入框」的一栏（DockPanel 顶贴标签，剩余空间给框）。
    /// </summary>
    /// <param name="label">栏顶部的灰色说明文字，告诉用户这里怎么填</param>
    /// <param name="edit">该栏使用的多行输入框（由调用方长期持有，这里只负责摆放）</param>
    /// <returns>可直接塞进布局的容器元素</returns>
    private static UIElement EditColumn(string label, TextBox edit)
    {
        var panel = new DockPanel();
        // 顶部标签：允许换行，窄窗口下不会把提示截断
        var head = new TextBlock
        {
            Text = label,
            Margin = new Thickness(0, 0, 0, 3),
            TextWrapping = TextWrapping.Wrap,
            Foreground = Application.Current.Resources["TextSecondaryBrush"] as System.Windows.Media.Brush,
        };
        DockPanel.SetDock(head, Dock.Top);
        panel.Children.Add(head);
        // 输入框是 DockPanel 的最后一个孩子，吃掉标签之外的全部剩余高度
        panel.Children.Add(edit);
        return panel;
    }

    /// <summary>
    /// 造表格的一列：表头文字 + 绑定到 <c>Pair</c> 的属性名，双向、按星号平分宽度。
    /// 双向绑定是必要的——用户在单元格里补的替换词要能写回行对象，动作才看得见。
    /// </summary>
    /// <param name="header">列头显示文字</param>
    /// <param name="property">绑定的属性名（用 nameof 取，避免手写字符串打错）</param>
    /// <returns>可直接 Add 进 DataGrid.Columns 的文本列</returns>
    private static DataGridTextColumn Column(string header, string property) => new()
    {
        Header = header,
        Binding = new System.Windows.Data.Binding(property) { Mode = System.Windows.Data.BindingMode.TwoWay },
        Width = new DataGridLength(1, DataGridLengthUnitType.Star),
    };

    /// <summary>
    /// 往按钮条里追加一个按钮：统一内边距与水平间距，点击直接转交给传入的回调。
    /// </summary>
    /// <param name="owner">承载按钮的水平 StackPanel</param>
    /// <param name="label">按钮文字</param>
    /// <param name="onClick">点击处理器（这里都是把无参动作包成 RoutedEventHandler）</param>
    private static void AddButton(System.Windows.Controls.Panel owner, string label, RoutedEventHandler onClick)
    {
        var button = new Button
        {
            Content = label,
            Padding = new Thickness(12, 3, 12, 3),
            Margin = new Thickness(3, 0, 3, 0),
        };
        button.Click += onClick;
        owner.Children.Add(button);
    }

    /// <summary>
    /// 状态栏唯一写入口：把提示文本摆到底部 <c>TextBlock</c>（不弹窗、不播声音，除非调用方自己播）。
    /// </summary>
    /// <param name="message">要显示的提示/结果文本</param>
    private void Status(string message) => _status.Text = message;
}
