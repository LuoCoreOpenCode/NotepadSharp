using System.Windows;
using System.Windows.Controls;
using Application = System.Windows.Application;
using TextBox = System.Windows.Controls.TextBox;
using Button = System.Windows.Controls.Button;
using Orientation = System.Windows.Controls.Orientation;
using HorizontalAlignment = System.Windows.HorizontalAlignment;

namespace NotepadSharp.Controls;

/// <summary>
/// 简易输入对话框
/// 纯代码搭出来的单行输入弹窗（标题 + 提示文字 + 输入框 + 确定/取消），
/// 没有 XAML 文件，配色直接取当前主题的 <c>PanelBackground</c> / <c>TextBrush</c> 资源。
/// 「转到行」「新建」「重命名」这类一次性小输入都走它，不单独做窗口。
/// </summary>
public static class Prompt
{
    /// <summary>
    /// 弹出一个模态输入框。回车和「确定」等价，Esc 和「取消」等价（后者返回 null，调用方据此放弃操作）。
    /// </summary>
    /// <param name="owner">父窗口，对话框相对它居中并跟随它的层级</param>
    /// <param name="title">标题栏文字</param>
    /// <param name="message">输入框上方的提示文字</param>
    /// <param name="defaultValue">输入框的初始内容，也是「还没任何回调写回」时 result 的初值</param>
    /// <returns>确定/回车时返回输入框里的文本（可能是空串）；取消或关闭窗口返回 null</returns>
    public static string? ShowInputDialog(Window owner, string title, string message, string defaultValue = "")
    {
        // 闭包变量：按钮/回车回调把文本写进来，ShowDialog 返回后在外面读。
        // 拿 defaultValue 作初值只是让它在回调之前就是合法状态；实际只有回调触发过（DialogResult 才会是 true）
        // 这条读取路径才走得到
        string? result = defaultValue;

        // 对话框外壳：固定尺寸、不可缩放、不进任务栏、相对父窗口居中
        var dialog = new Window
        {
            Title = title,
            Width = 320,
            Height = 150,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            Owner = owner,
            Background = Application.Current.Resources["PanelBackground"] as System.Windows.Media.Brush,   // 主题资源取色，缺失时为 null（回落到默认底色）
        };

        // 单行输入框，初始内容即默认值
        var inputBox = new TextBox
        {
            Margin = new Thickness(12, 4, 12, 4),
            Padding = new Thickness(4, 2, 4, 2),
            Text = defaultValue,
        };

        // 确定按钮：IsDefault=true → 回车等效于点它
        var okButton = new Button
        {
            Content = "确定",
            Width = 70,
            Margin = new Thickness(4),
            IsDefault = true,
        };

        // 取消按钮：IsCancel=true → Esc 等效于点它，且会自动把 DialogResult 置为 false
        var cancelButton = new Button
        {
            Content = "取消",
            Width = 70,
            Margin = new Thickness(4),
            IsCancel = true,
        };

        // 点确定：先收文本，再置 DialogResult=true 结束模态循环（顺序不能反，赋值后对话框已关）
        okButton.Click += (_, _) =>
        {
            result = inputBox.Text;
            dialog.DialogResult = true;
        };

        // 回车这条路径在输入框上显式接管：焦点在 TextBox 里时按键先到这里，
        // 收尾后标成已处理，窗口的 IsDefault 就不会再点一次「确定」造成重复赋值
        inputBox.KeyDown += (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.Enter)
            {
                result = inputBox.Text;
                dialog.DialogResult = true;
                e.Handled = true;
            }
        };

        // 竖排容器：提示文字 → 输入框 → 按钮行
        var stack = new StackPanel();
        stack.Children.Add(new TextBlock
        {
            Text = message,
            Margin = new Thickness(12, 12, 12, 4),
            Foreground = Application.Current.Resources["TextBrush"] as System.Windows.Media.Brush,   // 正文色同样取主题资源
        });
        stack.Children.Add(inputBox);

        // 按钮行：横排并整体靠右，符合 Windows 习惯
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 8, 12, 8),
        };
        buttons.Children.Add(okButton);
        buttons.Children.Add(cancelButton);
        stack.Children.Add(buttons);

        dialog.Content = stack;
        inputBox.Focus();   // 打开即把焦点放进输入框，用户不用先点一下再打字

        // 模态阻塞到这里返回：true=确定/回车，false 或 null（直接关窗口）都算取消
        if (dialog.ShowDialog() == true)
            return result;
        return null;
    }
}
