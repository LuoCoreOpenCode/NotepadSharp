using System.Text;
using System.Text.RegularExpressions;
using UserControl = System.Windows.Controls.UserControl;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using Clipboard = System.Windows.Clipboard;

namespace NotepadSharp.Controls;

/// <summary>一次命中：行列都是 1 基（面板直接显示），内部跳转时再减一</summary>
public sealed record SearchHit(string Term, int Line, int Column, int Length, string Preview)
{
    // ListView 容器项的 UIA 名称取自 ToString，默认的记录格式对读屏不友好
    public override string ToString() => $"行 {Line}, 列 {Column}: {Term} → {Preview}";
}

/// <summary>
/// 当前文件内的「查找全部 / 批量查找」服务：对编辑器缓冲区取命中，不碰磁盘，
/// 所以未保存的改动也能查，替换仍走各自的编辑器命令。
/// </summary>
public static class InFileSearch
{
    public const int MaxHits = 5000;
    private const int PreviewLength = 160;

    public static List<SearchHit> FindAll(string content, IEnumerable<string> terms, bool matchCase, bool useRegex)
    {
        var result = new List<SearchHit>();
        var clean = terms.Select(t => t.Trim()).Where(t => t.Length > 0).Distinct().ToList();
        if (clean.Count == 0 || content.Length == 0) return result;

        string[] lines = content.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        foreach (string term in clean)
        {
            Regex? regex = null;
            if (useRegex)
            {
                try { regex = new Regex(term, matchCase ? RegexOptions.None : RegexOptions.IgnoreCase); }
                catch { result.Add(new SearchHit(term + "（正则无效）", 0, 0, 0, string.Empty)); continue; }
            }

            for (int i = 0; i < lines.Length && result.Count < MaxHits; i++)
            {
                string line = lines[i];
                if (line.Length == 0) continue;

                if (regex != null)
                {
                    for (Match m = regex.Match(line); m.Success && result.Count < MaxHits; m = m.NextMatch())
                    {
                        if (m.Length == 0) continue;   // 零宽匹配会把整行刷屏
                        result.Add(new SearchHit(term, i + 1, m.Index + 1, m.Length, Preview(line, m.Index, m.Length)));
                    }
                    continue;
                }

                var comparison = matchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
                int offset = 0;
                while (result.Count < MaxHits)
                {
                    int idx = line.IndexOf(term, offset, comparison);
                    if (idx < 0) break;
                    result.Add(new SearchHit(term, i + 1, idx + 1, term.Length, Preview(line, idx, term.Length)));
                    offset = idx + term.Length;
                }
            }
        }
        return result;
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
            sb.AppendLine($"{prefix}行 {h.Line}, 列 {h.Column}: [{h.Term}] {h.Preview}");
        return sb.ToString();
    }
}

public partial class SearchResultsPanel : UserControl
{
    private readonly List<SearchHit> _hits = new();

    public event Action<SearchHit>? JumpRequested;
    public event Action? CloseRequested;

    public SearchResultsPanel()
    {
        InitializeComponent();
    }

    public void ShowResults(string summary, IReadOnlyList<SearchHit> hits)
    {
        _hits.Clear();
        _hits.AddRange(hits);
        ResultList.ItemsSource = null;
        ResultList.ItemsSource = _hits;
        SummaryText.Text = hits.Count == 0
            ? summary + " · 没有命中"
            : $"{summary} · {hits.Count} 处命中" + (hits.Count >= InFileSearch.MaxHits ? $"（已达上限 {InFileSearch.MaxHits}）" : string.Empty) + " · 双击跳转";
        if (_hits.Count > 0) ResultList.SelectedIndex = 0;
        Visibility = System.Windows.Visibility.Visible;
    }

    public void ClearResults()
    {
        _hits.Clear();
        ResultList.ItemsSource = null;
        SummaryText.Text = "查找结果：双击一行可跳到对应位置。";
    }

    private void OnClose(object sender, System.Windows.RoutedEventArgs e)
    {
        Visibility = System.Windows.Visibility.Collapsed;
        CloseRequested?.Invoke();
    }

    private void OnCopyAll(object sender, System.Windows.RoutedEventArgs e)
    {
        if (_hits.Count == 0) return;
        try { Clipboard.SetText(InFileSearch.ToText(_hits)); }
        catch { /* 剪贴板被占用时忽略 */ }
    }

    private void OnResultDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e) => JumpToSelected();

    private void OnJump(object sender, System.Windows.RoutedEventArgs e) => JumpToSelected();

    private void OnListKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter) { JumpToSelected(); e.Handled = true; }
    }

    private void JumpToSelected()
    {
        if (ResultList.SelectedItem is SearchHit hit) JumpRequested?.Invoke(hit);
    }
}
