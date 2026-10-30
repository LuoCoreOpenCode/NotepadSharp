using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using NotepadSharp.Services;
using UserControl = System.Windows.Controls.UserControl;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using Clipboard = System.Windows.Clipboard;

namespace NotepadSharp.Controls;

/// <summary>一次命中：行列都是 1 基（面板直接显示），内部跳转时再减一</summary>
/// <param name="Term">触发这次命中的关键词原文；正则模式下就是模式串本身</param>
/// <param name="Line">命中所在行号，1 基，与面板显示的「行 N」一致</param>
/// <param name="Column">命中在该行内的列号，1 基（由 0 基字符偏移加一得到）</param>
/// <param name="Length">匹配文本长度；正则的零宽匹配为 0</param>
/// <param name="Preview">命中行的可读片段：制表符换成空格、带上前后上下文、超长截断</param>
public sealed record SearchHit(string Term, int Line, int Column, int Length, string Preview)
{
    /// <summary>跨打开文件查找时标注命中来自哪个标签页，单文件查找留空</summary>
    public string Source { get; init; } = string.Empty;

    // ListView 容器项的 UIA 名称取自 ToString，默认的记录格式对读屏不友好
    /// <summary>
    /// 读屏/工具提示用的可读一行：有来源时前置「文件名 · 」，
    /// 其余部分与树上叶子节点的显示文本同一口径，方便对得上。
    /// </summary>
    /// <returns>形如「a.txt · 行 12, 列 5: foo → 整行片段」的单行文本</returns>
    public override string ToString() => (Source.Length == 0 ? string.Empty : Source + " · ")
        + $"行 {Line}, 列 {Column}: {Term} → {Preview}";
}

/// <summary>
/// 当前文件内的「查找全部 / 批量查找」服务：对编辑器缓冲区取命中，不碰磁盘，
/// 所以未保存的改动也能查，替换仍走各自的编辑器命令。
/// 匹配是逐行做的，这样结果树里的行号/列号一眼可读，跳转时换算也简单。
/// </summary>
public static class InFileSearch
{
    /// <summary>单次收集的命中硬上限：到数就收摊，防止「查找全部 a」这种把几十万处塞进树里</summary>
    public const int MaxHits = 5000;

    /// <summary>ND2 在 2000 与 4000 处各问一次是否继续（findwin.cpp:1656），这里用同样的门槛</summary>
    public const int AskAtFirst = 2000;
    /// <summary>第二道门槛：总数再跨过 4000 时问第二次，两道各只问一次（Ask 里用 askTimes 记账）</summary>
    public const int AskAtSecond = 4000;

    /// <summary>预览片段的最大字符数，超出截断加省略号，避免一行几万字符把面板撑爆</summary>
    private const int PreviewLength = 160;

