using System;
using System.Text;

namespace NotepadSharp.Services;

/// <summary>
/// 十六进制视图：把字节流渲染成「地址 | 十六进制 | ASCII」三栏文本。
/// 对应「文件-打开-二进制文件」入口；超大文件按 maxBytes 截断，避免一次性撑爆界面。
/// </summary>
public static class HexDump
{
    /// <summary>每行渲染的字节数（经典 16 字节/行布局，中间第 8 字节处再插一段分隔空格）</summary>
    private const int BytesPerLine = 16;

    /// <summary>
    /// 把字节流渲染成「地址 | 十六进制 | ASCII」三栏文本，每行 <see cref="BytesPerLine"/> 字节。
    /// ASCII 栏仅保留可打印字符，其余（控制字符、高位字节）用「.」占位。
    /// </summary>
    /// <param name="bytes">要渲染的字节</param>
    /// <param name="maxBytes">最多渲染的字节数，超出部分丢弃并在末尾追加截断提示（默认 4 MB）</param>
    /// <returns>多行十六进制视图文本，行间以 \n 分隔</returns>
    public static string FromBytes(byte[] bytes, int maxBytes = 4 * 1024 * 1024)
    {
        // 实际渲染上限：文件长度与 maxBytes 取小，防止超大文件把界面撑爆
        int limit = Math.Min(bytes.Length, maxBytes);
        var sb = new StringBuilder(limit * 4 + 1024);   // 预估容量：每字节约 4 字符输出 + 余量

        for (int offset = 0; offset < limit; offset += BytesPerLine)
        {
            // 第 1 栏：8 位十六进制的行地址（本行首字节偏移），后接两空格
            sb.Append(offset.ToString("X8")).Append("  ");

            // 第 2 栏：16 个字节的十六进制值；末行不足 16 个时用两空格占位，保持各列对齐
            int count = Math.Min(BytesPerLine, limit - offset);
            for (int i = 0; i < BytesPerLine; i++)
            {
                sb.Append(i < count ? bytes[offset + i].ToString("X2") : "  ");
                sb.Append(i == 7 ? "   " : " ");   // 第 8 个（i==7）之后用三空格，把每行拆成左右各 8 字节
            }

            // 第 3 栏：可打印 ASCII（0x20..0x7E）原样显示，其余用「.」代替
            sb.Append(' ');
            for (int i = 0; i < count; i++)
            {
                byte b = bytes[offset + i];
                sb.Append(b >= 0x20 && b < 0x7F ? (char)b : '.');
            }

            sb.Append('\n');
        }

        // 被 maxBytes 截断时追加一行提示，告知总长度与实际显示长度
        if (bytes.Length > limit)
        {
            sb.Append($"\n... 已截断，共 {bytes.Length} 字节，仅显示前 {limit} 字节\n");
        }

        return sb.ToString();
    }
}
