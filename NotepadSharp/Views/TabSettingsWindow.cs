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
/// </summary>
public sealed class TabSettingsWindow : Window
{
    private readonly ComboBox _length = new() { Width = 90 };
    private readonly CheckBox _replaceTab = new() { Content = "使用空格替换tab" };

    public TabSettingsWindow(int tabLength, bool replaceTabWithSpace, Action<int, bool> apply)
    {
        Title = "Tab 设置";
        SizeToContent = SizeToContent.Height;
        Width = 320;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Application.Current.Resources["PanelBackground"] as Brush;

        for (int i = 0; i <= 16; i++) _length.Items.Add(i);
        // ND2 只在 s_tabLens 落在 0-16 时才回填 spinbox，否则保持默认 4
        _length.SelectedItem = tabLength is >= 0 and <= 16 ? tabLength : 4;
        _replaceTab.IsChecked = replaceTabWithSpace;
        _replaceTab.Foreground = Application.Current.Resources["TextBrush"] as Brush;

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

        var ok = new Button
        {
            Content = "确定",
            Width = 75,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
            IsDefault = true,
        };
        ok.Click += (_, _) => Close();

        var root = new StackPanel { Margin = new Thickness(12) };
        root.Children.Add(group);
        root.Children.Add(ok);
        Content = root;

        Closed += (_, _) => apply((int)_length.SelectedItem, _replaceTab.IsChecked == true);
    }
}