    /// <summary>continueAsk(已命中数, 文件数) 返回 false 就停止收集</summary>
    /// <param name="content">编辑器缓冲区全文（GetContent 的结果），不是磁盘上的内容</param>
    /// <param name="conditions">关键词条件列表，批量查找时每个关键词一条；空条件与重复条件会被剔除</param>
    /// <param name="continueAsk">跨过 2000/4000 两道门槛时的询问回调，可空；返回 false 表示用户要求停止。
    /// 实参顺序是（命中文件数, 命中处数），与 AskContinue 一致；本服务固定只扫一个文件，前者恒传 1</param>
    /// <param name="sourceLabel">写入 SearchHit.Source 的来源标签：单文件查找留空，跨打开文件查找传标签页名</param>
    /// <returns>先按条件顺序、再按行序排列的命中列表，总条数不超过 MaxHits；被用户中止时是截止当下的部分结果</returns>
    public static List<SearchHit> FindAll(string content, IReadOnlyList<SearchCondition> conditions,
                                          Func<int, int, bool>? continueAsk = null, string sourceLabel = "")
    {
        var result = new List<SearchHit>();
        // 空查找词跳过；Distinct 按记录值相等去重，同名的两个关键词不会重复出结果
        var clean = conditions.Where(c => !c.IsEmpty).Distinct().ToList();
        if (clean.Count == 0 || content.Length == 0) return result;

        // 先把 CRLF/CR 统一折成 LF 再切行：行内匹配不涉及换行符，这样行列号与编辑器显示一致
        string[] lines = content.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        int askTimes = 0;   // 已经问过的次数，保证 2000/4000 两道门槛各只问一次
        bool aborted = false;  // 用户点了「不继续」，所有循环的条件里都带 !aborted

        foreach (SearchCondition condition in clean)
        {
            // EffectiveText 就是转义口径的分水岭：普通模式原文、扩展模式已还原 \n \t \xNN、正则模式原样当模式串
            string term = condition.EffectiveText;
            Regex? regex = null;
            if (condition.Mode == SearchMode.Regex)
            {
                // 模式串非法时不整轮失败：像 ND2 那样把这条关键词标成「正则无效」继续查后面的
                try { regex = new Regex(term, condition.ToRegexOptions()); }
                catch (ArgumentException)
                {
                    result.Add(new SearchHit(term + "（正则无效）", 0, 0, 0, string.Empty) { Source = sourceLabel });
                    continue;
                }
            }

            // 行外层循环：每行只扫一次，命中数触顶或用户喊停就整体退出
            for (int i = 0; i < lines.Length && result.Count < MaxHits && !aborted; i++)
            {
                string line = lines[i];
                if (line.Length == 0) continue;

                if (regex != null)
                {
                    // 正则分支：NextMatch 至少前进一个字符，零宽匹配不会原地打转
                    for (Match m = regex.Match(line); m.Success && result.Count < MaxHits; m = m.NextMatch())
                    {
                        // 零长匹配照样记一条，文字与 ND2 的 "find-regex-zero-length-match" 一致
                        string preview = m.Length == 0 ? ZeroLengthMatchLabel : Preview(line, m.Index, m.Length);
                        result.Add(new SearchHit(term, i + 1, m.Index + 1, m.Length, preview) { Source = sourceLabel });
                        // Ask 返回 true 表示用户拒绝继续，本行剩余匹配直接放弃
                        if (Ask(ref aborted, ref askTimes, result.Count, continueAsk)) break;
                    }
                    continue;
                }

                // 普通/扩展分支：只有大小写影响比较方式，转义已经在 EffectiveText 里处理完了
                var comparison = condition.MatchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
                int offset = 0;
                while (result.Count < MaxHits && !aborted)
                {
                    int idx = line.IndexOf(term, offset, comparison);
                    if (idx < 0) break;
                    // 全字匹配：邻居是字母或数字就跳过这一处，只把游标前进一格继续找（不能整段跳过）
                    if (condition.WholeWord && !IsWholeWord(line, idx, term.Length))
                    {
                        offset = idx + 1;
                        continue;
                    }
                    result.Add(new SearchHit(term, i + 1, idx + 1, term.Length, Preview(line, idx, term.Length)) { Source = sourceLabel });
                    // 命中按不重叠处理：游标直接跳到本次匹配末尾之后（只有全词不成立时才逐格前进）
                    offset = idx + term.Length;
                    _ = Ask(ref aborted, ref askTimes, result.Count, continueAsk);
                }
            }
            if (aborted) break;
        }
        return result;
    }

    /// <summary>正则零宽匹配在结果列表里的占位文字，与 ND2 的 "find-regex-zero-length-match" 显示等价</summary>
    public const string ZeroLengthMatchLabel = "正则查找零长匹配";

    /// <summary>模仿 ND2：只在跨过 2000、4000 两道门槛时各问一次</summary>
    /// <param name="aborted">引用传入的「用户已拒绝继续」标志，拒绝时在这里置真，外层循环条件随即退出</param>
    /// <param name="askTimes">引用传入的已询问次数，用来保证每道门槛只问一次</param>
    /// <param name="found">当前已累计的命中处数，与两道门槛比较</param>
    /// <param name="continueAsk">询问回调，为 null 表示不需要问（宿主自己数门槛时走这条）</param>
    /// <returns>true 表示用户拒绝继续、调用方应立即停止本行/本轮收集；false 表示可以继续</returns>
    private static bool Ask(ref bool aborted, ref int askTimes, int found, Func<int, int, bool>? continueAsk)
    {
        if (continueAsk == null) return false;
        // 只认「刚好跨过」：askTimes 记的是已经问过的档数，第一档问完才不会重复问
        bool crossFirst = askTimes == 0 && found > AskAtFirst;
        bool crossSecond = askTimes == 1 && found > AskAtSecond;
        if (!crossFirst && !crossSecond) return false;
        askTimes++;
        // 本服务只扫单个文件，所以回调的第一个参数（命中文件数）固定报 1
        if (!continueAsk(1, found)) { aborted = true; return true; }
        return false;
    }

