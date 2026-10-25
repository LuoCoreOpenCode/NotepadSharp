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
/// </summary>
public class FileIconColorConverter : IValueConverter
{
    private static readonly SolidColorBrush DefaultBrush = Freeze(MediaColor.FromRgb(0x9A, 0xA0, 0xA6));
    private static readonly SolidColorBrush FolderBrush = Freeze(MediaColor.FromRgb(0xE8, 0xA3, 0x3D));

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        string path;
        bool isDir = false;

        if (value is FileNode node)
        {
            path = node.FullPath;
            isDir = node.IsDirectory;
        }
        else if (value is string s)
        {
            path = s;
        }
        else
        {
            return DefaultBrush;
        }

        if (isDir)
            return FolderBrush;

        var ext = Path.GetExtension(path).ToLowerInvariant();

        return ext switch
        {
            ".cs" => Freeze(MediaColor.FromRgb(0x68, 0x21, 0x8A)),
            ".c" or ".h" or ".cpp" or ".cc" or ".cxx" or ".hpp" or ".hxx" or ".hh" => Freeze(MediaColor.FromRgb(0x00, 0x59, 0x8C)),
            ".py" or ".pyw" => Freeze(MediaColor.FromRgb(0x35, 0x76, 0x9E)),
            ".js" or ".mjs" or ".cjs" or ".jsx" => Freeze(MediaColor.FromRgb(0xE8, 0xB3, 0x39)),
            ".ts" or ".tsx" => Freeze(MediaColor.FromRgb(0x2F, 0x74, 0xC6)),
            ".html" or ".htm" => Freeze(MediaColor.FromRgb(0xE4, 0x4D, 0x26)),
            ".css" or ".scss" or ".less" => Freeze(MediaColor.FromRgb(0x29, 0x6F, 0xB8)),
            ".json" => Freeze(MediaColor.FromRgb(0xA8, 0x8B, 0x00)),
            ".xml" or ".xaml" or ".svg" or ".csproj" => Freeze(MediaColor.FromRgb(0x4C, 0xA6, 0x4C)),
            ".md" or ".markdown" => Freeze(MediaColor.FromRgb(0x1B, 0x6A, 0xC4)),
            ".sql" => Freeze(MediaColor.FromRgb(0xCC, 0x7A, 0x00)),
            ".sh" or ".bash" or ".bat" or ".cmd" or ".ps1" => Freeze(MediaColor.FromRgb(0x3E, 0x7A, 0x3E)),
            ".go" => Freeze(MediaColor.FromRgb(0x00, 0xAD, 0xD8)),
            ".rs" => Freeze(MediaColor.FromRgb(0xB7, 0x4A, 0x26)),
            ".java" => Freeze(MediaColor.FromRgb(0xB0, 0x72, 0x1A)),
            ".yaml" or ".yml" or ".toml" or ".ini" => Freeze(MediaColor.FromRgb(0x8E, 0x6A, 0xB0)),
            ".txt" or ".log" => DefaultBrush,
            _ => DefaultBrush,
        };
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();

    private static SolidColorBrush Freeze(MediaColor c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }
}
