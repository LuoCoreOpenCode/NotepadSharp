using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using NotepadSharp.Controls;
using NotepadSharp.Services;
using Orientation = System.Windows.Controls.Orientation;
using TextBox = System.Windows.Controls.TextBox;
using RadioButton = System.Windows.Controls.RadioButton;
using CheckBox = System.Windows.Controls.CheckBox;
using ComboBox = System.Windows.Controls.ComboBox;
using ComboBoxItem = System.Windows.Controls.ComboBoxItem;
using Button = System.Windows.Controls.Button;
using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;
using WpfAlignment = System.Windows.HorizontalAlignment;

namespace NotepadSharp.Views;

/// <summary>
/// 列编辑器对话框（插入文本 / 复制首行 / 粘贴剪贴板 / 递增编号）。
/// 四个单选钮的下标就是传给编辑器的模式序号：0 插入文本、1 复制首行到各行、2 粘贴剪贴板到各行、3 插入递增编号。
/// 控件的启用范围跟着模式走：只有 0 用到「插入文本」框，只有 3 用到进制/初值/步长/重复次数/位数/前缀/前导零这一组；
/// 1、2 的插入内容分别来自文档首行与剪贴板，因此这两种模式下其余输入框全部置灰。
/// 用法：宿主以模态方式 ShowDialog()，返回 true 后读下面的公开属性，原样喂给 EditorControl.ColumnEditor。
/// 点「确定」时还会把整组参数序列化成一行存进设置（ColumnEditorLastUsed），下次打开自动回填。
/// 备注：对照文档 §4「编辑与格式化」的列块编辑条目记明 ND2 侧的对话框 .ui 与位掩码（MASK_FORMAT/MASK_ZERO_LEADING）还没逐行取证，
/// 本窗口的控件分组按列块编辑器的通用语义排布，不在此处标注 ND2 源码行号。
/// </summary>
public sealed class ColumnEditorWindow : Window
{
    /// <summary>承载四个模式单选钮的面板：WPF 里同一父容器内的 RadioButton 自动互斥，所以必须都塞进这一个 StackPanel</summary>
    private readonly StackPanel _modePanel = new();
    /// <summary>四个模式钮的引用表，下标即模式序号，供 <see cref="SelectedMode"/> 反查勾选与设置回填</summary>
    private readonly List<RadioButton> _modes = new();
    /// <summary>模式 0 要插入的字面文本：选区里的每一行都插同一串</summary>
    private readonly TextBox _text = new();
    /// <summary>递增编号的起始值（仅模式 3），出厂文本 "1"</summary>
    private readonly TextBox _initial = new() { Text = "1" };
    /// <summary>递增编号的步长（仅模式 3），允许负数表示倒序编号</summary>
    private readonly TextBox _increment = new() { Text = "1" };
    /// <summary>同一个编号连续重复占用的行数（仅模式 3）；&lt;1 时由编辑端按 1 处理</summary>
    private readonly TextBox _repeat = new() { Text = "1" };
    /// <summary>编号要补齐到的最少位数（仅模式 3）；0 表示不补位</summary>
    private readonly TextBox _digits = new() { Text = "0" };
    /// <summary>拼在编号最前面的前缀（仅模式 3），如 <c>0x</c></summary>
    private readonly TextBox _prefix = new();
    /// <summary>是否用 0 左补到「位数」；构造即勾选，与编辑端 leadingZero 参数同义</summary>
    private readonly CheckBox _leadingZero = new() { Content = "前导零补齐", IsChecked = true };
    /// <summary>编号进制下拉框：每项的 Tag 存 EditorControl.ColumnNumberFormat，项序与枚举序号刻意一一对应</summary>
    private readonly ComboBox _radix = new();

    /// <summary>用户确认的模式序号：0 插入文本 / 1 复制首行到各行 / 2 粘贴剪贴板到各行 / 3 插入递增编号</summary>
    public int Mode { get; private set; }
    /// <summary>模式 0 的插入文本；其它模式也会原样带回，由编辑端自行忽略</summary>
    public string Text { get; private set; } = string.Empty;
    /// <summary>编号起始值；非模式 3 时复位为 1</summary>
    public int Initial { get; private set; } = 1;
    /// <summary>编号步长；非模式 3 时复位为 1</summary>
    public int Increment { get; private set; } = 1;
    /// <summary>同一编号的重复行数；非模式 3 时复位为 1</summary>
    public int Repeat { get; private set; } = 1;
    /// <summary>编号补齐位数；非模式 3 时复位为 0（不补位）</summary>
    public int Digits { get; private set; }
    /// <summary>编号进制；下拉框拿不到合法 Tag 时回落为十进制</summary>
    public EditorControl.ColumnNumberFormat NumberFormat { get; private set; }
    /// <summary>是否前导零补齐，对应编辑端 FormatColumnNumber 的 PadLeft 开关</summary>
    public bool LeadingZero { get; private set; } = true;
    /// <summary>编号前缀；仅模式 3 参与拼接，且永远拼在编号字符串最前面</summary>
    public string Prefix { get; private set; } = string.Empty;

