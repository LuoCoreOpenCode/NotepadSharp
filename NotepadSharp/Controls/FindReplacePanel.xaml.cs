using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using NotepadSharp.Services;
using UserControl = System.Windows.Controls.UserControl;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace NotepadSharp.Controls;

/// <summary>
/// 查找替换面板：把 ND2 查找窗 Find/Replace/Mark 三个 tab 的条件压进两行，
/// 条件（大小写/全字/循环/向后/模式）与标记色都在这里，宿主只负责执行。
/// </summary>
public partial class FindReplacePanel : UserControl
{
    public event EventHandler? FindNextRequested;
    public event EventHandler? FindPreviousRequested;
    public event EventHandler? CountRequested;
    public event EventHandler? ReplaceRequested;
    public event EventHandler? ReplaceAllRequested;
    /// <summary>ND2「替换所有打开文件」：逐个文档替换，只读文档跳过</summary>
    public event EventHandler? ReplaceAllInOpenedRequested;
    /// <summary>标记全部匹配，参数为是否同时加书签</summary>
    public event EventHandler<bool>? MarkRequested;
    public event EventHandler? ClearMarkRequested;
    /// <summary>ND2 Mark tab 的 Clear All：五种标记色一起清，书签保留</summary>
    public event EventHandler? ClearAllMarksRequested;
    /// <summary>ND2「提取正则结果到剪切板」（copyReResultBt）：只有正则模式可用</summary>
    public event EventHandler? CopyRegexResultRequested;
    public event EventHandler? CloseRequested;

    private static readonly int[] MarkColors = EditorControl.MarkColors;
    private const int HistorySize = 25;

    public FindReplacePanel()
    {
        InitializeComponent();

        for (int i = 0; i < MarkColors.Length; i++)
        {
            MarkColorCombo.Items.Add(new ComboBoxItem
            {
                Content = $"颜色 {i + 1}",
                Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(0xFF, (byte)(MarkColors[i] >> 16), (byte)(MarkColors[i] >> 8), (byte)MarkColors[i])),
            });
        }