    /// <summary>
    /// 全词判定：只看命中位置左右两个邻居字符，只要有一个是字母或数字就不算独立单词；
    /// 行首/行尾越界时短路成「没有邻居」，天然算作单词边界。
    /// 注意口径差异：这里的词字符只有字母和数字，目录扫描那侧的 IsWordChar 还多算了下划线。
    /// </summary>
    /// <param name="line">当前行文本</param>
    /// <param name="start">命中在本行的起始下标，0 基</param>
    /// <param name="length">命中长度</param>
    /// <returns>左右邻居都不构成单词延续时为 true</returns>
    private static bool IsWholeWord(string line, int start, int length)
    {
        // 两个局部函数各自负责一侧：贴边（越界）时短路成 false，即行首/行尾视为天然边界
        bool BeforeIsWord() => start > 0 && char.IsLetterOrDigit(line[start - 1]);
        bool AfterIsWord() => start + length < line.Length && char.IsLetterOrDigit(line[start + length]);
        return !BeforeIsWord() && !AfterIsWord();
    }

    /// <summary>
    /// 生成一行预览片段：命中位置前 40 后 60 个字符，制表符换成空格保证列号对得上，
    /// 超过 PreviewLength 再截断加省略号。
    /// </summary>
    /// <param name="line">命中所在整行</param>
    /// <param name="start">命中起始下标，0 基</param>
    /// <param name="length">命中长度</param>
    /// <returns>可直接显示的片段文本（已去掉尾部空白）</returns>
    private static string Preview(string line, int start, int length)
    {
        // Replace 是 1:1 换字符，所以下标不用重算，start/length 仍然有效
        string text = line.Replace('\t', ' ');
        int from = Math.Max(0, start - 40);
        int to = Math.Min(text.Length, start + length + 60);
        string slice = text[from..to].TrimEnd();
        return slice.Length > PreviewLength ? slice[..PreviewLength] + "…" : slice;
    }

    /// <summary>把命中列表导出成「文件:行:列: 内容」式的文本，方便复制到别处</summary>
    /// <param name="hits">要导出的命中，顺序与树上显示一致</param>
    /// <param name="prefix">每行统一前缀，宿主用来标注是哪一个文档的结果；默认空</param>
    /// <returns>每条命中一行、以平台换行结尾的纯文本</returns>
    public static string ToText(IEnumerable<SearchHit> hits, string prefix = "")
    {
        var sb = new StringBuilder();
        foreach (var h in hits)
            sb.AppendLine($"{prefix}{(h.Source.Length == 0 ? string.Empty : h.Source + " · ")}行 {h.Line}, 列 {h.Column}: [{h.Term}] {h.Preview}");
        return sb.ToString();
    }
}

/// <summary>
/// 查找结果面板：对应 ND2 的结果窗（findresultwin + findresultview）。
/// 命中按文件分组、组节点可折叠，双击跳回编辑器，右键菜单是 ND2 那一套动作。
/// </summary>
public partial class SearchResultsPanel : UserControl
{
    /// <summary>上边缘拖条能把面板压缩到的最小高度（DIP），再小就看不见结果了</summary>
    private const double MinPanelHeight = 120;
    /// <summary>允许拖到的最大高度（DIP），防止一拖就把编辑器全遮住</summary>
    private const double MaxPanelHeight = 600;

    /// <summary>双击/回车/点「跳转」时把一条命中交给宿主：换算 0 基坐标、切标签页、选中都由宿主做</summary>
    public event Action<SearchHit>? JumpRequested;
    /// <summary>「✕ 关闭」按钮：宿主清空列表、清归属映射并撤掉编辑器里的查找高亮</summary>
    public event Action? CloseRequested;

    /// <summary>面板自己留一份命中副本：右键菜单的复制、就地再查找都基于它，不回头问宿主</summary>
    private readonly List<SearchHit> _hits = new();
    /// <summary>「自动换行」菜单项的勾选状态，决定叶子节点用换行还是省略号显示</summary>
    private bool _wrap;
    /// <summary>拖动开始时的面板高度基准；0 表示当前没在拖，第一次 DragDelta 时补取</summary>
    private double _startHeight;

