using System;
using System.Text;

namespace NotepadSharp.Services;

/// <summary>
/// 十六进制视图：把字节流渲染成「地址 | 十六进制 | ASCII」三栏文本。
/// 对应「文件-打开-二进制文件」入口。
/// </summary>
public static class HexDump
{
    private const int BytesPerLine = 16;

    public static string FromBytes(byte[] bytes, int maxBytes = 4 * 1024 * 1024)
    {
        int limit = Math.Min(bytes.Length, maxBytes);
        var sb = new StringBuilder(limit * 4 + 1024);

        for (int offset = 0; offset < limit; offset += BytesPerLine)
        {
            sb.Append(offset.ToString("X8")).Append("  ");

            int count = Math.Min(BytesPerLine, limit - offset);
            for (int i = 0; i < BytesPerLine; i++)
            {
                sb.Append(i < count ? bytes[offset + i].ToString("X2") : "  ");
                sb.Append(i == 7 ? "   " : " ");
            }

            sb.Append(' ');
            for (int i = 0; i < count; i++)
            {
                byte b = bytes[offset + i];
                sb.Append(b >= 0x20 && b < 0x7F ? (char)b : '.');
            }

            sb.Append('\n');
        }

        if (bytes.Length > limit)
        {
            sb.Append($"\n... 已截断，共 {bytes.Length} 字节，仅显示前 {limit} 字节\n");
        }

        return sb.ToString();
    }
}
