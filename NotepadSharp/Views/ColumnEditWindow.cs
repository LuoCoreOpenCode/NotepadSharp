using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using NotepadSharp.Controls;
using Application = System.Windows.Application;
using Brush = System.Windows.Media.Brush;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using ComboBox = System.Windows.Controls.ComboBox;
using MessageBox = System.Windows.MessageBox;
using Orientation = System.Windows.Controls.Orientation;
using TextBox = System.Windows.Controls.TextBox;

namespace NotepadSharp.Views;

/// <summary>
/// 「列与宏 → 列编辑」：给选中的每一行在指定列插入前缀 + 文本 + 递增序号。
/// 位置可选行首、选区左边界、光标列，适合批量给多行加注释/编号/列前缀。
/// 与 <see cref="ColumnEditorWindow"/> 的区别：这个窗口是非模态的常驻工具窗（宿主用 Show 打开并做单例），
/// 每点一次「应用」就立刻写一次文档；列编辑器是对话框式的一次性参数确认后由 EditorControl.ColumnEditor 落地。
/// 换算口径：行列一律用 EditorControl.GetColumnEditContext 拿到的 0 基行号 + 视觉列（Tab 已按制表位展开），
/// 展示给用户时才 +1 变成「第 N 行 / 第 N 列」；落点由 InsertColumnText 按码元偏移计算，含 Tab 的行两种口径会有差异。
/// </summary>
public sealed class ColumnEditWindow : Window
{
    /// <summary>本窗口绑定的目标编辑器；构造时注入，之后每次应用都现取它的选区上下文（不做内容快照）</summary>
    private readonly EditorControl _editor;
    /// <summary>插入串最前面的前缀，对每一行都相同</summary>
    private readonly TextBox _prefix = new();
    /// <summary>插入串最后面的固定文本，对每一行都相同</summary>
    private readonly TextBox _text = new();
    /// <summary>是否插入递增序号；未勾选时三个数值输入框置灰，序号串按空串处理</summary>
    private readonly CheckBox _number = new() { Content = "递增序号" };
    /// <summary>序号起始值（第一个被处理的选区行拿到的值）</summary>
    private readonly TextBox _start = new() { Text = "1", Width = 60 };
    /// <summary>序号步长，逐行累加，可为负数做倒序</summary>
    private readonly TextBox _step = new() { Text = "1", Width = 60 };
    /// <summary>序号补齐位数（"D{digits}" 的 D 格式），0 表示不补，上限 9</summary>
    private readonly TextBox _digits = new() { Text = "3", Width = 60 };
    /// <summary>插入位置下拉：项序 0 行首 / 1 选区左边界 / 2 光标列，与 Apply 里的 switch 分支一一对应</summary>
    private readonly ComboBox _where = new() { Width = 130 };
    /// <summary>窗口底部的提示行：打开时显示当前选区范围与两口径列号，应用后改为结果播报</summary>
    private readonly TextBlock _status = new();

    /// <summary>
    /// 构建列编辑窗口：设窗口形态 → 装配控件与自动化标识 → 初始刷新序号可用态与选区提示。
    /// 窗口以非模态方式显示，故订阅 Activated 在每次被激活时重取一次选区上下文（选区可能在别处被改过）。
    /// </summary>
    /// <param name="editor">要操作的编辑器控件，由宿主 MainWindow 传入当前标签页对应的实例</param>
    public ColumnEditWindow(EditorControl editor)
    {
        _editor = editor;
        // 窗口形态：高度自适应、固定宽度、不可缩放、相对主窗居中；背景取主题面板画刷
        Title = "列编辑";
        SizeToContent = SizeToContent.Height;
        Width = 460;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Application.Current.Resources["PanelBackground"] as Brush;

        // 插入位置的三个选项：SelectedIndex 就是 Apply 里 switch 的分支号，改顺序会改语义
        _where.Items.Add("行首");
        _where.Items.Add("选区左边界");
        _where.Items.Add("光标列");
        // 默认「选区左边界」：列编辑的典型用法就是沿着拖选出的矩形左缘批量插东西
        _where.SelectedIndex = 1;

        // AutomationId：给 UI 自动化与无障碍工具按名字定位控件用，不参与任何界面逻辑
        AutomationProperties.SetAutomationId(_prefix, "ColPrefix");
        AutomationProperties.SetAutomationId(_text, "ColText");
        AutomationProperties.SetAutomationId(_number, "ColNumber");
        AutomationProperties.SetAutomationId(_start, "ColStart");
        AutomationProperties.SetAutomationId(_step, "ColStep");
        AutomationProperties.SetAutomationId(_digits, "ColDigits");
        AutomationProperties.SetAutomationId(_where, "ColWhere");
        AutomationProperties.SetAutomationId(_status, "ColStatus");

        // 勾/去勾序号都只要重算一次可用态
        _number.Checked += (_, _) => UpdateNumberInputs();
        _number.Unchecked += (_, _) => UpdateNumberInputs();

        // 第一行：前缀 + 文本；Row 是「一行两组标签+控件」的通用装配器
        var panel = new StackPanel { Margin = new Thickness(12) };
        panel.Children.Add(Row("前缀", _prefix, 220, "文本", _text, 220));
        // 第二行只有插入位置一个控件：后四个参数传空，Row 会跳过右半组的生成
        panel.Children.Add(Row("插入位置", _where, 0, "", null, 0));

        // 第三行：序号勾选框 + 起始/步长/位数三个窄输入框，标签用 Caption 塞在框前
        var numberRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        _number.VerticalAlignment = System.Windows.VerticalAlignment.Center;
        _number.Foreground = Application.Current.Resources["TextBrush"] as Brush;
        numberRow.Children.Add(_number);
        numberRow.Children.Add(Caption("起始", 10));
        numberRow.Children.Add(_start);
        numberRow.Children.Add(Caption("步长", 10));
        numberRow.Children.Add(_step);
        numberRow.Children.Add(Caption("位数", 10));
        numberRow.Children.Add(_digits);
        panel.Children.Add(numberRow);

        // 按钮条右对齐：「应用」可反复点（不关窗），「关闭」只关窗；关窗后宿主把单例引用置空
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = System.Windows.HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        buttons.Children.Add(TextButton("应用", Apply, "ColApply"));
        buttons.Children.Add(TextButton("关闭", Close));
        panel.Children.Add(buttons);

        // 底部提示行：次要文字画刷 + 自动换行，选区行号跨度长了也不会撑破窗口
        _status.Foreground = Application.Current.Resources["TextSecondaryBrush"] as Brush;
        _status.TextWrapping = TextWrapping.Wrap;
        _status.Margin = new Thickness(0, 8, 0, 0);
        panel.Children.Add(_status);

        Content = panel;
        // 序号输入框的初始可用态跟随 _number 的默认勾选状态（未勾 → 置灰）
        UpdateNumberInputs();
        // 打开即播报一次当前选区，之后每次被激活再刷新，避免显示过期的行/列号
        UpdateRangeHint();
        Activated += (_, _) => UpdateRangeHint();
    }

