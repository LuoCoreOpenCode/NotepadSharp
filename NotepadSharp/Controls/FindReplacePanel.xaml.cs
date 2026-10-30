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
    // 以下事件是面板与宿主（MainWindow）之间唯一的通信方式：
    // 面板只负责收集条件、把界面动作翻译成信号，真正操作 Scintilla 缓冲区的查找/替换/标记都由宿主执行。
    // 这样同一个面板可以同时服务当前文档查找、批量查找、菜单里的「标记当前词」等多个入口。

    /// <summary>「下一个」按钮或查找框回车：让宿主按当前条件向后找一个匹配</summary>
    public event EventHandler? FindNextRequested;
    /// <summary>「上一个」按钮：让宿主反向找一个匹配（与「向后搜索」复选框是两条独立路径）</summary>
    public event EventHandler? FindPreviousRequested;
    /// <summary>「计数」按钮：让宿主统计全文匹配处数，再调 SetMatchCount 回填结果文本</summary>
    public event EventHandler? CountRequested;
    /// <summary>「替换」按钮：替换当前定位到的那一处，替换完继续查找下一处</summary>
    public event EventHandler? ReplaceRequested;
    /// <summary>「在当前文件中替换」按钮：当前文档内一次性替换全部匹配</summary>
    public event EventHandler? ReplaceAllRequested;
    /// <summary>ND2「替换所有打开文件」：逐个文档替换，只读文档跳过</summary>
    public event EventHandler? ReplaceAllInOpenedRequested;
    /// <summary>标记全部匹配，参数为是否同时加书签——false 来自「全部标记」，true 来自「标记并加书签」</summary>
    public event EventHandler<bool>? MarkRequested;
    /// <summary>「清除标记」按钮：清掉这一路条件留下的标记高亮</summary>
    public event EventHandler? ClearMarkRequested;
    /// <summary>ND2 Mark tab 的 Clear All：五种标记色一起清，书签保留</summary>
    public event EventHandler? ClearAllMarksRequested;
    /// <summary>ND2「提取正则结果到剪切板」（copyReResultBt）：只有正则模式可用</summary>
    public event EventHandler? CopyRegexResultRequested;
    /// <summary>「✕ 关闭」按钮或查找框按 Esc：宿主负责收起面板并把焦点还给编辑器</summary>
    public event EventHandler? CloseRequested;

    /// <summary>
    /// 五种标记色的整数值，直接复用 EditorControl.MarkColors：
    /// 面板下拉的预览底色与编辑器里实际绘制的高亮色必须出自同一份表，否则「颜色 N」名不副实。
    /// </summary>
    private static readonly int[] MarkColors = EditorControl.MarkColors;
    /// <summary>查找/替换历史各自保留的条数上限，超出的从最旧一端丢弃（与 ND2 下拉框一致）</summary>
    private const int HistorySize = 25;

    /// <summary>
    /// 构造面板：装配 XAML 生成的控件，填充标记色下拉，并恢复上次的色槽/轮色/历史设置。
    /// </summary>
    public FindReplacePanel()
    {
        InitializeComponent();

        // 逐个色槽生成一条「颜色 N」下拉项，底色就是编辑器里真正会画出来的那个颜色
        for (int i = 0; i < MarkColors.Length; i++)
        {
            MarkColorCombo.Items.Add(new ComboBoxItem
            {
                Content = $"颜色 {i + 1}",
                // MarkColors 存的是 0xRRGGBB 整数，右移拆出 R/G/B 三字节再拼成不透明画刷当色块预览
                Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(0xFF, (byte)(MarkColors[i] >> 16), (byte)(MarkColors[i] >> 8), (byte)MarkColors[i])),
            });
        }

        var settings = SettingsService.Current;
        // 设置里存的是 1 基色槽，ComboBox 的 SelectedIndex 是 0 基，所以夹完再减一；
        // Math.Clamp 兜住配置文件被手改坏的情况，越界一律落回 1..5
        MarkColorCombo.SelectedIndex = Math.Clamp(settings.MarkColorSlot, 1, MarkColors.Length) - 1;
        MarkLoopCheckBox.IsChecked = settings.MarkColorLoop;
        // 两个下拉框挂上上次留下的查找/替换词历史
        RefreshHistory();
    }

    /// <summary>
    /// 当前查找条件（含扩展/正则模式与转义还原）
    /// ——只是把四个控件的状态打包成 SearchCondition，本面板不做任何匹配。
    /// 转义口径由 SearchCondition 统一负责：普通模式原样、扩展模式还原 \n \t \xNN 等、正则模式当模式串用。
    /// </summary>
    public SearchCondition CurrentFind
        => new(FindCombo.Text ?? string.Empty, CurrentMode, MatchCaseCheckBox.IsChecked == true, MatchWordCheckBox.IsChecked == true);

    /// <summary>替换框原文；是否还原转义由宿主按同一个 Mode 调 PrepareReplacement 处理</summary>
    public string CurrentReplace => ReplaceCombo.Text ?? string.Empty;
    /// <summary>「循环查找」勾选状态：查到文末时绕回开头继续（ND2 Wrap around）</summary>
    public bool WrapAround => WrapCheckBox.IsChecked == true;
    /// <summary>「向后搜索」勾选状态：置真时「下一个」也反向找，相当于整轮方向取反</summary>
    public bool Backward => BackwardCheckBox.IsChecked == true;

    /// <summary>
    /// 三个模式单选框对应的查找模式：正则 &gt; 扩展 &gt; 普通 依次判定，
    /// 单选框同组不会同时为真，都不为真（理论上 XAML 默认勾了「普通」）时落回普通模式。
    /// </summary>
    /// <returns>本次条件使用的 SearchMode 三态之一</returns>
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
    /// <returns>1 基色槽号（1..5），调用方直接把它交给 EditorControl 的 MarkAll 系列方法</returns>
    public int TakeMarkSlot()
    {
        // 先取「本次要用」的槽位：下拉是 0 基，对外统一报 1 基，夹一下防越界
        int slot = Math.Clamp(MarkColorCombo.SelectedIndex + 1, 1, MarkColors.Length);
        if (MarkLoopCheckBox.IsChecked == true)
        {
            // 轮色：把下拉推进到下一格。slot 是 1 基，slot % 5 恰好就是下一格的 0 基下标
            //（第 5 色 5%5=0 回到第 1 色），五种颜色循环使用
            MarkColorCombo.SelectedIndex = (slot % MarkColors.Length);
        }
        // 落回 0 基的 SelectedIndex 再 +1 存盘：轮色后的选择也会被记住，下次打开面板接着用
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
    /// <param name="slot">1 基色槽号；越界由 Math.Clamp 拉回 1..5</param>
    public void SelectMarkColorSlot(int slot)
    {
        MarkColorCombo.SelectedIndex = Math.Clamp(slot, 1, MarkColors.Length) - 1;
        var settings = SettingsService.Current;
        settings.MarkColorSlot = MarkColorCombo.SelectedIndex + 1;
        SettingsService.Save();
    }

    /// <summary>
    /// 宿主把默认关键词（如编辑器里的选中文本/当前词）预填进查找框，并把焦点交给面板。
    /// 传空串表示「只想聚焦，不动已有内容」，此时连全选都不做，免得覆盖用户已经写好的词。
    /// </summary>
    /// <param name="text">要预填的查找文本；空或 null 时保持原样</param>
    public void SetFindText(string text)
    {
        if (!string.IsNullOrEmpty(text)) FindCombo.Text = text;
        FindCombo.Focus();
        if (string.IsNullOrEmpty(text)) return;
        // WPF 的可编辑 ComboBox 没有 SelectAll，要选中里面的输入框
        if (FindCombo.Template?.FindName("PART_EditableTextBox", FindCombo) is System.Windows.Controls.TextBox box)
            box.SelectAll();
    }

    /// <summary>
    /// 回填计数/标记的结果文本。宿主查完自己调这里，面板不主动去问编辑器。
    /// </summary>
    /// <param name="count">匹配处数；0 或负数一律显示「没有匹配」</param>
    public void SetMatchCount(int count)
        => MatchCountText.Text = count > 0 ? $"{count} 处匹配" : "没有匹配";

    /// <summary>把一条查找/替换内容记进历史（最新在前、去重），与 ND2 下拉行为一致</summary>
    /// <param name="findText">查找框文本；空串不入库</param>
    /// <param name="replaceText">替换框文本；空串不入库</param>
    public void Remember(string findText, string replaceText)
    {
        bool changed = false;
        var settings = SettingsService.Current;
        if (!string.IsNullOrEmpty(findText)) { Prepend(settings.FindHistory, findText); changed = true; }
        if (!string.IsNullOrEmpty(replaceText)) { Prepend(settings.ReplaceHistory, replaceText); changed = true; }
        // 两条都是空就没必要落盘，也没必要重挂下拉数据源
        if (changed)
        {
            SettingsService.Save();
            RefreshHistory();
        }
    }

    /// <summary>
    /// 历史入库的公共尾部：先按原文（区分大小写）删掉同一条旧记录，再插到最前面，
    /// 最后把超出 HistorySize 的尾部旧条目挤掉——所以下拉里永远是「最新在前、无重复」。
    /// </summary>
    /// <param name="history">要原地修改的历史列表（设置对象里的实例）</param>
    /// <param name="value">本次要记住的文本</param>
    private static void Prepend(List<string> history, string value)
    {
        history.RemoveAll(h => string.Equals(h, value, StringComparison.Ordinal));
        history.Insert(0, value);
        while (history.Count > HistorySize) history.RemoveAt(history.Count - 1);
    }

    /// <summary>
    /// 把两份历史重新挂到两个下拉框上。
    /// 复制成数组是为了给 ItemsSource 一个静态快照，避免设置里的可变列表被继续追加后出现重复项。
    /// </summary>
    private void RefreshHistory()
    {
        var settings = SettingsService.Current;
        FindCombo.ItemsSource = settings.FindHistory.ToArray();
        ReplaceCombo.ItemsSource = settings.ReplaceHistory.ToArray();
    }

    // ==================== 界面动作 → 事件转发 ====================
    // 下面这一组 Click 处理器都只做一件事：把按钮点击翻译成对应事件发给宿主，
    // 条件由宿主在回调里现取 CurrentFind / CurrentReplace / WrapAround / Backward，面板不参与执行。

    /// <summary>「下一个」按钮 Click：转发为 FindNextRequested</summary>
    /// <param name="sender">按钮本身，未使用</param>
    /// <param name="e">标准 RoutedEventArgs，未使用</param>
    private void OnFindNext(object sender, RoutedEventArgs e) => FindNextRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>「上一个」按钮 Click：转发为 FindPreviousRequested（宿主强制反向查找）</summary>
    /// <param name="sender">按钮本身，未使用</param>
    /// <param name="e">标准 RoutedEventArgs，未使用</param>
    private void OnFindPrevious(object sender, RoutedEventArgs e) => FindPreviousRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>「计数」按钮 Click：转发为 CountRequested，结果之后由 SetMatchCount 回填</summary>
    /// <param name="sender">按钮本身，未使用</param>
    /// <param name="e">标准 RoutedEventArgs，未使用</param>
    private void OnCount(object sender, RoutedEventArgs e) => CountRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>「替换」按钮 Click：转发为 ReplaceRequested，替换当前定位到的那一处</summary>
    /// <param name="sender">按钮本身，未使用</param>
    /// <param name="e">标准 RoutedEventArgs，未使用</param>
    private void OnReplace(object sender, RoutedEventArgs e) => ReplaceRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>「在当前文件中替换」按钮 Click：转发为 ReplaceAllRequested</summary>
    /// <param name="sender">按钮本身，未使用</param>
    /// <param name="e">标准 RoutedEventArgs，未使用</param>
    private void OnReplaceAll(object sender, RoutedEventArgs e) => ReplaceAllRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>「替换所有打开文件」按钮 Click：转发为 ReplaceAllInOpenedRequested，只读文档由宿主跳过</summary>
    /// <param name="sender">按钮本身，未使用</param>
    /// <param name="e">标准 RoutedEventArgs，未使用</param>
    private void OnReplaceAllInOpened(object sender, RoutedEventArgs e) => ReplaceAllInOpenedRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>「全部标记」按钮 Click：MarkRequested 带 false，只上高亮不加书签</summary>
    /// <param name="sender">按钮本身，未使用</param>
    /// <param name="e">标准 RoutedEventArgs，未使用</param>
    private void OnMarkAll(object sender, RoutedEventArgs e) => MarkRequested?.Invoke(this, false);

    /// <summary>「标记并加书签」按钮 Click：MarkRequested 带 true，高亮之外再给每处加一个书签</summary>
    /// <param name="sender">按钮本身，未使用</param>
    /// <param name="e">标准 RoutedEventArgs，未使用</param>
    private void OnMarkWithBookmark(object sender, RoutedEventArgs e) => MarkRequested?.Invoke(this, true);

    /// <summary>「清除标记」按钮 Click：转发为 ClearMarkRequested</summary>
    /// <param name="sender">按钮本身，未使用</param>
    /// <param name="e">标准 RoutedEventArgs，未使用</param>
    private void OnClearMark(object sender, RoutedEventArgs e) => ClearMarkRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>「清除全部」按钮 Click（ND2 的 F7）：转发为 ClearAllMarksRequested，五种色一起清、书签保留</summary>
    /// <param name="sender">按钮本身，未使用</param>
    /// <param name="e">标准 RoutedEventArgs，未使用</param>
    private void OnClearAllMarks(object sender, RoutedEventArgs e) => ClearAllMarksRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>「提取正则结果」按钮 Click：转发为 CopyRegexResultRequested，非正则模式下由宿主给提示</summary>
    /// <param name="sender">按钮本身，未使用</param>
    /// <param name="e">标准 RoutedEventArgs，未使用</param>
    private void OnCopyRegexResult(object sender, RoutedEventArgs e) => CopyRegexResultRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>「✕ 关闭」按钮 Click：转发为 CloseRequested</summary>
    /// <param name="sender">按钮本身，未使用</param>
    /// <param name="e">标准 RoutedEventArgs，未使用</param>
    private void OnClose(object sender, RoutedEventArgs e) => CloseRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>
    /// 查找框的 KeyDown：面板内唯一的键盘快捷键，Esc 关面板、回车查找下一个。
    /// 两个分支都置 Handled，避免回车被下拉框当成「确认下拉项」、Esc 被当成「收起下拉」。
    /// </summary>
    /// <param name="sender">查找下拉框，未使用</param>
    /// <param name="e">按键事件，读取 Key 并回写 Handled</param>
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