    /// <summary>
    /// 构造面板：恢复上次的换行偏好与面板高度。
    /// 高度只在记录值落在合法区间时才采用，否则用 220 的默认值（没拖过的记录是 0）。
    /// </summary>
    public SearchResultsPanel()
    {
        InitializeComponent();
        var settings = SettingsService.Current;
        _wrap = settings.SearchResultsWrap;
        WrapMenuItem.IsChecked = _wrap;
        if (settings.SearchResultsHeight >= MinPanelHeight)
            Height = Math.Min(settings.SearchResultsHeight, MaxPanelHeight);
        else
            Height = 220;
    }

    /// <summary>
    /// 宿主填结果入口：记下命中、重建树并显示汇总语，同时把面板显示出来。
    /// 结果条数触到 InFileSearch.MaxHits 时在汇总语里注明，避免用户以为查全了。
    /// </summary>
    /// <param name="summary">宿主给的前半句，例如「a.txt（3 个关键词）」</param>
    /// <param name="hits">本次全部命中，顺序即树上显示顺序</param>
    public void ShowResults(string summary, IReadOnlyList<SearchHit> hits)
    {
        _hits.Clear();
        _hits.AddRange(hits);
        RenderTree(selectFirst: true);

        SummaryText.Text = hits.Count == 0
            ? summary + " · 没有命中"
            : $"{summary} · {hits.Count} 处命中" + (hits.Count >= InFileSearch.MaxHits ? $"（已达上限 {InFileSearch.MaxHits}）" : string.Empty) + " · 双击跳转";
        Visibility = Visibility.Visible;
    }

    /// <summary>
    /// 清空结果：列表副本与树一起清掉，汇总语回落到默认提示。
    /// 宿主关闭面板时也走这里，所以不清 Visibility（由调用方决定收起还是留着）。
    /// </summary>
    public void ClearResults()
    {
        _hits.Clear();
        ResultTree.Items.Clear();
        SummaryText.Text = "查找结果：双击一行可跳到对应位置。";
    }

    /// <summary>按来源分组重建树；组数不超过 5 时默认展开，与目录查找一致</summary>
    /// <param name="selectFirst">true 表示重建后把第一行选中，方便接着按回车跳转</param>
    private void RenderTree(bool selectFirst)
    {
        ResultTree.Items.Clear();
        // 没有来源标签的（单文件查找）统一归到「当前文件」这一组，树上永远是两层
        var groups = _hits.GroupBy(h => h.Source.Length == 0 ? "当前文件" : h.Source).ToList();
        foreach (var group in groups)
        {
            var fileNode = new TreeViewItem
            {
                Header = $"{group.Key}（{group.Count()} 处）",
                Tag = group.Key,
            };
            foreach (var hit in group)
            {
                // 叶子用 TextBlock 而不是字符串：换行开关要能逐条控制 TextWrapping/TextTrimming
                fileNode.Items.Add(new TreeViewItem
                {
                    Header = new TextBlock
                    {
                        Text = $"行 {hit.Line}, 列 {hit.Column}: [{hit.Term}] {hit.Preview}",
                        TextWrapping = _wrap ? TextWrapping.Wrap : TextWrapping.NoWrap,
                        TextTrimming = _wrap ? TextTrimming.None : TextTrimming.CharacterEllipsis,
                    },
                    Tag = hit,
                });
            }
            ResultTree.Items.Add(fileNode);
            fileNode.IsExpanded = groups.Count <= 5;
        }

        if (selectFirst && _hits.Count > 0) SelectRow(0);
    }

    /// <summary>
    /// 把树的两层结构拍平成一个「行列表」：只取各组下的叶子，顺序与树上从上往下的一致。
    /// 就地再查找与选中行号都以此为坐标，所以折叠状态不影响定位。
    /// </summary>
    /// <returns>全部命中叶子节点，组的文件节点不计入</returns>
    private List<TreeViewItem> RowItems()
    {
        var rows = new List<TreeViewItem>();
        foreach (TreeViewItem fileNode in ResultTree.Items)
            foreach (var child in fileNode.Items.OfType<TreeViewItem>())
                rows.Add(child);
        return rows;
    }

