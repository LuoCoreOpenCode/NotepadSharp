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
public sealed record SearchHit(string Term, int Line, int Column, int Length, string Preview)
{
    /// <summary>跨打开文件查找时标注命中来自哪个标签页，单文件查找留空</summary>
    public string Source { get; init; } = string.Empty;

    // ListView 容器项的 UIA 名称取自 ToString，默认的记录格式对读屏不友好
    public override string ToString() => (Source.Length == 0 ? string.Empty : Source + " · ")
        + $"行 {Line}, 列 {Column}: {Term} → {Preview}";
}

/// <summary>
/// 当前文件内的「查找全部 / 批量查找」服务：对编辑器缓冲区取命中，不碰磁盘，
/// 所以未保存的改动也能查，替换仍走各自的编辑器命令。
/// </summary>
public static class InFileSearch
{
    public const int MaxHits = 5000;

    /// <summary>ND2 在 2000 与 4000 处各问一次是否继续（findwin.cpp:1656），这里用同样的门槛</summary>
    public const int AskAtFirst = 2000;
    public const int AskAtSecond = 4000;

    private const int PreviewLength = 160;

    /// <summary>continueAsk(已命中数, 文件数) 返回 false 就停止收集</summary>
    public static List<SearchHit> FindAll(string content, IReadOnlyList<SearchCondition> conditions,
                                          Func<int, int, bool>? continueAsk = null, string sourceLabel = "")
    {
        var result = new List<SearchHit>();
        var clean = conditions.Where(c => !c.IsEmpty).Distinct().ToList();
        if (clean.Count == 0 || content.Length == 0) return result;

        string[] lines = content.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        int askTimes = 0;
        bool aborted = false;

        foreach (SearchCondition condition in clean)
        {
            string term = condition.EffectiveText;
            Regex? regex = null;
            if (condition.Mode == SearchMode.Regex)
            {
                try { regex = new Regex(term, condition.ToRegexOptions()); }
                catch (ArgumentException)
                {
                    result.Add(new SearchHit(term + "（正则无效）", 0, 0, 0, string.Empty) { Source = sourceLabel });
                    continue;
                }
            }

            for (int i = 0; i < lines.Length && result.Count < MaxHits && !aborted; i++)
            {
                string line = lines[i];
                if (line.Length == 0) continue;

                if (regex != null)
                {
                    for (Match m = regex.Match(line); m.Success && result.Count < MaxHits; m = m.NextMatch())
                    {
                        // 零长匹配照样记一条，文字与 ND2 的 "find-regex-zero-length-match" 一致
                        string preview = m.Length == 0 ? ZeroLengthMatchLabel : Preview(line, m.Index, m.Length);
                        result.Add(new SearchHit(term, i + 1, m.Index + 1, m.Length, preview) { Source = sourceLabel });
                        if (Ask(ref aborted, ref askTimes, result.Count, continueAsk)) break;
                    }
                    continue;
                }

                var comparison = condition.MatchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
                int offset = 0;
                while (result.Count < MaxHits && !aborted)
                {
                    int idx = line.IndexOf(term, offset, comparison);
                    if (idx < 0) break;
                    if (condition.WholeWord && !IsWholeWord(line, idx, term.Length))
                    {
                        offset = idx + 1;
                        continue;
                    }
                    result.Add(new SearchHit(term, i + 1, idx + 1, term.Length, Preview(line, idx, term.Length)) { Source = sourceLabel });
                    offset = idx + term.Length;
                    _ = Ask(ref aborted, ref askTimes, result.Count, continueAsk);
                }
            }
            if (aborted) break;
        }
        return result;
    }

    public const string ZeroLengthMatchLabel = "正则查找零长匹配";

    /// <summary>模仿 ND2：只在跨过 2000、4000 两道门槛时各问一次</summary>
    private static bool Ask(ref bool aborted, ref int askTimes, int found, Func<int, int, bool>? continueAsk)
    {
        if (continueAsk == null) return false;
        bool crossFirst = askTimes == 0 && found > AskAtFirst;
        bool crossSecond = askTimes == 1 && found > AskAtSecond;
        if (!crossFirst && !crossSecond) return false;
        askTimes++;
        if (!continueAsk(1, found)) { aborted = true; return true; }
        return false;
    }

    private static bool IsWholeWord(string line, int start, int length)
    {
        bool BeforeIsWord() => start > 0 && char.IsLetterOrDigit(line[start - 1]);
        bool AfterIsWord() => start + length < line.Length && char.IsLetterOrDigit(line[start + length]);
        return !BeforeIsWord() && !AfterIsWord();
    }

    private static string Preview(string line, int start, int length)
    {
        string text = line.Replace('\t', ' ');
        int from = Math.Max(0, start - 40);
        int to = Math.Min(text.Length, start + length + 60);
        string slice = text[from..to].TrimEnd();
        return slice.Length > PreviewLength ? slice[..PreviewLength] + "…" : slice;
    }

    /// <summary>把命中列表导出成「文件:行:列: 内容」式的文本，方便复制到别处</summary>
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
    private const double MinPanelHeight = 120;
    private const double MaxPanelHeight = 600;

    public event Action<SearchHit>? JumpRequested;
    public event Action? CloseRequested;

    private readonly List<SearchHit> _hits = new();
    private bool _wrap;
    private double _startHeight;

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

    public void ClearResults()
    {
        _hits.Clear();
        ResultTree.Items.Clear();
        SummaryText.Text = "查找结果：双击一行可跳到对应位置。";
    }

