using System.Windows.Input;

namespace NotepadSharp.Services;

/// <summary>停靠主题 XAML 里绑定的命令（运行时解析的 XAML 只能引用 public static 成员）。</summary>
public static class VsDockCommands
{
    /// <summary>
    /// 浮动窗标题栏「关闭」按钮绑定的命令。
    /// 构造第二个参数是命令的归属类型（owner type，用于标识命令属于哪个类并参与 WPF 的属主校验），
    /// 真正决定谁响应的是路由：MainWindow 用 RegisterClassCommandBinding(typeof(FloatingWindow))
    /// 给所有浮动窗按类型挂上统一处理，这样 ✕ 点击能先走「未保存确认 + 是否被锁定」再真正关闭，
    /// 而不是让停靠库自己把窗直接干掉。
    /// 第一个参数与成员名一致（"CloseFloatingView"），XAML 侧靠 {x:Static app:VsDockCommands.CloseFloatingView} 引用到它。
    /// </summary>
    public static readonly RoutedCommand CloseFloatingView = new("CloseFloatingView", typeof(VsDockCommands));
}