    /// <summary>
    /// 按拍平后的下标选中某一行：先把所属文件组展开，否则选中项藏在折起状态里看不见，
    /// 最后 BringIntoView 让它滚进可视区域。
    /// </summary>
    /// <param name="index">RowItems 里的 0 基下标，越界由 Math.Clamp 拉回首尾</param>
    private void SelectRow(int index)
    {
        var rows = RowItems();
        if (rows.Count == 0) return;
        index = Math.Clamp(index, 0, rows.Count - 1);
        var row = rows[index];
        if (row.Parent is TreeViewItem parent) parent.IsExpanded = true;
        row.IsSelected = true;
        row.BringIntoView();
    }

    /// <summary>
    /// 反查当前选中项在拍平列表里的下标，供「在结果中查找」当作起点。
    /// </summary>
    /// <returns>0 基下标；没有选中行时返回 -1（调用方据此从首行/末行开始）</returns>
    private int SelectedRowIndex()
    {
        var rows = RowItems();
        int found = rows.FindIndex(r => r.IsSelected);
        return found;
    }

    /// <summary>「✕ 关闭」按钮与右键菜单「关闭」：先本地收起，再通知宿主清理归属映射与高亮</summary>
    /// <param name="sender">事件源，未使用</param>
    /// <param name="e">标准 RoutedEventArgs，未使用</param>
    private void OnClose(object sender, RoutedEventArgs e)
    {
        Visibility = Visibility.Collapsed;
        CloseRequested?.Invoke();
    }

    /// <summary>顶栏「清空」按钮与右键菜单「清空」：清结果，同时把就地查找条收起来</summary>
    /// <param name="sender">事件源，未使用</param>
    /// <param name="e">标准 RoutedEventArgs，未使用</param>
    private void OnClear(object sender, RoutedEventArgs e)
    {
        ClearResults();
        ResultFindRow.Visibility = Visibility.Collapsed;
    }

    /// <summary>「复制全部」按钮：把整张结果表按「文件 · 行 X, 列 Y: [词] 片段」写进剪切板</summary>
    /// <param name="sender">事件源按钮，未使用</param>
    /// <param name="e">标准 RoutedEventArgs，未使用</param>
    private void OnCopyAll(object sender, RoutedEventArgs e)
    {
        if (_hits.Count == 0) return;
        // 剪切板是进程级独占资源，别的程序正占着时 SetText 会抛异常；这里静默忽略，不打断用户
        try { Clipboard.SetText(InFileSearch.ToText(_hits)); }
        catch { /* 剪贴板被占用时忽略 */ }
    }

    /// <summary>ND2 copy select line：只取命中行的内容部分（去掉「行 X, 列 Y: 」前缀）</summary>
    /// <param name="sender">顶栏按钮或右键菜单项，未使用</param>
    /// <param name="e">标准 RoutedEventArgs，未使用</param>
    private void OnCopySelectedLine(object sender, RoutedEventArgs e)
    {
        // 选中文件组节点时 Tag 不是 SearchHit，视为没选中，给一句提示而不是复制错内容
        if (ResultTree.SelectedItem is not TreeViewItem { Tag: SearchHit hit })
        {
            SummaryText.Text = "先选中一行结果再复制。";
            return;
        }
        try
        {
            // 零宽正则匹配没有行内容可复制，退回复制关键词本身
            Clipboard.SetText(hit.Preview.Length == 0 ? hit.Term : hit.Preview);
            SummaryText.Text = "已复制选中行的内容。";
        }
        catch { /* 剪贴板被占用时忽略 */ }
    }

    /// <summary>右键菜单「自动换行」：切换每条结果的换行/省略号显示，并记住偏好</summary>
    /// <param name="sender">菜单项本身，未使用</param>
    /// <param name="e">标准 RoutedEventArgs，未使用</param>
    private void OnToggleWrap(object sender, RoutedEventArgs e)
    {
        _wrap = !_wrap;
        WrapMenuItem.IsChecked = _wrap;
        var settings = SettingsService.Current;
        settings.SearchResultsWrap = _wrap;
        SettingsService.Save();
        // 换行是 TextBlock 的属性，不能就地改，整棵树按新开关重建一次；
        // 重建时不选首行，免得用户刚点的菜单项把光标位置冲掉
        RenderTree(selectFirst: false);
    }