    /// <summary>按来源分组重建树；组数不超过 5 时默认展开，与目录查找一致</summary>
    private void RenderTree(bool selectFirst)
    {
        ResultTree.Items.Clear();
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

    private List<TreeViewItem> RowItems()
    {
        var rows = new List<TreeViewItem>();
        foreach (TreeViewItem fileNode in ResultTree.Items)
            foreach (var child in fileNode.Items.OfType<TreeViewItem>())
                rows.Add(child);
        return rows;
    }

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

    private int SelectedRowIndex()
    {
        var rows = RowItems();
        int found = rows.FindIndex(r => r.IsSelected);
        return found;
    }

    private void OnClose(object sender, RoutedEventArgs e)
    {
        Visibility = Visibility.Collapsed;
        CloseRequested?.Invoke();
    }

    private void OnClear(object sender, RoutedEventArgs e)
    {
        ClearResults();
        ResultFindRow.Visibility = Visibility.Collapsed;
    }

    private void OnCopyAll(object sender, RoutedEventArgs e)
    {
        if (_hits.Count == 0) return;
        try { Clipboard.SetText(InFileSearch.ToText(_hits)); }
        catch { /* 剪贴板被占用时忽略 */ }
    }

    /// <summary>ND2 copy select line：只取命中行的内容部分（去掉「行 X, 列 Y: 」前缀）</summary>
    private void OnCopySelectedLine(object sender, RoutedEventArgs e)
    {
        if (ResultTree.SelectedItem is not TreeViewItem { Tag: SearchHit hit })
        {
            SummaryText.Text = "先选中一行结果再复制。";
            return;
        }
        try
        {
            Clipboard.SetText(hit.Preview.Length == 0 ? hit.Term : hit.Preview);
            SummaryText.Text = "已复制选中行的内容。";
        }
        catch { /* 剪贴板被占用时忽略 */ }
    }

    private void OnToggleWrap(object sender, RoutedEventArgs e)
    {
        _wrap = !_wrap;
        WrapMenuItem.IsChecked = _wrap;
        var settings = SettingsService.Current;
        settings.SearchResultsWrap = _wrap;
        SettingsService.Save();
        RenderTree(selectFirst: false);
    }

    private void OnFoldAll(object sender, RoutedEventArgs e)
    {
        foreach (TreeViewItem node in ResultTree.Items) node.IsExpanded = false;
    }

    private void OnExpandAll(object sender, RoutedEventArgs e)
    {
        foreach (TreeViewItem node in ResultTree.Items) node.IsExpanded = true;
    }

    private void OnResultDoubleClick(object sender, MouseButtonEventArgs e) => JumpToSelected();

    private void OnJump(object sender, RoutedEventArgs e) => JumpToSelected();

    private void OnTreeKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { JumpToSelected(); e.Handled = true; }
        else if (e.Key == Key.F && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            ShowResultFind();
            e.Handled = true;   // 不让窗口的 Ctrl+F（查找替换）再接管
        }
    }

    private void JumpToSelected()
    {
        if (ResultTree.SelectedItem is TreeViewItem { Tag: SearchHit hit }) JumpRequested?.Invoke(hit);
    }

    // ==================== 结果区就地再查找（ND2 TextFind） ====================

    private void OnShowResultFind(object sender, RoutedEventArgs e) => ShowResultFind();

    private void ShowResultFind()
    {
        ResultFindRow.Visibility = Visibility.Visible;
        ResultFindBox.Focus();
        ResultFindBox.SelectAll();
    }

    private void OnCloseResultFind(object sender, RoutedEventArgs e)
    {
        ResultFindRow.Visibility = Visibility.Collapsed;
        ResultTree.Focus();
    }

    private void OnResultFindKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { OnCloseResultFind(sender, e); e.Handled = true; }
        else if (e.Key == Key.Enter) { FindRow(forward: true); e.Handled = true; }
    }

    private void OnResultFindNext(object sender, RoutedEventArgs e) => FindRow(forward: true);

    private void OnResultFindPrev(object sender, RoutedEventArgs e) => FindRow(forward: false);

    /// <summary>在结果树的显示文本里找下一条/上一条包含关键字的行，找到就选中并滚动</summary>
    private void FindRow(bool forward)
    {
        string needle = ResultFindBox.Text;
        if (needle.Length == 0 || _hits.Count == 0) return;

        var rows = RowItems();
        int start = SelectedRowIndex();
        for (int step = 1; step <= rows.Count; step++)
        {
            int index = forward
                ? (start < 0 ? step - 1 : (start + step) % rows.Count)
                : (start < 0 ? rows.Count - step : (start - step + rows.Count * 2) % rows.Count);
            if (index < 0 || index >= rows.Count) continue;
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

    private void OnResizeStart(object sender, System.Windows.Controls.Primitives.DragStartedEventArgs e)
        => _startHeight = ActualHeight;

    private void OnResizeDrag(object sender, System.Windows.Controls.Primitives.DragDeltaEventArgs e)
    {
        if (_startHeight <= 0) _startHeight = ActualHeight;
        Height = Math.Clamp(_startHeight - e.VerticalChange, MinPanelHeight, MaxPanelHeight);
    }

    private void OnResizeDone(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e)
    {
        _startHeight = 0;
        var settings = SettingsService.Current;
        settings.SearchResultsHeight = Height;
        SettingsService.Save();
    }
}
