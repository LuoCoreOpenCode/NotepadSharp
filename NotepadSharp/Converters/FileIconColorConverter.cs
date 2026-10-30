using System;
using System.Globalization;
using System.IO;
using System.Windows.Data;
using System.Windows.Media;
using MediaColor = System.Windows.Media.Color;
using NotepadSharp.Models;

namespace NotepadSharp.Converters;

/// <summary>
/// 文件树图标着色：根据文件扩展名返回不同颜色的画刷
/// ——两种用法共用一个转换器：文件树的节点模板直接绑 <see cref="FileNode"/> 对象，
/// 停靠标签头的图标绑文件路径字符串；颜色取的是常见语言/格式的辨识色，
/// 目的是「一眼看出这是什么类型的文件」，不追求与任何主题的图标一致。
/// 单向转换器：只读绑定，不参与回写（见 <see cref="ConvertBack"/>）。
/// </summary>
public class FileIconColorConverter : IValueConverter
{
    /// <summary>
    /// 兜底画刷（中性灰）：未知扩展名、无扩展名、纯文本与日志文件，
    /// 以及传入值既不是 <see cref="FileNode"/> 也不是字符串时使用。
    /// 静态只读 + 已冻结，可跨线程共享，重复绑定不再有分配开销。
    /// </summary>
    private static readonly SolidColorBrush DefaultBrush = Freeze(MediaColor.FromRgb(0x9A, 0xA0, 0xA6));
    /// <summary>
    /// 目录画刷（琥珀黄）：<see cref="FileNode.IsDirectory"/> 为 true 时优先返回，不再看扩展名。
    /// </summary>
    private static readonly SolidColorBrush FolderBrush = Freeze(MediaColor.FromRgb(0xE8, 0xA3, 0x3D));

    /// <summary>
    /// 把绑定值换算成图标画刷：先看是不是目录（是就直接给目录色），否则取扩展名（转小写）查表匹配。
    /// </summary>
    /// <param name="value">绑定的源值：<see cref="FileNode"/>（文件树节点）或文件路径 <see cref="string"/>（标签头图标）；其它类型走兜底色</param>
    /// <param name="targetType">目标类型，此处恒为画刷；本转换器不依据它做分支</param>
    /// <param name="parameter">XAML 里传入的可选参数；当前实现不使用</param>
    /// <param name="culture">区域信息；扩展名比较固定用不变区域，故不依据它变化</param>
    /// <returns>图标要用的 <see cref="SolidColorBrush"/>；未命中任何规则时为 <see cref="DefaultBrush"/></returns>
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        // path/isDir 是两种入参形态归一化后的结果：字符串入参只能给路径，目录标志保持 false
        string path;
        bool isDir = false;

        // 文件树：节点自带目录标志，路径与标志一起取
        if (value is FileNode node)
        {
            path = node.FullPath;
            isDir = node.IsDirectory;
        }
        // 标签头：只绑了路径字符串，一律按文件对待
        else if (value is string s)
        {
            path = s;
        }
        // x:Null、未建好的绑定等异常取值：兜底色，绝不抛给绑定引擎
        else
        {
            return DefaultBrush;
        }

        // 目录优先于扩展名：同名扩展（如 foo.cs 目录）也显示目录色
        if (isDir)
            return FolderBrush;

        // 取扩展名并小写归一，.CS 与 .cs 命中同一条规则；无扩展名时为空串，落到默认分支
        var ext = Path.GetExtension(path).ToLowerInvariant();

