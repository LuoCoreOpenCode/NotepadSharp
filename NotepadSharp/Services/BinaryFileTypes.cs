using System;
using System.Collections.Generic;
using System.IO;

namespace NotepadSharp.Services;

/// <summary>
/// ND2 的已知二进制后缀表（doctypelistview.cpp:21-22 HEX_FILE_EXTS_LIST）。
/// 打开这类文件时不询问，直接进十六进制视图（ccnotepad.cpp:5417）。
/// </summary>
public static class BinaryFileTypes
{
    /// <summary>
    /// ND2 已知二进制/需按十六进制打开的后缀集合，全部小写存储。
    /// 用 <see cref="StringComparer.Ordinal"/>（序数比较）复刻 ND2 的「大小写敏感」行为：
    /// 例如 PNG 与 png 视为不同，只有小写后缀才会命中。
    /// </summary>
    private static readonly HashSet<string> HexExtensions = new(StringComparer.Ordinal)
    {
        "exe", "dll", "png", "jpg", "doc", "docx", "ppt", "pptx",
        "zip", "gz", "rar", "pdf", "7z", "lib", "so", "db", "obj",
        "bmp", "ico", "qm", "icns", "jpeg", "res", "exp", "ilk", "deb",
    };

    /// <summary>ND2 直接比较后缀字符串，大小写敏感</summary>
    /// <param name="path">文件路径（可含目录，只取最后一段的扩展名参与判断）</param>
    /// <returns>该路径的后缀是否命中已知二进制表；无扩展名或大小写不符均返回 false</returns>
    public static bool IsHexExtension(string path) =>
        // GetExtension 返回带点的后缀（如 ".png"），TrimStart('.') 去点后与表内小写项做 Ordinal 比较
        HexExtensions.Contains(Path.GetExtension(path).TrimStart('.'));
}
