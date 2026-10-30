using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Application = System.Windows.Application;
using Brush = System.Windows.Media.Brush;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using ComboBox = System.Windows.Controls.ComboBox;
using GroupBox = System.Windows.Controls.GroupBox;
using Orientation = System.Windows.Controls.Orientation;

namespace NotepadSharp.Views;

/// <summary>
/// ND2「选项 → 文本和字体」页里的 Tab Setting 分栏（texteditsetwin.ui / texteditsetwin.cpp）。
/// 长度对应 QSpinBox 的 0-16、出厂 4；「使用空格替换tab」绑 s_noUseTab，ND2 的出厂种子是 0（默认不勾，仍打真 TAB）。
/// ND2 在这一页析构时才落值（TextEditSetWin::save 只在 ~TextEditSetWin 里调用），所以这里是关窗才生效，不做实时预览。
/// 只有「确定」一个按钮：点确定还是直接点标题栏关闭，都走 Closed → apply 回调，语义与 ND2「析构即 save」一致，
/// 也就是说这个窗口没有「放弃修改」的路径——要撤销改动得手动把值调回去再关窗（没设 IsCancel，按 Esc 不会关窗）。
/// 生效范围由宿主的 apply 决定（遍历所有已打开标签页的编辑器 + 落盘），本窗口不直接碰编辑器。
/// </summary>
public sealed class TabSettingsWindow : Window
{
    /// <summary>Tab 长度下拉框：项是 0–16 的整数（装箱存着，取出时强转回 int），对应 ND2 的 spinBoxTabLens 范围</summary>
    private readonly ComboBox _length = new() { Width = 90 };
    /// <summary>「使用空格替换tab」勾选框，对应 ND2 的 checkBoxReplaceTabToSpace / 设置键 tabnouse</summary>
    private readonly CheckBox _replaceTab = new() { Content = "使用空格替换tab" };

    /// <summary>
    /// 构建 Tab 设置窗口：用外部传入的当前值回填控件，关窗时通过回调把新值交回宿主。
    /// 窗口由宿主以非模态 Show 打开，构造本身不写任何设置，也不订阅编辑器。
    /// </summary>
    /// <param name="tabLength">打开时的 Tab 长度（设置里的 TabWidth，源自键 tablens）；落在 0–16 之外时按 ND2 行为回落到 4</param>
    /// <param name="replaceTabWithSpace">打开时「使用空格替换tab」的勾选状态（设置里的 ReplaceTabWithSpace，源自键 tabnouse）</param>
    /// <param name="apply">关窗回调，参数依次为新的 Tab 长度与新的替换开关；由宿主负责应用到所有编辑器并落盘</param>
    public TabSettingsWindow(int tabLength, bool replaceTabWithSpace, Action<int, bool> apply)
    {
        // 窗口形态：高度自适应、固定宽度、不可缩放、相对主窗居中，背景取主题面板画刷
        Title = "Tab 设置";
        SizeToContent = SizeToContent.Height;
        Width = 320;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Application.Current.Resources["PanelBackground"] as Brush;

        // 0–16 逐个数塞成下拉项：ND2 用的是 QSpinBox，这里换成组合框但取值区间保持一样
        for (int i = 0; i <= 16; i++) _length.Items.Add(i);
        // ND2 只在 s_tabLens 落在 0-16 时才回填 spinbox，否则保持默认 4
        _length.SelectedItem = tabLength is >= 0 and <= 16 ? tabLength : 4;
        // 勾选状态原样回填；前景色取主题文字画刷，深色主题下才不会是黑字
        _replaceTab.IsChecked = replaceTabWithSpace;
        _replaceTab.Foreground = Application.Current.Resources["TextBrush"] as Brush;

        // 分栏外观照 ND2 那一页：GroupBox 里竖排放「Tab 字符长度」一行 + 替换勾选框一行
        var group = new GroupBox { Header = "Tab 设置", Margin = new Thickness(0, 0, 0, 8) };
        var inner = new StackPanel { Margin = new Thickness(6) };
        var lengthRow = new StackPanel { Orientation = Orientation.Horizontal };
        var label = new TextBlock
        {
            Text = "Tab 字符长度",
            VerticalAlignment = System.Windows.VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0),
            Foreground = Application.Current.Resources["TextBrush"] as Brush,
        };
        lengthRow.Children.Add(label);
        lengthRow.Children.Add(_length);
        inner.Children.Add(lengthRow);
        inner.Children.Add(_replaceTab);
        group.Content = inner;

        // 唯一的按钮：IsDefault 让回车等效于点它；点击只是关窗，不在这里做校验或落值
        var ok = new Button
        {
            Content = "确定",
            Width = 75,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
            IsDefault = true,
        };
        ok.Click += (_, _) => Close();

        // 竖排：分栏在上、确定按钮右下对齐
        var root = new StackPanel { Margin = new Thickness(12) };
        root.Children.Add(group);
        root.Children.Add(ok);
        Content = root;

        // 关窗才生效（对应 ND2 析构里的 save()）：无论走「确定」还是直接关窗都触发一次回调。
        // 强转 int 安全——_length.SelectedItem 只可能是上面装进去的 0–16 装箱整数
        Closed += (_, _) => apply((int)_length.SelectedItem, _replaceTab.IsChecked == true);
    }
}
