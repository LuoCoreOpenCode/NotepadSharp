using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace NotepadSharp.Services;

/// <summary>
/// 编码方案：以代码页 + 是否带 BOM 描述一种可选择的编码。
/// CodePage == 0 表示系统 ANSI。
/// </summary>
public sealed record EncodingOption(string Display, int CodePage, bool Bom);

/// <summary>
/// 文件编码检测、解码与转码服务
/// </summary>
public static class EncodingService
{
    public const int CpAnsi = 0;
    public const int CpUtf8 = 65001;
    public const int CpUtf16Le = 1200;
    public const int CpUtf16Be = 1201;
    public const int CpUtf32Le = 12000;
    public const int CpUtf32Be = 12001;
    public const int CpGb18030 = 54936;
    public const int CpGbk = 936;
    public const int CpBig5 = 950;
    public const int CpShiftJis = 932;
    public const int CpEucKr = 949;

    static EncodingService()
    {
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        }
        catch
        {
            // 平台不支持额外代码页时忽略
        }
    }

    // ==================== 菜单选项 ====================

    /// <summary>「编码-转换」列表：改变文档编码并写回时生效，屏幕内容不变</summary>
    public static IReadOnlyList<EncodingOption> ConvertOptions { get; } = new List<EncodingOption>
    {
        new("ANSI (系统默认)", CpAnsi, false),
        new("中文 [GBK / GB2312]", CpGbk, false),
        new("中文 [GB18030]", CpGb18030, false),
        new("中文 [BIG5]", CpBig5, false),
        new("日文 [Shift-JIS]", CpShiftJis, false),
        new("韩文 [EUC-KR]", CpEucKr, false),
        new("韩文 [JOHAB]", 1361, false),
        new("西里尔 [KOI8-R]", 20878, false),
        new("西里尔 [KOI8-U]", 20873, false),
        new("西里尔 [Windows-1251]", 1251, false),
        new("希腊 [Windows-1253]", 1253, false),
        new("土耳其 [Windows-1254]", 1254, false),
        new("越南 [Windows-1258]", 1258, false),
        new("泰国 [Windows-874]", 874, false),
        new("希伯来 [ISO-8859-8]", 28598, false),
        new("阿拉伯 [Windows-1256]", 1256, false),
        new("波罗的海 [Windows-1257]", 1257, false),
        new("西欧 [Windows-1252]", 1252, false),
        new("中欧 [Windows-1250]", 1250, false),
        new("欧洲 [ISO-8859-1]", 28591, false),
        new("欧洲 [ISO-8859-2]", 28592, false),
        new("欧洲 [ISO-8859-4]", 28594, false),
        new("欧洲 [ISO-8859-5]", 28595, false),
        new("欧洲 [ISO-8859-7]", 28597, false),
        new("欧洲 [ISO-8859-13]", 28603, false),
        new("欧洲 [ISO-8859-15]", 28605, false),
        new("OEM [850]", 850, false),
        new("Unicode - 65001 (UTF-8)", CpUtf8, false),
        new("Unicode - 65001 (UTF-8 含 BOM)", CpUtf8, true),
        new("Unicode - 1200 (UCS-2 LE BOM)", CpUtf16Le, true),
        new("Unicode - 1201 (UCS-2 BE BOM)", CpUtf16Be, true),
        new("Unicode - 12000 (UTF-32 LE BOM)", CpUtf32Le, true),
        new("Unicode - 12001 (UTF-32 BE BOM)", CpUtf32Be, true),
    };

    /// <summary>「编码-重新载入」列表：用指定编码重新解码磁盘内容，屏幕随之改变</summary>
    public static IReadOnlyList<EncodingOption> ReloadOptions { get; } = new List<EncodingOption>
    {
        new("以 ANSI 载入", CpAnsi, false),
        new("以 UTF-8 载入", CpUtf8, false),
        new("以 GB2312 载入", CpGbk, false),
        new("以 BIG5 载入", CpBig5, false),
        new("以KO8-R 载入", 20878, false),
        new("以UCS-2 little endian 载入", CpUtf16Le, true),
        new("以UCS-2 big endian 载入", CpUtf16Be, true),
    };

    public static Encoding GetEncodingByCodePage(int codePage)
    {
        try
        {
            return codePage switch
            {
                CpAnsi => Encoding.Default,
                CpUtf8 => new UTF8Encoding(false),
                CpUtf16Le => Encoding.Unicode,
                CpUtf16Be => Encoding.BigEndianUnicode,
                CpUtf32Le => new UTF32Encoding(false, false),
                CpUtf32Be => new UTF32Encoding(true, false),
                CpGb18030 => Encoding.GetEncoding("GB18030"),
                _ => Encoding.GetEncoding(codePage),
            };
        }
        catch
        {
            return Encoding.Default;
        }
    }

    // ==================== 二进制判定 ====================

    /// <summary>
    /// 是否属于无法作为文本编辑的内容（含 NUL、乱码比例过高）。
    /// 空文件不算二进制。
    /// </summary>
    public static bool LooksBinary(byte[] bytes)
    {
        if (bytes.Length == 0) return false;

        int sample = Math.Min(bytes.Length, 64 * 1024);
        int nul = 0, control = 0, printable = 0;

        for (int i = 0; i < sample; i++)
        {
            byte b = bytes[i];
            if (b == 0) nul++;
            else if (b < 0x09 || (b > 0x0D && b < 0x20)) control++;
            else printable++;
        }

        if (nul > 0) return true;
        if (sample >= 512 && control > printable) return true;
        return false;
    }

    // ==================== 检测 ====================

    /// <summary>探测文件编码（BOM → UTF-8 严格解码 → 中日韩启发式 → ANSI）</summary>
    public static (int CodePage, bool Bom) Detect(byte[] bytes)
    {
        if (bytes.Length == 0) return (CpUtf8, false);

        var bom = DetectBom(bytes);
        if (bom.HasValue) return bom.Value;

        int utf8Errors = CountStrictDecodeErrors(bytes, CpUtf8);
        if (utf8Errors == 0)
        {
            // 纯 ASCII 也按 UTF-8 无 BOM 处理，与 ndd 默认一致
            return (CpUtf8, false);
        }

        // 多字节候选按「解码后落在目标文字区间的比例」打分
        var candidates = new[] { CpGbk, CpBig5, CpShiftJis, CpEucKr };
        int bestCp = CpAnsi;
        double bestScore = 0;

        foreach (int cp in candidates)
        {
            int errors = CountStrictDecodeErrors(bytes, cp);
            if (errors > 0) continue;

            double score = ScriptScore(Decode(bytes, cp, false), cp);
            if (score > bestScore)
            {
                bestScore = score;
                bestCp = cp;
            }
        }

        if (bestCp != CpAnsi && bestScore > 0.02) return (bestCp, false);

        // 单字节西里尔/西欧：错误率低者优先
        foreach (int cp in new[] { 1251, 1253, 1254, 1256, 1257, 20878, 20873 })
        {
            if (CountStrictDecodeErrors(bytes, cp) == 0 && LooksLikeSingleByteScript(bytes, cp))
                return (cp, false);
        }

        return (CpAnsi, false);
    }

    private static (int, bool)? DetectBom(byte[] b)
    {
        if (b.Length >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF) return (CpUtf8, true);
        if (b.Length >= 4 && b[0] == 0x00 && b[1] == 0x00 && b[2] == 0xFE && b[3] == 0xFF) return (CpUtf32Be, true);
        if (b.Length >= 4 && b[0] == 0xFF && b[1] == 0xFE && b[2] == 0x00 && b[3] == 0x00) return (CpUtf32Le, true);
        if (b.Length >= 2 && b[0] == 0xFF && b[1] == 0xFE) return (CpUtf16Le, true);
        if (b.Length >= 2 && b[0] == 0xFE && b[1] == 0xFF) return (CpUtf16Be, true);
        if (b.Length >= 4 && b[0] == 0x2B && b[1] == 0x2F && b[2] == 0x76) return (65000, true);
        return null;
    }

    private static int CountStrictDecodeErrors(byte[] bytes, int codePage)
    {
        try
        {
            var strict = Encoding.GetEncoding(codePage,
                EncoderFallback.ReplacementFallback, DecoderFallback.ExceptionFallback);
            int contentStart = 0;
            // BOM 不参与严格性判断
            if (codePage == CpUtf8 && bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
                contentStart = 3;

            byte[] slice = contentStart == 0 ? bytes : bytes[contentStart..];
            strict.GetString(slice);
            return 0;
        }
        catch (DecoderFallbackException)
        {
            return 1;
        }
        catch (ArgumentException)
        {
            return int.MaxValue; // 代码页不可用
        }
    }

    /// <summary>解码后的文本有多少字符落在该编码应有的文字区间内</summary>
    private static double ScriptScore(string text, int codePage)
    {
        if (text.Length == 0) return 0;

        int hit = 0;
        foreach (char c in text)
        {
            switch (codePage)
            {
                case CpGbk: // CJK 统一表意文字 + 中文标点
                    if (c is >= '一' and <= '鿿' or >= '　' and <= '〿' or >= '＀' and <= '￯') hit++;
                    break;
                case CpBig5:
                    if (c is >= '一' and <= '鿿' or >= '　' and <= '〿') hit++;
                    break;
                case CpShiftJis: // 假名区
                    if (c is >= 'ぁ' and <= 'ヿ' or >= '一' and <= '鿿') hit++;
                    break;
                case CpEucKr: // 韩文音节
                    if (c is >= '가' and <= '힣') hit++;
                    break;
            }
        }
        return (double)hit / text.Length;
    }

    private static bool LooksLikeSingleByteScript(byte[] bytes, int codePage)
    {
        int relevant = 0;
        foreach (byte b in bytes)
        {
            if (b < 0x80) continue;
            relevant++;
            bool plausible = codePage switch
            {
                1251 or 1253 or 1254 or 1256 or 1257 => b >= 0xC0, // 扩展区多为大写起始
                20878 or 20873 => true,
                _ => false,
            };
            if (!plausible) return false;
        }
        return relevant > 0;
    }

    // ==================== 解码 / 编码 ====================

    /// <summary>按指定编码解码字节，自动剥离 BOM；非法字节用替代符而不是抛异常</summary>
    public static string Decode(byte[] bytes, int codePage, bool stripBom = true)
    {
        if (bytes.Length == 0) return string.Empty;

        var enc = GetEncodingByCodePage(codePage);
        int offset = 0;

        if (stripBom)
        {
            int bomLen = BomLength(codePage);
            if (bomLen > 0 && bytes.Length >= bomLen && HasBomBytes(bytes, codePage))
                offset = bomLen;
        }

        if (offset > 0)
            return enc.GetString(bytes, offset, bytes.Length - offset);

        return enc.GetString(bytes);
    }

    /// <summary>把文本按指定编码写成字节，按需加 BOM</summary>
    public static byte[] Encode(string text, int codePage, bool bom)
    {
        var enc = GetEncodingByCodePage(codePage);
        byte[] body = enc.GetBytes(text);

        byte[] preamble = bom ? GetPreamble(codePage) : Array.Empty<byte>();
        if (preamble.Length == 0) return body;

        var all = new byte[preamble.Length + body.Length];
        Buffer.BlockCopy(preamble, 0, all, 0, preamble.Length);
        Buffer.BlockCopy(body, 0, all, preamble.Length, body.Length);
        return all;
    }

    public static bool HasBomBytes(byte[] bytes, int codePage)
    {
        byte[] pre = GetPreamble(codePage);
        if (pre.Length == 0 || bytes.Length < pre.Length) return false;
        for (int i = 0; i < pre.Length; i++)
        {
            if (bytes[i] != pre[i]) return false;
        }
        return true;
    }

    public static byte[] GetPreamble(int codePage)
    {
        return codePage switch
        {
            CpUtf8 => new byte[] { 0xEF, 0xBB, 0xBF },
            CpUtf16Le => new byte[] { 0xFF, 0xFE },
            CpUtf16Be => new byte[] { 0xFE, 0xFF },
            CpUtf32Le => new byte[] { 0xFF, 0xFE, 0x00, 0x00 },
            CpUtf32Be => new byte[] { 0x00, 0x00, 0xFE, 0xFF },
            _ => Array.Empty<byte>(),
        };
    }

    private static int BomLength(int codePage) => GetPreamble(codePage).Length;

    /// <summary>状态栏/对话框显示名，对齐 ndd 的措辞</summary>
    public static string GetDisplayName(int codePage, bool bom)
    {
        string name = codePage switch
        {
            CpAnsi => "ANSI",
            CpUtf8 => "UTF-8",
            CpUtf16Le => "UCS-2 LE",
            CpUtf16Be => "UCS-2 BE",
            CpUtf32Le => "UTF-32 LE",
            CpUtf32Be => "UTF-32 BE",
            CpGb18030 => "GB18030",
            CpGbk => "GBK",
            CpBig5 => "BIG5",
            CpShiftJis => "Shift-JIS",
            CpEucKr => "EUC-KR",
            65000 => "UTF-7",
            20878 => "KOI8-R",
            20873 => "KOI8-U",
            1361 => "JOHAB",
            _ => "CP" + codePage,
        };

        if (codePage >= 1250 && codePage <= 1258) name = "Windows-" + codePage;
        else if (codePage >= 28591 && codePage <= 28605) name = "ISO-8859-" + (codePage - 28590);
        else if (codePage == 874) name = "TIS-620";
        else if (codePage == 850) name = "OEM-850";

        return bom && GetPreamble(codePage).Length > 0 ? name + "-BOM" : name;
    }

    // ==================== 文件读写 ====================

    public static void WriteFile(string filePath, string content, int codePage, bool bom)
    {
        File.WriteAllBytes(filePath, Encode(content, codePage, bom));
    }
}
