using System.Windows.Input;

namespace NotepadSharp.Services;

/// <summary>停靠主题 XAML 里绑定的命令（运行时解析的 XAML 只能引用 public static 成员）。</summary>
public static class VsDockCommands
{
    public static readonly RoutedCommand CloseFloatingView = new("CloseFloatingView", typeof(VsDockCommands));
}
