using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Application = System.Windows.Application;
using Brush = System.Windows.Media.Brush;
using Button = System.Windows.Controls.Button;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Orientation = System.Windows.Controls.Orientation;

namespace NotepadSharp.Views;

/// <summary>
/// 「打开为文本或二进制」三选一弹窗的返回值：用户点哪个按钮就是哪个值，没有勾对/未选之类的中间态。
/// </summary>
public enum OpenTextOrHex
{
    /// <summary>以二进制（十六进制只读视图）打开：对应弹窗的第一个按钮，也是默认按钮（回车即选它）</summary>
    Hex,
    /// <summary>仍然以文本打开：即使解码出乱码也按检测到的编码灌进编辑器，即「明知可能乱码也要看」</summary>
    Text,
    /// <summary>放弃本次打开：既不进 hex 也不建文档。点「取消」、按 Esc 或直接关窗都算这一项</summary>
    Cancel,
}

/// <summary>
/// ND2 filemanager.cpp:549-570 的「打开为文本或二进制？[检测存在乱码]」三选一弹窗。
/// QMessageBox 的按钮依次是 以二进制打开 / 以文本打开 / 取消，第一个是默认按钮。
/// 这里逐条对齐：按钮顺序与文案照抄，「以二进制打开」用 IsDefault 吃回车，「取消」用 IsCancel 吃 Esc，
/// 因此默认按钮 / Esc / 点右上角关闭三条路径的返回值语义都和 ND2 一致（后两条都落在 Cancel）。
/// 触发时机由宿主决定：只在正常打开文件、整篇按检测编码解不出（判据是乱码而非 NUL 数量）时弹；
/// 指定编码重载、启动恢复会话、已知二进制后缀直进 hex 这些场景都不该走到这里。
/// </summary>
public sealed class OpenTextOrHexWindow : Window
{
    /// <summary>用户的最终选择；构造即预置为 Cancel，所以任何「没点前两个按钮」的退出路径都是取消</summary>
    private OpenTextOrHex _choice = OpenTextOrHex.Cancel;

    /// <summary>
    /// 构建弹窗：一行说明文字 + 右下三个按钮（二进制 / 文本 / 取消）。
    /// 私有构造——外部只能走 <see cref="Ask"/>，保证一定是模态显示且拿到的是闭合后的 _choice。
    /// </summary>
    /// <param name="filePath">要展示在提示语里的文件路径；只做文案展示，本窗口不碰磁盘</param>
    private OpenTextOrHexWindow(string filePath)
    {
        // 窗口形态：标题照 ND2 原文（含「[检测存在乱码]」后缀）、定宽 + 高度自适应、不可缩放、不占任务栏、相对主窗居中
        Title = "打开为文本或二进制？[检测存在乱码]";
        Width = 480;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        Background = Application.Current.Resources["PanelBackground"] as Brush;
        var foreground = Application.Current.Resources["TextBrush"] as Brush;

        // 提示语：路径直接插进句子，长路径靠自动换行兜住（480 宽 + Wrap，不会把按钮挤出可视区）
        var message = new TextBlock
        {
            Text = $"文件 {filePath} 可能是二进制格式，你想以二进制（只读）格式打开文件吗？",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(16, 16, 16, 12),
            Foreground = foreground,
        };

        // 三个按钮：顺序与 ND2 的 QMessageBox 一致；hex 是默认按钮（回车），cancel 是取消按钮（Esc）。
        // 前两个各自记住选择再关窗；cancel 不挂 Click，靠 _choice 的预置值 Cancel 直接关窗
        var hex = new Button { Content = "以二进制打开", Width = 96, Margin = new Thickness(4), IsDefault = true };
        var text = new Button { Content = "以文本打开", Width = 96, Margin = new Thickness(4) };
        var cancel = new Button { Content = "取消", Width = 96, Margin = new Thickness(4), IsCancel = true };
        hex.Click += (_, _) => { _choice = OpenTextOrHex.Hex; Close(); };
        text.Click += (_, _) => { _choice = OpenTextOrHex.Text; Close(); };

        // 按钮条靠 DockPanel 钉在底部靠右，剩余空间整块给提示文字
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(12, 0, 12, 12),
        };
        buttons.Children.Add(hex);
        buttons.Children.Add(text);
        buttons.Children.Add(cancel);

        // 先加按钮条再加提示文字：DockPanel 按加入顺序结算，LastChildFill 让最后加入的子元素吃掉剩余区域
        var root = new DockPanel();
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(buttons);
        root.Children.Add(message);
        Content = root;
    }

    /// <summary>
    /// 以模态方式弹出三选一询问，拿到用户选择后返回。
    /// 返回值语义：<see cref="OpenTextOrHex.Hex"/> 让宿主转去开 hex 只读视图；
    /// <see cref="OpenTextOrHex.Text"/> 让宿主忽略乱码继续按文本打开；
    /// <see cref="OpenTextOrHex.Cancel"/> 让宿主直接放弃这次打开。
    /// </summary>
    /// <param name="owner">主窗口，用来做居中基准与模态归属；尚未显示时退化成无主窗口居中弹</param>
    /// <param name="filePath">提示语里展示的文件路径</param>
    /// <returns>用户点的按钮对应的 <see cref="OpenTextOrHex"/> 值；未显式选前两项时为 Cancel</returns>
    public static OpenTextOrHex Ask(Window owner, string filePath)
    {
        var dialog = new OpenTextOrHexWindow(filePath);
        // 命令行带文件启动时主窗口还没 Show，WPF 不允许给未显示的窗口设 Owner（会抛异常）；
        // 这种情况退化成无主窗口居中弹，ShowDialog 照样是模态
        if (owner.IsVisible) dialog.Owner = owner;
        dialog.ShowDialog();
        // ShowDialog 返回后窗口已闭合，按钮的 Click 早已把 _choice 写好；这里只读结果
        return dialog._choice;
    }
}