    /// <summary>
    /// 构建列编辑器对话框：摆控件 → 回填上次用过的参数 → 按当前模式刷新可用态 → 焦点给「插入文本」框。
    /// 无参构造：初值只来自各字段的默认值与 SettingsService.Current.ColumnEditorLastUsed，不接收外部上下文。
    /// </summary>
    public ColumnEditorWindow()
    {
        // 窗口形态：高度按内容自适应、禁止缩放、不进任务栏、屏幕居中；背景取主题资源，深浅色都能跟着走
        Title = "列编辑器";
        Width = 440;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Background = Application.Current.Resources["PanelBackground"] as System.Windows.Media.Brush;

        // 四个模式单选钮：同一父面板内自动互斥（点一个就取消上一个），勾选变化必须立刻回灌可用态，
        // 所以每个钮都挂同一个 UpdateEnabled，而不是等确定时再算
        foreach (var label in new[] { "插入文本", "复制首行到各行", "粘贴剪贴板到各行", "插入递增编号" })
        {
            var radio = new RadioButton
            {
                Content = label,
                Margin = new Thickness(0, 2, 12, 2),
                Foreground = Application.Current.Resources["TextBrush"] as System.Windows.Media.Brush,
            };
            radio.Checked += (_, _) => UpdateEnabled();
            _modes.Add(radio);
            _modePanel.Children.Add(radio);
        }
        // 默认落在「插入文本」——也是 _modes 列表下标 0，即 Mode 的出厂值
        _modes[0].IsChecked = true;

        // 进制下拉项：显示文案与 EditorControl.ColumnNumberFormat 成对写死，真正的枚举值塞进 Tag，
        // 这里项序恰好等于枚举序号，RestoreLastUsed 才能直接拿存下来的数字当 SelectedIndex 用
        foreach (var (label, fmt) in new[]
        {
            ("十进制", EditorControl.ColumnNumberFormat.Decimal),
            ("十六进制（大写）", EditorControl.ColumnNumberFormat.HexUpper),
            ("十六进制（小写）", EditorControl.ColumnNumberFormat.HexLower),
            ("八进制", EditorControl.ColumnNumberFormat.Octal),
            ("二进制", EditorControl.ColumnNumberFormat.Binary),
        })
        {
            _radix.Items.Add(new ComboBoxItem { Content = label, Tag = fmt });
        }
        // 默认十进制（项序 0 == 枚举序号 0），未选进制时编辑端也按十进制处理
        _radix.SelectedIndex = 0;

        // 表单网格：两列（左列标签 Auto、右列控件吃满剩余宽度）+ 七行，
        // 行数要和下面 AddRow 的调用次数对齐，否则控件会掉进未定义的行
        var grid = new Grid { Margin = new Thickness(14, 10, 14, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (int i = 0; i < 7; i++)
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // 逐行装配：方式（四个钮的面板整体占一格）、插入文本、编号格式、初值/步长、重复/位数、前缀，
        // 最后一行 label 传 null——整格只放勾选框，与上面的标签列对齐留空
        int row = 0;
        AddRow(grid, "方式", row, _modePanel); row++;
        AddRow(grid, "插入文本", row, _text); row++;
        AddRow(grid, "编号格式", row, _radix); row++;
        AddRow(grid, "初始值 / 步长", row, Pair(_initial, _increment)); row++;
        AddRow(grid, "重复次数 / 位数", row, Pair(_repeat, _digits)); row++;
        AddRow(grid, "前缀", row, _prefix); row++;
        AddRow(grid, null, row, _leadingZero); row++;

        // 按钮条：竖排面板底部右对齐；「确定」IsDefault 吃回车，「取消」IsCancel 吃 Esc 并把 DialogResult 留空
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = WpfAlignment.Right,
            Margin = new Thickness(0, 14, 14, 12),
        };
        var ok = new Button { Content = "确定", Width = 76, IsDefault = true, Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new Button { Content = "取消", Width = 76, IsCancel = true };
        // 确定：校验 + 落值 + 存历史全在 OnOk 里；取消不写任何挂接，靠 IsCancel 直接关窗，公开属性保持出厂默认
        ok.Click += OnOk;
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);

        // 外层竖排：表单在上、按钮条在下；SizeToContent.Height 会按这两块的自然高度定窗口高度
        var root = new StackPanel();
        root.Children.Add(grid);
        root.Children.Add(buttons);
        Content = root;

        // 顺序有讲究：先回填历史（勾模式钮会连锁触发 UpdateEnabled），
        // 再显式刷一次可用态兜住「没有历史」的情况，最后把光标放到最常用的插入文本框
        RestoreLastUsed();
        UpdateEnabled();
        _text.Focus();
    }

    /// <summary>当前勾选的模式序号：用引用表反查，不给按钮挂 Tag</summary>
    /// <returns>0–3 的模式序号；一个都没勾时返回 -1（构造初期、或历史值越界没回填成功时），调用处用 Math.Max 夹成 0</returns>
    private int SelectedMode => _modes.FindIndex(r => r.IsChecked == true);

    /// <summary>往表单网格加一行：左列可选标签 + 右列控件，两者各自定位到同一行</summary>
    /// <param name="grid">目标表单网格</param>
    /// <param name="label">左列标签文本；传 null 表示这行不放标签（整行只有一个勾选框时用）</param>
    /// <param name="row">行下标（0 基，须落在网格已定义的行内）</param>
    /// <param name="element">放进第 1 列的控件</param>
    private static void AddRow(Grid grid, string? label, int row, UIElement element)
    {
        // 标签：垂直居中贴左列，右边留 12px 与控件隔开；前景同样取主题文字画刷
        if (label != null)
        {
            var text = new TextBlock
            {
                Text = label,
                Margin = new Thickness(0, 5, 12, 5),
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = Application.Current.Resources["TextBrush"] as System.Windows.Media.Brush,
            };
            Grid.SetRow(text, row);
            Grid.SetColumn(text, 0);
            grid.Children.Add(text);
        }

        // 控件一律进第 1 列（Star 那列），因此「方式」这种面板整体也只需要一格
        Grid.SetRow(element, row);
        Grid.SetColumn(element, 1);
        grid.Children.Add(element);
    }

    /// <summary>把两个输入框并排包一层横向面板，用于「初始值 / 步长」「重复次数 / 位数」这类一行两栏的条目</summary>
    /// <param name="left">左侧输入框</param>
    /// <param name="right">右侧输入框</param>
    /// <returns>可直接作为一行控件塞进表单的 StackPanel</returns>
    private static StackPanel Pair(TextBox left, TextBox right)
    {
        // 左右框之间留 8px，竖向各 3px 让它们与上面的单选钮行高齐平
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        left.Margin = new Thickness(0, 3, 8, 3);
        right.Margin = new Thickness(0, 3, 0, 3);
        panel.Children.Add(left);
        panel.Children.Add(right);
        return panel;
    }

    /// <summary>
    /// 按当前模式刷新输入框可用态：模式 0 只放开「插入文本」，模式 3 放开整组编号参数，
    /// 模式 1/2（内容来自首行与剪贴板）什么都不放开，避免用户误以为填了会生效。
    /// 挂载点：每个模式钮的 Checked 事件 + 构造末尾。
    /// </summary>
    private void UpdateEnabled()
    {
        int mode = SelectedMode;
        _text.IsEnabled = mode == 0;
        // 下面七个控件只服务「插入递增编号」，非模式 3 一律置灰
        _radix.IsEnabled = mode == 3;
        _initial.IsEnabled = mode == 3;
        _increment.IsEnabled = mode == 3;
        _repeat.IsEnabled = mode == 3;
        _digits.IsEnabled = mode == 3;
        _prefix.IsEnabled = mode == 3;
        _leadingZero.IsEnabled = mode == 3;
    }

    /// <summary>
    /// 「确定」按钮（也是回车）的处理：取模式 → 落公开属性 → 只在编号模式下校验四个整数 → 存历史 → DialogResult = true 关窗。
    /// 校验不过时只弹提示并 return，窗口留着让用户改，DialogResult 不会置真。
    /// </summary>
    /// <param name="sender">确定按钮，未使用</param>
    /// <param name="e">RoutedEventArgs，未使用</param>
    private void OnOk(object sender, RoutedEventArgs e)
    {
        // 与编号无关的四项先取走（模式、文本、前缀、前导零）；SelectedMode 理论上可能返回 -1（没有钮被勾），夹成 0 防越界
        Mode = Math.Max(0, SelectedMode);
        Text = _text.Text;
        Prefix = _prefix.Text;
        LeadingZero = _leadingZero.IsChecked == true;

        if (Mode == 3)
        {
            // 四个整数一起解析：okNumbers 一旦为 false 就一路带到末尾（ &=  短路不了，保证每个都解析过、out 值都有定值）
            bool okNumbers = TryParseInt(_initial.Text, out int initial);
            okNumbers &= TryParseInt(_increment.Text, out int increment);
            okNumbers &= TryParseInt(_repeat.Text, out int repeat);
            okNumbers &= TryParseInt(_digits.Text, out int digits);
            if (!okNumbers)
            {
                MessageBox.Show(this, "初始值 / 步长 / 重复次数 / 位数必须是整数。", "列编辑器",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            Initial = initial;
            Increment = increment;
            Repeat = repeat;
            Digits = digits;
        }
        else
        {
            // 非编号模式把数值项复位成中性默认，免得窗口内改过又填错的历史值串到本次调用
            Initial = 1;
            Increment = 1;
            Repeat = 1;
            Digits = 0;
        }

        // 进制只认 ComboBoxItem.Tag 里的枚举值；取不到（例如 SelectedIndex 为 -1）回落十进制
        NumberFormat = (_radix.SelectedItem as ComboBoxItem)?.Tag is EditorControl.ColumnNumberFormat fmt
            ? fmt
            : EditorControl.ColumnNumberFormat.Decimal;

        // 落盘「上次用过的参数」并保存，下次打开这个对话框直接回填；DialogResult = true 同时让 ShowDialog 返回 true
        SettingsService.Current.ColumnEditorLastUsed = ToSettingsString();
        SettingsService.Save();
        DialogResult = true;
    }

    /// <summary>按不变文化解析整数：先 Trim 首尾空白，接受正负号，不接受小数点、千分位与十六进制字面量</summary>
    /// <param name="s">输入框里的原始文本</param>
    /// <param name="value">解析结果；失败时为 0</param>
    /// <returns>解析是否成功</returns>
    private static bool TryParseInt(string s, out int value)
        => int.TryParse(s.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value);

    /// <summary>
    /// 把九个字段用 '|' 串成一行，供设置项 ColumnEditorLastUsed 存取。
    /// 字段顺序必须与 <see cref="RestoreLastUsed"/> 的解析下标严格一致；
    /// 前缀放在最后一位，所以它内部允许出现 '|'。
    /// </summary>
    /// <returns>形如「模式|文本|初值|步长|重复|位数|进制|前导零|前缀」的一行文本，其中布尔位写成 1/0</returns>
    public string ToSettingsString()
        => string.Join('|', Mode, Text, Initial, Increment, Repeat, Digits, (int)NumberFormat, LeadingZero ? 1 : 0, Prefix);

    /// <summary>
    /// 从设置里读回上次用过的参数并回填控件，解析口径与 <see cref="ToSettingsString"/> 一一对应。
    /// 历史串为空、字段不足或序号越界时一律保持控件出厂值，不抛异常——用户手改配置文件也不会把对话框打开就崩。
    /// </summary>
    private void RestoreLastUsed()
    {
        string saved = SettingsService.Current.ColumnEditorLastUsed;
        // 空串＝首次使用，直接用默认值
        if (string.IsNullOrEmpty(saved)) return;

        // 最多切 9 段：只在前 8 个分隔符处断开，剩下的原文全归最后一段（前缀），
        // 因此前缀里的 '|' 不会被切碎；反过来，「插入文本」里含 '|' 会让后面几段整体错位，这是朴素序列化的已知取舍
        var parts = saved.Split('|', 9);
        if (parts.Length < 9) return;

        // 模式与进制都要「解析成功 + 在合法区间内」才回填，否则维持默认，避免 IndexOutOfRange
        if (int.TryParse(parts[0], out int mode) && mode >= 0 && mode < _modes.Count) _modes[mode].IsChecked = true;
        _text.Text = parts[1];
        _initial.Text = parts[2];
        _increment.Text = parts[3];
        _repeat.Text = parts[4];
        _digits.Text = parts[5];
        if (int.TryParse(parts[6], out int fmt) && fmt >= 0 && fmt < _radix.Items.Count) _radix.SelectedIndex = fmt;
        // 布尔位存的是 1/0 文本，只有 "1" 算勾选
        _leadingZero.IsChecked = parts[7] == "1";
        _prefix.Text = parts[8];
    }
}