    /// <summary>右键菜单「全部折叠」：只折文件组节点，命中行本身没有子项</summary>
    /// <param name="sender">菜单项本身，未使用</param>
    /// <param name="e">标准 RoutedEventArgs，未使用</param>
    private void OnFoldAll(object sender, RoutedEventArgs e)
    {
        foreach (TreeViewItem node in ResultTree.Items) node.IsExpanded = false;
    }

    /// <summary>右键菜单「全部展开」：与折叠对称，结果多时一次性摊开</summary>
    /// <param name="sender">菜单项本身，未使用</param>
    /// <param name="e">标准 RoutedEventArgs，未使用</param>
    private void OnExpandAll(object sender, RoutedEventArgs e)
    {
        foreach (TreeViewItem node in ResultTree.Items) node.IsExpanded = true;
    }

    /// <summary>结果树双击（MouseDoubleClick）：等价于「跳转」，只看当前选中行</summary>
    /// <param name="sender">结果树，未使用</param>
    /// <param name="e">鼠标事件，未使用</param>
    private void OnResultDoubleClick(object sender, MouseButtonEventArgs e) => JumpToSelected();

    /// <summary>顶栏「跳转」按钮 Click：与双击走同一条路径，方便键盘用户</summary>
    /// <param name="sender">按钮本身，未使用</param>
    /// <param name="e">标准 RoutedEventArgs，未使用</param>
    private void OnJump(object sender, RoutedEventArgs e) => JumpToSelected();