    /// <summary>造一个垂直居中的小标签文本块，用于给输入框加前置说明</summary>
    /// <param name="text">标签文字</param>
    /// <param name="marginLeft">左边距，默认 0；同行里用它给控件之间留出间隔</param>
    /// <returns>可直接塞进面板的 TextBlock</returns>
    private static TextBlock Caption(string text, double marginLeft = 0) => new()
    {
        Text = text,
        VerticalAlignment = System.Windows.VerticalAlignment.Center,
        Margin = new Thickness(marginLeft, 0, 6, 0),
    };

    /// <summary>造一行「标签 + 控件（+ 可选的第二组）」的四列网格：两列 Auto 放标签，两列 Auto 放控件</summary>
    /// <param name="labelA">左组标签文字</param>
    /// <param name="controlA">左组控件；传 null 表示这行左半只有标签</param>
    /// <param name="widthA">左组控件宽度，&lt;=0 表示不改控件自带宽度</param>
    /// <param name="labelB">右组标签文字，仅在 controlB 非 null 时使用</param>
    /// <param name="controlB">右组控件；传 null 表示这行只有一组</param>
    /// <param name="widthB">右组控件宽度，直接赋值（右组存在时才生效）</param>
    /// <returns>可塞进竖排面板的 Grid 行</returns>
    private static Grid Row(string labelA, FrameworkElement? controlA, double widthA, string labelB, FrameworkElement? controlB, double widthB)
    {
        // 底部留 8px，行与行之间自然隔开；四列全 Auto，控件多宽就占多宽
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 8) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // 左组：标签固定在第 0 列，控件有就进第 1 列
        var a = Caption(labelA);
        Grid.SetColumn(a, 0);
        grid.Children.Add(a);
        if (controlA != null)
        {
            if (widthA > 0) controlA.Width = widthA;
            Grid.SetColumn(controlA, 1);
            grid.Children.Add(controlA);
        }
        // 右组：标签带 16px 左边距跟左控件拉开，控件进第 3 列
        if (controlB != null)
        {
            var b = Caption(labelB, 16);
            Grid.SetColumn(b, 2);
            grid.Children.Add(b);
            controlB.Width = widthB;
            Grid.SetColumn(controlB, 3);
            grid.Children.Add(controlB);
        }
        return grid;
    }

    /// <summary>造一个把动作直接绑到 Click 的普通按钮；点击后不关窗，所以「应用」可以连着点多次</summary>
    /// <param name="label">按钮文字</param>
    /// <param name="action">点击要执行的动作（无参无返回，失败信息自行写进状态行或弹窗）</param>
    /// <param name="automationId">可选的 AutomationId，供 UI 自动化定位；null 表示不设</param>
    /// <returns>已挂好事件的 Button</returns>
    private Button TextButton(string label, Action action, string? automationId = null)
    {
        // 左外边距 6px 让按钮之间留缝；MinWidth 保证「应用」「关闭」一样宽
        var b = new Button { Content = label, Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(6, 0, 0, 0), MinWidth = 70 };
        if (automationId != null) AutomationProperties.SetAutomationId(b, automationId);
        b.Click += (_, _) => action();
        return b;
    }

    /// <summary>序号勾选联动：只有勾了「递增序号」才放开起始/步长/位数三个框</summary>
    private void UpdateNumberInputs()
    {
        bool on = _number.IsChecked == true;
        // 链式赋值：三个框共用同一个开关，一次写定
        _start.IsEnabled = _step.IsEnabled = _digits.IsEnabled = on;
    }

    /// <summary>
    /// 把当前选区上下文写进状态行：行范围 + 两个列口径。
    /// 列号在这里 +1 转成用户视角的「第 N 列」；「左边界」取选区起点列与光标列的较小值，
    /// 因为 Scintilla 的 SelectionStart 是排序后的锚点，不反映拖动方向，反向拖选时靠 caret 列补回来。
    /// </summary>
    private void UpdateRangeHint()
    {
        var (startLine, endLine, startCol, caretCol) = _editor.GetColumnEditContext();
        _status.Text = $"{RangeText(startLine, endLine)}，" +
                       $"左边界第 {Math.Min(startCol, caretCol) + 1} 列，光标第 {caretCol + 1} 列";
    }

    /// <summary>选区行范围的中文描述：单行只报一行，跨行额外报总行数（行号做 0 基 → 1 基换算）</summary>
    /// <param name="startLine">起始行号（0 基，含）</param>
    /// <param name="endLine">结束行号（0 基，含）</param>
    /// <returns>形如「当前选区：第 3–10 行（8 行）」的文案</returns>
    private static string RangeText(int startLine, int endLine)
        => startLine == endLine
            ? $"当前选区：第 {startLine + 1} 行"
            : $"当前选区：第 {startLine + 1}–{endLine + 1} 行（{endLine - startLine + 1} 行）";

    /// <summary>
    /// 「应用」：把 前缀 + 序号 + 文本 逐行插到选定列，整体算一步撤销。
    /// 插入列由下拉框决定：0 行首（列 0）、1 选区左边界（起点列与光标列取小）、其余按光标列；
    /// 序号按「起始 + 行偏移 × 步长」自上而下递增，位数 &gt;0 时用 D 格式左补零。
    /// 无选区时起止行都等于光标行，退化成「只改当前行」。
    /// </summary>
    private void Apply()
    {
        // 每次现取上下文：窗口常驻非模态，选区在用户回到编辑器后随时可能变
        var (startLine, endLine, startCol, caretCol) = _editor.GetColumnEditContext();
        // 下拉框项序 → 目标列（视觉列，0 基）；默认分支兜住未选中的情况
        int column = _where.SelectedIndex switch
        {
            0 => 0,
            1 => Math.Min(startCol, caretCol),
            _ => caretCol,
        };

        bool numbering = _number.IsChecked == true;
        string prefix = _prefix.Text, text = _text.Text;
        // 空插不做：三样都没有就没必要动文档，只把原因写在状态行
        if (!numbering && prefix.Length == 0 && text.Length == 0)
        {
            _status.Text = "没有要插入的内容：前缀、文本、序号至少填一项。";
            return;
        }

        // 只有开了序号才解析三个整数；位数被限定在 0–9，越界视为非法输入（未勾序号时这三个框是置灰的，值不参与运算）
        // 注意短路：任一项解析失败就弹窗并留在窗口，不产生任何改动
        int start = 1, step = 1, digits = 0;
        if (numbering
            && (!int.TryParse(_start.Text, out start) || !int.TryParse(_step.Text, out step)
                || !int.TryParse(_digits.Text, out digits) || digits is < 0 or > 9))
        {
            MessageBox.Show(this, "序号的起始 / 步长 / 位数要填整数（位数 0–9）。", "列编辑",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        int count = endLine - startLine + 1;
        // 包成一个撤销组：Ctrl+Z 一步整体回退，而不是逐行撤 N 次
        _editor.BeginEditGroup();
        try
        {
            // 从末行往上插：插入只加长本行不改行数，但倒着走可以保证行号与列位置全程不受前面插入影响
            for (int line = endLine; line >= startLine; line--)
            {
                // 行偏移 (line - startLine) 保证「最上面的行拿到起始值」，与倒序遍历无关；
                // digits>0 时套 D 格式左补零，否则走默认 ToString()。
                // 这里没传 CultureInfo.InvariantCulture（列编辑器那条路径传了），严格说会跟随当前区域设置的数字形状
                string number = numbering
                    ? (start + (line - startLine) * step).ToString(digits > 0 ? "D" + digits : null)
                    : string.Empty;
                _editor.InsertColumnText(line, column, prefix + number + text);
            }
        }
        finally
        {
            // 中途抛异常（例如行号越界）也必须闭合撤销组，否则 Scintilla 的 undo 动作会一直挂着
            _editor.EndEditGroup();
        }

        // 焦点还给编辑器，方便直接继续编辑或 Ctrl+Z；状态行播报本次作用范围
        _editor.FocusEditor();
        _status.Text = $"已插入 {count} 行（第 {startLine + 1}–{endLine + 1} 行，第 {column + 1} 列），Ctrl+Z 可整体撤销";
    }
}