        var settings = SettingsService.Current;
        MarkColorCombo.SelectedIndex = Math.Clamp(settings.MarkColorSlot, 1, MarkColors.Length) - 1;
        MarkLoopCheckBox.IsChecked = settings.MarkColorLoop;
        RefreshHistory();
    }

    /// <summary>当前查找条件（含扩展/正则模式与转义还原）</summary>
    public SearchCondition CurrentFind
        => new(FindCombo.Text ?? string.Empty, CurrentMode, MatchCaseCheckBox.IsChecked == true, MatchWordCheckBox.IsChecked == true);

    public string CurrentReplace => ReplaceCombo.Text ?? string.Empty;
    public bool WrapAround => WrapCheckBox.IsChecked == true;
    public bool Backward => BackwardCheckBox.IsChecked == true;

    private SearchMode CurrentMode
    {
        get
        {
            if (ModeRegexRadio.IsChecked == true) return SearchMode.Regex;
            if (ModeExtendRadio.IsChecked == true) return SearchMode.Extend;
            return SearchMode.Normal;
        }
    }

    /// <summary>本次标记用的色槽；开了轮色就往下走一格并记住选择</summary>
    public int TakeMarkSlot()
    {
        int slot = Math.Clamp(MarkColorCombo.SelectedIndex + 1, 1, MarkColors.Length);
        if (MarkLoopCheckBox.IsChecked == true)
        {
            MarkColorCombo.SelectedIndex = (slot % MarkColors.Length);
        }
        var settings = SettingsService.Current;
        settings.MarkColorSlot = MarkColorCombo.SelectedIndex + 1;
        settings.MarkColorLoop = MarkLoopCheckBox.IsChecked == true;
        SettingsService.Save();
        return slot;
    }

    /// <summary>
    /// 从「查找→标记颜色」菜单直接指定色槽：只选中，不像轮色那样自动前进
    /// （ND2 是 QActionGroup 单选，选完立刻用这个色重标光标处的词）。
    /// </summary>
    public void SelectMarkColorSlot(int slot)
    {
        MarkColorCombo.SelectedIndex = Math.Clamp(slot, 1, MarkColors.Length) - 1;
        var settings = SettingsService.Current;
        settings.MarkColorSlot = MarkColorCombo.SelectedIndex + 1;
        SettingsService.Save();
    }

    public void SetFindText(string text)
    {
        if (!string.IsNullOrEmpty(text)) FindCombo.Text = text;
        FindCombo.Focus();
        if (string.IsNullOrEmpty(text)) return;
        // WPF 的可编辑 ComboBox 没有 SelectAll，要选中里面的输入框
        if (FindCombo.Template?.FindName("PART_EditableTextBox", FindCombo) is System.Windows.Controls.TextBox box)
            box.SelectAll();
    }

    public void SetMatchCount(int count)
        => MatchCountText.Text = count > 0 ? $"{count} 处匹配" : "没有匹配";

    /// <summary>把一条查找/替换内容记进历史（最新在前、去重），与 ND2 下拉行为一致</summary>
    public void Remember(string findText, string replaceText)
    {
        bool changed = false;
        var settings = SettingsService.Current;
        if (!string.IsNullOrEmpty(findText)) { Prepend(settings.FindHistory, findText); changed = true; }
        if (!string.IsNullOrEmpty(replaceText)) { Prepend(settings.ReplaceHistory, replaceText); changed = true; }
        if (changed)
        {
            SettingsService.Save();
            RefreshHistory();
        }
    }

    private static void Prepend(List<string> history, string value)
    {
        history.RemoveAll(h => string.Equals(h, value, StringComparison.Ordinal));
        history.Insert(0, value);
        while (history.Count > HistorySize) history.RemoveAt(history.Count - 1);
    }

    private void RefreshHistory()
    {
        var settings = SettingsService.Current;
        FindCombo.ItemsSource = settings.FindHistory.ToArray();
        ReplaceCombo.ItemsSource = settings.ReplaceHistory.ToArray();
    }

    private void OnFindNext(object sender, RoutedEventArgs e) => FindNextRequested?.Invoke(this, EventArgs.Empty);

    private void OnFindPrevious(object sender, RoutedEventArgs e) => FindPreviousRequested?.Invoke(this, EventArgs.Empty);

    private void OnCount(object sender, RoutedEventArgs e) => CountRequested?.Invoke(this, EventArgs.Empty);

    private void OnReplace(object sender, RoutedEventArgs e) => ReplaceRequested?.Invoke(this, EventArgs.Empty);

    private void OnReplaceAll(object sender, RoutedEventArgs e) => ReplaceAllRequested?.Invoke(this, EventArgs.Empty);

    private void OnReplaceAllInOpened(object sender, RoutedEventArgs e) => ReplaceAllInOpenedRequested?.Invoke(this, EventArgs.Empty);

    private void OnMarkAll(object sender, RoutedEventArgs e) => MarkRequested?.Invoke(this, false);

    private void OnMarkWithBookmark(object sender, RoutedEventArgs e) => MarkRequested?.Invoke(this, true);

    private void OnClearMark(object sender, RoutedEventArgs e) => ClearMarkRequested?.Invoke(this, EventArgs.Empty);

    private void OnClearAllMarks(object sender, RoutedEventArgs e) => ClearAllMarksRequested?.Invoke(this, EventArgs.Empty);

    private void OnCopyRegexResult(object sender, RoutedEventArgs e) => CopyRegexResultRequested?.Invoke(this, EventArgs.Empty);

    private void OnClose(object sender, RoutedEventArgs e) => CloseRequested?.Invoke(this, EventArgs.Empty);

    private void OnFindKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            CloseRequested?.Invoke(this, EventArgs.Empty);
            e.Handled = true;
        }
        else if (e.Key == Key.Enter)
        {
            FindNextRequested?.Invoke(this, EventArgs.Empty);
            e.Handled = true;
        }
    }
}