        // 扩展名→颜色的对照表（多个同族扩展名合并到一条），每行现造一个冻结画刷
        return ext switch
        {
            ".cs" => Freeze(MediaColor.FromRgb(0x68, 0x21, 0x8A)),                                        // C#：品牌紫
            ".c" or ".h" or ".cpp" or ".cc" or ".cxx" or ".hpp" or ".hxx" or ".hh" => Freeze(MediaColor.FromRgb(0x00, 0x59, 0x8C)),
                                                                                                          // C/C++ 全系（含各种头文件后缀）：深蓝
            ".py" or ".pyw" => Freeze(MediaColor.FromRgb(0x35, 0x76, 0x9E)),                              // Python（.pyw 为无控制台窗口版）：蓝
            ".js" or ".mjs" or ".cjs" or ".jsx" => Freeze(MediaColor.FromRgb(0xE8, 0xB3, 0x39)),          // JavaScript 各模块格式与 JSX：黄
            ".ts" or ".tsx" => Freeze(MediaColor.FromRgb(0x2F, 0x74, 0xC6)),                              // TypeScript 与 TSX：蓝
            ".html" or ".htm" => Freeze(MediaColor.FromRgb(0xE4, 0x4D, 0x26)),                            // 网页标记：橙红
            ".css" or ".scss" or ".less" => Freeze(MediaColor.FromRgb(0x29, 0x6F, 0xB8)),                 // 样式表及其预处理器：蓝
            ".json" => Freeze(MediaColor.FromRgb(0xA8, 0x8B, 0x00)),                                       // JSON 数据：暗黄
            ".xml" or ".xaml" or ".svg" or ".csproj" => Freeze(MediaColor.FromRgb(0x4C, 0xA6, 0x4C)),     // XML 家族（含工程文件与矢量图）：绿
            ".md" or ".markdown" => Freeze(MediaColor.FromRgb(0x1B, 0x6A, 0xC4)),                         // Markdown 文档：蓝
            ".sql" => Freeze(MediaColor.FromRgb(0xCC, 0x7A, 0x00)),                                        // 数据库脚本：橙
            ".sh" or ".bash" or ".bat" or ".cmd" or ".ps1" => Freeze(MediaColor.FromRgb(0x3E, 0x7A, 0x3E)),
                                                                                                          // 各平台脚本（类 Unix / 批处理 / PowerShell）：墨绿
            ".go" => Freeze(MediaColor.FromRgb(0x00, 0xAD, 0xD8)),                                        // Go：青
            ".rs" => Freeze(MediaColor.FromRgb(0xB7, 0x4A, 0x26)),                                        // Rust：铁锈红
            ".java" => Freeze(MediaColor.FromRgb(0xB0, 0x72, 0x1A)),                                      // Java：赭黄
            ".yaml" or ".yml" or ".toml" or ".ini" => Freeze(MediaColor.FromRgb(0x8E, 0x6A, 0xB0)),       // 配置类文件：淡紫
            ".txt" or ".log" => DefaultBrush,                                                             // 纯文本与日志：刻意用中性色，不抢注意力
            _ => DefaultBrush,                                                                            // 未知/无扩展名：兜底中性灰
        };
    }

    /// <summary>
    /// 反向换算：颜色无法还原成节点或路径，本转换器只支持单向绑定，故一律抛出不支持。
    /// 绑定模式为 OneWay（模板里只做图标着色），正常情况下框架不会调用到这里。
    /// </summary>
    /// <param name="value">目标侧的值；未使用</param>
    /// <param name="targetType">要换算成的源类型；未使用</param>
    /// <param name="parameter">XAML 可选参数；未使用</param>
    /// <param name="culture">区域信息；未使用</param>
    /// <returns>永不返回；调用即抛 <see cref="NotSupportedException"/></returns>
    /// <exception cref="NotSupportedException">总是抛出：着色是不可逆的显示换算</exception>
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();

    /// <summary>
    /// 造一个已冻结的画刷：调用 <c>Freeze</c> 后对象不可变，WPF 可跨线程共享并跳过变更跟踪，
    /// 因此兜底/目录画刷存进静态字段复用，反复绑定也不再产生额外开销。
    /// </summary>
    /// <param name="c">画刷颜色（<see cref="MediaColor"/> 是 <c>System.Windows.Media.Color</c> 的别名，
    /// 用以区别同名的 <c>System.Drawing.Color</c>）</param>
    /// <returns>冻结后的 <see cref="SolidColorBrush"/>，可安全长期持有与共享</returns>
    private static SolidColorBrush Freeze(MediaColor c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }
}