    /// <summary>
    /// 结果树的 PreviewKeyDown：回车跳转、Ctrl+F 打开就地查找条。
    /// 两个分支都置 Handled——回车不让树再做别的动作，Ctrl+F 更要抢在窗口的「查找替换」之前。
    /// </summary>
    /// <param name="sender">结果树，未使用</param>
    /// <param name="e">按键事件；Key 只带主键（这里是 F），判断 Ctrl 要另外读 Keyboard.Modifiers 全局状态</param>
    private void OnTreeKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { JumpToSelected(); e.Handled = true; }
        else if (e.Key == Key.F && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            ShowResultFind();
            e.Handled = true;   // 不让窗口的 Ctrl+F（查找替换）再接管
        }
    }

    /// <summary>
    /// 选中项的 Tag 是一条 SearchHit 才发跳转事件：文件组节点的 Tag 是组名，模式不匹配就忽略。
    /// 行/列是 1 基，宿主收到后减一换成编辑器坐标。
    /// </summary>
    private void JumpToSelected()
    {
        if (ResultTree.SelectedItem is TreeViewItem { Tag: SearchHit hit }) JumpRequested?.Invoke(hit);
    }

    // ==================== 结果区就地再查找（ND2 TextFind） ====================

    /// <summary>顶栏「在结果中查找」按钮 Click：展开查找条</summary>
    /// <param name="sender">按钮本身，未使用</param>
    /// <param name="e">标准 RoutedEventArgs，未使用</param>
    private void OnShowResultFind(object sender, RoutedEventArgs e) => ShowResultFind();

    /// <summary>
    /// 显示就地查找条并把焦点放进输入框、全选原有内容。
    /// 菜单/按钮/快捷键三个入口都汇到这里，保证行为一致。
    /// </summary>
    private void ShowResultFind()
    {
        ResultFindRow.Visibility = Visibility.Visible;
        ResultFindBox.Focus();
        ResultFindBox.SelectAll();
    }

    /// <summary>查找条右侧「✕」按钮：收起查找条并把焦点还给结果树，方向键接着能用</summary>
    /// <param name="sender">按钮或转发自按键的查找框，未使用</param>
    /// <param name="e">路由事件，未使用（KeyEventArgs 也是 RoutedEventArgs，可直接转传）</param>
    private void OnCloseResultFind(object sender, RoutedEventArgs e)
    {
        ResultFindRow.Visibility = Visibility.Collapsed;
        ResultTree.Focus();
    }

    /// <summary>
    /// 就地查找框的 KeyDown：Esc 关闭查找条，回车找下一条。
    /// 回车只往前走，要回头点「上一个」按钮。
    /// </summary>
    /// <param name="sender">查找输入框，未使用</param>
    /// <param name="e">按键事件，处理后置 Handled 防止回车冒泡到窗口的默认按钮</param>
    private void OnResultFindKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { OnCloseResultFind(sender, e); e.Handled = true; }
        else if (e.Key == Key.Enter) { FindRow(forward: true); e.Handled = true; }
    }

    /// <summary>查找条「下一个」按钮 Click：从当前选中行向后找</summary>
    /// <param name="sender">按钮本身，未使用</param>
    /// <param name="e">标准 RoutedEventArgs，未使用</param>
    private void OnResultFindNext(object sender, RoutedEventArgs e) => FindRow(forward: true);

    /// <summary>查找条「上一个」按钮 Click：从当前选中行向前找</summary>
    /// <param name="sender">按钮本身，未使用</param>
    /// <param name="e">标准 RoutedEventArgs，未使用</param>
    private void OnResultFindPrev(object sender, RoutedEventArgs e) => FindRow(forward: false);

    /// <summary>在结果树的显示文本里找下一条/上一条包含关键字的行，找到就选中并滚动</summary>
    /// <param name="forward">true 向后（下一个），false 向前（上一个）</param>
    private void FindRow(bool forward)
    {
        string needle = ResultFindBox.Text;
        if (needle.Length == 0 || _hits.Count == 0) return;

        var rows = RowItems();
        int start = SelectedRowIndex();
        // 最多绕一整圈：step 从 1 数到行数，配合下面的取模实现首尾环绕
        for (int step = 1; step <= rows.Count; step++)
        {
            // 没有选中行时正向从首行、反向从末行起步；有选中行则以它为基准前后挪
            // 反向那支多加 2 倍行数再取模，纯粹是为了让负数下标变正，避免 C# 取模保留负号
            int index = forward
                ? (start < 0 ? step - 1 : (start + step) % rows.Count)
                : (start < 0 ? rows.Count - step : (start - step + rows.Count * 2) % rows.Count);
            if (index < 0 || index >= rows.Count) continue;
            // 匹配对象是树上那一行的可见文本（含行列号与关键词），不比原文；固定忽略大小写
            if (rows[index].Header is TextBlock tb &&
                tb.Text.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                SelectRow(index);
                SummaryText.Text = $"结果内定位到第 {index + 1} 行。";
                return;
            }
        }
        SummaryText.Text = $"结果里没有「{needle}」。";
    }

    // ==================== 面板高度持久化 ====================

    /// <summary>拖条 Thumb 的 DragStarted：记下起拖时的高度作为基准，后续位移都相对它算</summary>
    /// <param name="sender">上边缘拖条，未使用</param>
    /// <param name="e">拖起事件，未使用</param>
    private void OnResizeStart(object sender, System.Windows.Controls.Primitives.DragStartedEventArgs e)
        => _startHeight = ActualHeight;

    /// <summary>
    /// 拖条 Thumb 的 DragDelta：VerticalChange 是本次拖动的纵向增量。
    /// 面板在编辑器下方、拖条在上边缘，鼠标向上拖（增量为负）时面板要变高，所以取 _startHeight - VerticalChange，
    /// 再夹进 [MinPanelHeight, MaxPanelHeight]；开头那句是兜底：没收到 DragStarted 时基准还是 0，先补取当前高度。
    /// </summary>
    /// <param name="sender">上边缘拖条，未使用</param>
    /// <param name="e">拖动增量事件</param>
    private void OnResizeDrag(object sender, System.Windows.Controls.Primitives.DragDeltaEventArgs e)
    {
        if (_startHeight <= 0) _startHeight = ActualHeight;
        Height = Math.Clamp(_startHeight - e.VerticalChange, MinPanelHeight, MaxPanelHeight);
    }

    /// <summary>拖条 Thumb 的 DragCompleted：清掉基准并落盘，下次启动沿用上次的面板高度（ND2 findwinsize）</summary>
    /// <param name="sender">上边缘拖条，未使用</param>
    /// <param name="e">拖动结束事件，未使用</param>
    private void OnResizeDone(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e)
    {
        _startHeight = 0;
        var settings = SettingsService.Current;
        settings.SearchResultsHeight = Height;
        SettingsService.Save();
    }
}
