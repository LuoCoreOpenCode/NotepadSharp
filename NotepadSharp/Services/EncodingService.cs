using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace NotepadSharp.Services;

/// <summary>
/// 编码方案：以代码页 + 是否带 BOM 描述一种可选择的编码。
/// CodePage == 0 表示系统 ANSI。
/// </summary>
/// <param name="Display">在「编码」菜单/对话框里显示的名称</param>
/// <param name="CodePage">代码页数值，0 表示系统 ANSI（真实取 GetACP）</param>
/// <param name="Bom">是否携带 BOM（true 时写回会拼上该编码的 BOM 前缀）</param>
public sealed record EncodingOption(string Display, int CodePage, bool Bom);

/// <summary>
/// 文件编码检测、解码与转码服务
/// </summary>
public static class EncodingService
{
    /// <summary>代码页 0：占位值，代表「系统 ANSI」，真实代码页由 GetACP() 决定（见 <see cref="SystemAnsiCodePage"/>）</summary>
    public const int CpAnsi = 0;
    /// <summary>代码页 65001：UTF-8，变长 1-4 字节；纯 ASCII 文本是其子集，与 ANSI 单字节区表现一致</summary>
    public const int CpUtf8 = 65001;
    /// <summary>代码页 1200：UTF-16 LE。.NET 的 char 即一个 UTF-16 码元（2 字节小端序），与 UTF-8 的字节口径不同</summary>
    public const int CpUtf16Le = 1200;
    /// <summary>代码页 1201：UTF-16 BE（2 字节大端序）</summary>
    public const int CpUtf16Be = 1201;
    /// <summary>代码页 12000：UTF-32 LE（每个码点固定 4 字节小端序）</summary>
    public const int CpUtf32Le = 12000;
    /// <summary>代码页 12001：UTF-32 BE（每个码点固定 4 字节大端序）</summary>
    public const int CpUtf32Be = 12001;
    /// <summary>代码页 54936：GB18030，GBK 的超集，变长 1/2/4 字节，是国标强制的完整字符集</summary>
    public const int CpGb18030 = 54936;
    /// <summary>代码页 936：GBK / GB2312 兼容编码，中文 Windows 的 ANSI 通常就是它；变长 1/2 字节</summary>
    public const int CpGbk = 936;
    /// <summary>代码页 950：BIG5，繁体中文，变长 1/2 字节</summary>
    public const int CpBig5 = 950;
    /// <summary>代码页 932：Shift-JIS，日文，变长 1/2 字节</summary>
    public const int CpShiftJis = 932;
    /// <summary>代码页 949：EUC-KR，韩文，变长 1/2 字节</summary>
    public const int CpEucKr = 949;

    /// <summary>
    /// 类型初始化器：注册 CodePagesEncodingProvider，让 .NET 能按代码页取到 GBK / BIG5 / Shift-JIS 等
    /// 传统编码。CoreCLR 默认只内置 UTF-* 与 Latin1，不注册的话 Encoding.GetEncoding(936) 会直接抛。
    /// </summary>
    static EncodingService()
    {
        try
        {
            // 一次性把 Windows-125x / ISO-8859 / 中日韩等代码页挂进 Encoding 查找表
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        }
        catch
        {
            // 平台不支持额外代码页时忽略
            // 注意：即便注册了 Provider，个别代码页（如 KOI8-R 20878）在部分平台仍会抛 NotSupportedException，
            // 调用方需各自兜底（见 CountStrictDecodeErrors 的 catch (NotSupportedException)）。
        }
    }

    /// <summary>
    /// 系统 ANSI 代码页。.NET 的 Encoding.Default 是 UTF-8，不能拿来表示 ANSI；
    /// ND2 在 Windows 上的 ANSI 就是 GetACP()（中文系统 936）。
    /// </summary>
    public static int SystemAnsiCodePage { get; } = DetectSystemAnsiCodePage();

    /// <summary>
    /// 取系统 ANSI 代码页：调用 Win32 GetACP()；取不到（如非 Windows 平台抛异常）时回落到 1252（Windows 西欧）。
    /// </summary>
    /// <returns>系统 ANSI 对应的代码页数值</returns>
    private static int DetectSystemAnsiCodePage()
    {
        try { return (int)GetACP(); }   // 中文系统返回 936、日文 932、西欧 1252 等
        catch { return 1252; }          // 拿不到时的安全默认值
    }

    /// <summary>
    /// Win32 kernel32 的 GetACP()：返回当前区域设置的 ANSI 代码页（Active Code Page）。
    /// </summary>
    /// <returns>ANSI 代码页数值（无符号整型）</returns>
    [DllImport("kernel32.dll")]
    private static extern uint GetACP();

    // ==================== 菜单选项 ====================

    /// <summary>
    /// 「编码-转换」列表：改变文档编码并写回时生效，屏幕内容不变。
    /// 每项 <see cref="EncodingOption"/> 三个字段依次为：显示名、代码页、是否带 BOM。
    /// </summary>
    public static IReadOnlyList<EncodingOption> ConvertOptions { get; } = new List<EncodingOption>
    {
        new("ANSI (系统默认)", CpAnsi, false),
        new("中文 [GBK / GB2312]", CpGbk, false),           // 936
        new("中文 [GB18030]", CpGb18030, false),            // 54936，GBK 超集
        new("中文 [BIG5]", CpBig5, false),                  // 950 繁体
        new("日文 [Shift-JIS]", CpShiftJis, false),         // 932
        new("韩文 [EUC-KR]", CpEucKr, false),               // 949
        new("韩文 [JOHAB]", 1361, false),                   // 1361 JOHAB，部分平台不可用
        new("西里尔 [KOI8-R]", 20878, false),               // 20878：即便注册 Provider 也可能抛，需兜底
        new("西里尔 [KOI8-U]", 20873, false),               // 20873
        new("西里尔 [Windows-1251]", 1251, false),
        new("希腊 [Windows-1253]", 1253, false),
        new("土耳其 [Windows-1254]", 1254, false),
        new("越南 [Windows-1258]", 1258, false),
        new("泰国 [Windows-874]", 874, false),              // 874 即 TIS-620
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
        new("OEM [850]", 850, false),                       // DOS 西欧码页
        new("Unicode - 65001 (UTF-8)", CpUtf8, false),
        new("Unicode - 65001 (UTF-8 含 BOM)", CpUtf8, true),        // Bom=true，写回时拼 EF BB BF
        new("Unicode - 1200 (UCS-2 LE BOM)", CpUtf16Le, true),      // UTF-16 LE，ND2 文案称 UCS-2
        new("Unicode - 1201 (UCS-2 BE BOM)", CpUtf16Be, true),      // UTF-16 BE
        new("Unicode - 12000 (UTF-32 LE BOM)", CpUtf32Le, true),    // UTF-32 LE
        new("Unicode - 12001 (UTF-32 BE BOM)", CpUtf32Be, true),    // UTF-32 BE
    };

    /// <summary>「编码-重新载入」列表：用指定编码重新解码磁盘内容，屏幕随之改变</summary>
    public static IReadOnlyList<EncodingOption> ReloadOptions { get; } = new List<EncodingOption>
    {
        new("以 ANSI 载入", CpAnsi, false),
        new("以 UTF-8 载入", CpUtf8, false),
        new("以 GB2312 载入", CpGbk, false),                        // ND2 文案写 GB2312，实际用 936(GBK)
        new("以 BIG5 载入", CpBig5, false),
        new("以KO8-R 载入", 20878, false),                           // ND2 原文拼写如此（应为 KOI8-R）
        new("以UCS-2 little endian 载入", CpUtf16Le, true),          // UTF-16 LE
        new("以UCS-2 big endian 载入", CpUtf16Be, true),             // UTF-16 BE
    };

    /// <summary>
    /// 按代码页取 <see cref="Encoding"/> 实例。CpAnsi(0) 会映射到系统 ANSI 真实代码页；
    /// 取不到（代码页不可用或平台不支持）时回落到 <see cref="Encoding.Default"/>（.NET 里即 UTF-8）。
    /// </summary>
    /// <param name="codePage">代码页数值，0 表示系统 ANSI</param>
    /// <returns>可用于解码/编码的 Encoding 实例，保证非 null</returns>
    public static Encoding GetEncodingByCodePage(int codePage)
    {
        try
        {
            return codePage switch
            {
                CpAnsi => Encoding.GetEncoding(SystemAnsiCodePage),   // 0 → GetACP() 的真实代码页
                CpUtf8 => new UTF8Encoding(false),                    // false=不输出 BOM；解码仍会吞掉已有 BOM
                CpUtf16Le => Encoding.Unicode,                        // 内置 UTF-16 LE
                CpUtf16Be => Encoding.BigEndianUnicode,               // 内置 UTF-16 BE
                CpUtf32Le => new UTF32Encoding(false, false),         // 参数：(isBigEndian=false, getPreamble=false)
                CpUtf32Be => new UTF32Encoding(true, false),          // 大端 UTF-32，不带前缀
                CpGb18030 => Encoding.GetEncoding("GB18030"),         // 用别名取，规避个别环境 54936 未登记
                _ => Encoding.GetEncoding(codePage),                  // 其余走 Provider 注册的代码页表
            };
        }
        catch
        {
            return Encoding.Default;   // 兜底：任何异常退到 UTF-8，保证返回非 null
        }
    }

    // ==================== 检测 ====================

    /// <summary>
    /// 探测文件编码，多级回退：BOM → UTF-8 严格解码 → 中日韩多字节启发式打分 → 单字节西里尔/西欧 → ANSI。
    /// 复刻 ND2 的编码嗅探顺序。
    /// </summary>
    /// <param name="bytes">文件原始字节（可能含 BOM）</param>
    /// <returns>元组：CodePage 为探测到的代码页（0=系统 ANSI）；Bom 为是否检测到 BOM</returns>
    public static (int CodePage, bool Bom) Detect(byte[] bytes)
    {
        // 空文件无从判断，默认按 UTF-8 无 BOM（ND2 同处理）
        if (bytes.Length == 0) return (CpUtf8, false);

        // 第一级：先看有没有 BOM，命中即据此定编码，无需再猜
        var bom = DetectBom(bytes);
        if (bom.HasValue) return bom.Value;

        // 第二级：UTF-8 严格解码。靠 DecoderFallback.ExceptionFallback 把非法字节序列变成异常，
        // 0 错误即整段是合法 UTF-8；纯 ASCII 是 UTF-8 子集，也在这里返回
        int utf8Errors = CountStrictDecodeErrors(bytes, CpUtf8);
        if (utf8Errors == 0)
        {
            // 纯 ASCII 也按 UTF-8 无 BOM 处理
            return (CpUtf8, false);
        }

        // 多字节候选按「解码后落在目标文字区间的比例」打分
        // 只有能严格解码（errors==0）的候选才参与评分，避免非法字节污染统计
        var candidates = new[] { CpGbk, CpBig5, CpShiftJis, CpEucKr };
        int bestCp = CpAnsi;
        double bestScore = 0;

        foreach (int cp in candidates)
        {
            int errors = CountStrictDecodeErrors(bytes, cp);
            if (errors > 0) continue;   // 该编码连严格解码都过不了，直接淘汰

            // stripBom:false：字节已确认无 BOM，整段按候选编码解码后再统计字符落区
            double score = ScriptScore(Decode(bytes, cp, false), cp);
            if (score > bestScore)
            {
                bestScore = score;
                bestCp = cp;
            }
        }

        // 得分须同时满足「选出了非 ANSI 候选」且「命中率 > 2%」才采信，否则继续往下试单字节
        if (bestCp != CpAnsi && bestScore > 0.02) return (bestCp, false);

        // 单字节西里尔/西欧：错误率低者优先
        // 20878(KOI8-R) 在很多环境 GetEncoding 会抛，CountStrictDecodeErrors 返回 int.MaxValue 自然被排除
        foreach (int cp in new[] { 1251, 1253, 1254, 1256, 1257, 20878, 20873 })
        {
            if (CountStrictDecodeErrors(bytes, cp) == 0 && LooksLikeSingleByteScript(bytes, cp))
                return (cp, false);
        }

        // 全部落空：按系统 ANSI 处理
        return (CpAnsi, false);
    }

    /// <summary>
    /// 通过起始字节序列识别 BOM。UTF-32 LE 必须在 UTF-16 LE 之前判断：UTF-32 LE 的前两字节 FF FE
    /// 与 UTF-16 LE 的 BOM 完全相同，若先匹配 UTF-16 会把 UTF-32 文件误判。返回 null 表示无 BOM。
    /// </summary>
    /// <param name="b">文件字节</param>
    /// <returns>可空元组：命中时字段一为该 BOM 对应代码页、字段二恒为 true；未命中返回 null</returns>
    private static (int, bool)? DetectBom(byte[] b)
    {
        // EF BB BF → UTF-8
        if (b.Length >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF) return (CpUtf8, true);
        // 00 00 FE FF → UTF-32 BE（前两字节 00 00，与 UTF-16 BE 的 FE FF 不同，先后均可）
        if (b.Length >= 4 && b[0] == 0x00 && b[1] == 0x00 && b[2] == 0xFE && b[3] == 0xFF) return (CpUtf32Be, true);
        // FF FE 00 00 → UTF-32 LE，必须先于 FF FE 开头的 UTF-16 LE 判断，否则被误认成 UTF-16
        if (b.Length >= 4 && b[0] == 0xFF && b[1] == 0xFE && b[2] == 0x00 && b[3] == 0x00) return (CpUtf32Le, true);
        // FF FE → UTF-16 LE
        if (b.Length >= 2 && b[0] == 0xFF && b[1] == 0xFE) return (CpUtf16Le, true);
        // FE FF → UTF-16 BE
        if (b.Length >= 2 && b[0] == 0xFE && b[1] == 0xFF) return (CpUtf16Be, true);
        // 2B 2F 76 → UTF-7（代码页 65000）；注意此处判长 >=4 却只比对 3 个字节，恰好 3 字节的 UTF-7 头会漏判（疑似 bug）
        if (b.Length >= 4 && b[0] == 0x2B && b[1] == 0x2F && b[2] == 0x76) return (65000, true);
        return null;
    }

    /// <summary>
    /// 以「严格模式」试解码并判断是否出错：靠 DecoderFallback.ExceptionFallback 让非法字节序列抛
    /// DecoderFallbackException。返回 0 表示整段可严格解码，返回 1 表示存在非法字节，
    /// 返回 int.MaxValue 表示该代码页在本机根本取不到（不可用），调用方据此把它排除。
    /// </summary>
    /// <param name="bytes">待检测字节</param>
    /// <param name="codePage">代码页</param>
    /// <returns>0=无错误；1=有非法序列；int.MaxValue=代码页不可用</returns>
    private static int CountStrictDecodeErrors(byte[] bytes, int codePage)
    {
        try
        {
            // ExceptionFallback：遇非法字节立即抛异常，而不是悄悄替换成 U+FFFD
            var strict = Encoding.GetEncoding(codePage,
                EncoderFallback.ReplacementFallback, DecoderFallback.ExceptionFallback);
            int contentStart = 0;
            // BOM 不参与严格性判断
            // UTF-8 若带 EF BB BF，先跳过 3 字节 BOM 再检测正文体，避免把合法 BOM 误判成非法字节
            if (codePage == CpUtf8 && bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
                contentStart = 3;

            byte[] slice = contentStart == 0 ? bytes : bytes[contentStart..];
            strict.GetString(slice);   // 只要不抛，就说明整段可严格解码
            return 0;
        }
        catch (DecoderFallbackException)
        {
            return 1;   // 存在无法映射的字节序列
        }
        catch (ArgumentException)
        {
            return int.MaxValue; // 代码页不可用
        }
        catch (NotSupportedException)
        {
            // 例如 20878：CodePagesEncodingProvider 也不带，GetEncoding 直接抛
            return int.MaxValue;
        }
    }

    /// <summary>
    /// 解码后的文本有多少字符落在该编码应有的文字区间内。命中率＝命中字符数 / 总字符数，
    /// 用于在多个「能严格解码」的候选之间挑最像的那个。
    /// 注意这里按 UTF-16 码元（char）统计，与字节口径无关：落在基本多文种平面外的字符（如 GBK 四字节
    /// 生僻字）会被拆成一对代理项，单个代理项不满足区间判断，会略微拉低命中率，但不影响候选间相对排序。
    /// </summary>
    /// <param name="text">已按 codePage 解码出的文本</param>
    /// <param name="codePage">该文本据以解码的代码页</param>
    /// <returns>命中目标文字区间的字符比例，取值 0..1</returns>
    private static double ScriptScore(string text, int codePage)
    {
        if (text.Length == 0) return 0;

        int hit = 0;
        foreach (char c in text)
        {
            switch (codePage)
            {
                case CpGbk: // CJK 统一表意文字 + 中文标点
                    // 一..鿿=汉字(U+4E00–9FFF)，　..〿=CJK 标点(U+3000–303F)，＀..￯=全/半角形式(U+FF00–FFEF)
                    if (c is >= '一' and <= '鿿' or >= '　' and <= '〿' or >= '＀' and <= '￯') hit++;
                    break;
                case CpBig5:
                    // 繁体同样落在 CJK 汉字与标点区，但不统计全/半角形式段
                    if (c is >= '一' and <= '鿿' or >= '　' and <= '〿') hit++;
                    break;
                case CpShiftJis: // 假名区
                    // ぁ..ヿ=平/片假名(U+3041–30FF)，再并入 CJK 汉字区
                    if (c is >= 'ぁ' and <= 'ヿ' or >= '一' and <= '鿿') hit++;
                    break;
                case CpEucKr: // 韩文音节
                    // 가..힣=谚文音节(U+AC00–D7A3)
                    if (c is >= '가' and <= '힣') hit++;
                    break;
            }
        }
        return (double)hit / text.Length;   // 命中率：命中数 / 总码元数
    }

    /// <summary>
    /// 单字节编码的启发式：只看高位字节（&gt;=0x80），要求它们都「像」该编码的字母区。
    /// Windows-125x 的西里尔/希腊等字母集中在 0xC0 以上，故要求高位字节不低于 0xC0；
    /// KOI8-R/U 的西里尔可用字符分布较宽，故一律放过。只要有一个高位字节不合规律就判定不像并返回 false。
    /// </summary>
    /// <param name="bytes">待判字节</param>
    /// <param name="codePage">代码页</param>
    /// <returns>是否含高位字节、且全部符合该编码的字母分布规律</returns>
    private static bool LooksLikeSingleByteScript(byte[] bytes, int codePage)
    {
        int relevant = 0;   // 参与判断的高位（非 ASCII）字节数
        foreach (byte b in bytes)
        {
            if (b < 0x80) continue;   // ASCII 段任何单字节编码都兼容，跳过
            relevant++;
            bool plausible = codePage switch
            {
                1251 or 1253 or 1254 or 1256 or 1257 => b >= 0xC0, // 扩展区多为大写起始
                20878 or 20873 => true,                             // KOI8：不额外设限，直接放过
                _ => false,                                         // 未列出的代码页不在本启发式范围内
            };
            if (!plausible) return false;   // 一个字节不合规律即整体否定
        }
        return relevant > 0;   // 至少要有一个高位字节；全 ASCII 不该由单字节分支认领
    }

    // ==================== 解码 / 编码 ====================

    /// <summary>按指定编码解码字节，自动剥离 BOM；非法字节用替代符（U+FFFD）而不是抛异常</summary>
    /// <param name="bytes">待解码字节</param>
    /// <param name="codePage">代码页，0 表示系统 ANSI</param>
    /// <param name="stripBom">是否手动跳过 BOM（默认 true）；UTF-8/UTF-16 的 Encoding 自身也会吞 BOM，这里手动跳过是为统一口径</param>
    /// <returns>解码后的字符串</returns>
    public static string Decode(byte[] bytes, int codePage, bool stripBom = true)
    {
        if (bytes.Length == 0) return string.Empty;

        var enc = GetEncodingByCodePage(codePage);   // 宽松 Encoding：非法字节替换为 U+FFFD，不抛
        int offset = 0;

        // 手动定位 BOM：按该编码应有的前缀长度逐字节比对，命中则把 offset 越过 BOM
        if (stripBom)
        {
            int bomLen = BomLength(codePage);
            if (bomLen > 0 && bytes.Length >= bomLen && HasBomBytes(bytes, codePage))
                offset = bomLen;
        }

        // offset>0 时从 BOM 之后解码；否则等价整段解码
        if (offset > 0)
            return enc.GetString(bytes, offset, bytes.Length - offset);

        return enc.GetString(bytes);
    }

    /// <summary>
    /// 打开文件的解码结果；Garbled 对应 ND2 scanFileOutPut 的 existGrbledCode。
    /// 四个字段：Text=解出的文本，CodePage=最终采用的代码页，Bom=是否带 BOM，
    /// Garbled=true 表示严格解码失败（ND2 据此弹「打开为文本或二进制」）。
    /// </summary>
    public readonly record struct OpenText(string Text, int CodePage, bool Bom, bool Garbled);

    /// <summary>
    /// 打开文件时的整文件解码，照 ND2 scanFileOutPut(QFile&amp;)（CmpareMode.cpp:1106-1152）：
    /// 按检测到的编码解，解不出来且不是 GBK 就无条件改用 GBK 再解一次（跳过长度沿用原编码算出的值），
    /// 仍然解不出来才置 Garbled——ND2 就是拿它弹「打开为文本或二进制」的。
    /// </summary>
    /// <param name="bytes">文件字节</param>
    /// <param name="codePage">Detect 出来的初始代码页</param>
    /// <param name="bom">是否带 BOM（决定跳过的字节数）</param>
    /// <returns>解码结果 <see cref="OpenText"/>，其 Garbled 标记是否需要走乱码/二进制提示</returns>
    public static OpenText DecodeForOpen(byte[] bytes, int codePage, bool bom)
    {
        // 先算 BOM 占用的跳过字节数；若文件比 BOM 还短则不跳，避免负长度
        int skip = bom ? BomLength(codePage) : 0;
        if (skip > bytes.Length) skip = 0;

        // 第一枪：按检测到的编码严格解码，失败时 out 仍带回一份含替代符的文本
        bool ok = TryDecodeStrict(bytes, skip, codePage, out string text);

        // ND2 行为：解不出来且当前不是 GBK，就无条件改用 GBK(936) 再解一次
        // 注意 skip 沿用原编码算出的值（如原为 UTF-16 会跳 2 字节），ND2 正是如此，可能吃掉 GBK 正文首字节
        if (!ok && codePage != CpGbk)
        {
            codePage = CpGbk;   // 回落 936
            bom = false;        // GBK 无 BOM 概念，清掉标记
            ok = TryDecodeStrict(bytes, skip, CpGbk, out text);
        }

        // !ok 即两枪都解不干净 → 判为乱码 Garbled
        return new OpenText(text, codePage, bom, !ok);
    }

    /// <summary>严格解码；失败时也带回一份带替代符的文本（「以文本打开」显示的就是它）</summary>
    /// <param name="bytes">文件字节</param>
    /// <param name="offset">起始偏移（跳过 BOM 后的正文起点）</param>
    /// <param name="codePage">代码页，0 会先解析成系统 ANSI 真实代码页</param>
    /// <param name="text">输出：成功时为干净文本，失败时为含 U+FFFD 替代符的宽松文本</param>
    /// <returns>true=严格解码成功；false=存在非法序列（乱码）</returns>
    private static bool TryDecodeStrict(byte[] bytes, int offset, int codePage, out string text)
    {
        // 空正文（含纯 BOM 文件）视为成功，直接返回空串
        int count = bytes.Length - offset;
        if (count <= 0)
        {
            text = string.Empty;
            return true;
        }

        try
        {
            // 严格 Encoding（ExceptionFallback）；codePage 0 先经 ResolveCodePage 映射成真实 ANSI 代码页
            var strict = Encoding.GetEncoding(ResolveCodePage(codePage),
                EncoderFallback.ReplacementFallback, DecoderFallback.ExceptionFallback);
            text = strict.GetString(bytes, offset, count);
            return true;
        }
        catch (DecoderFallbackException)
        {
            // 严格失败：改用宽松 Encoding 产出「可读但含替代符」的文本，并标记为乱码
            text = GetEncodingByCodePage(codePage).GetString(bytes, offset, count);
            return false;
        }
        catch (ArgumentException)
        {
            // 代码页在本机不可用：按宽松解码显示，不判乱码
            text = GetEncodingByCodePage(codePage).GetString(bytes, offset, count);
            return true;
        }
    }

    /// <summary>把占位代码页 0（ANSI）翻译成真实系统代码页；其余原样返回，供 Encoding.GetEncoding 使用</summary>
    /// <param name="codePage">可能为 0 的代码页</param>
    /// <returns>可直接传给 Encoding.GetEncoding 的真实代码页</returns>
    private static int ResolveCodePage(int codePage) => codePage == CpAnsi ? SystemAnsiCodePage : codePage;

    /// <summary>把文本按指定编码写成字节，按需加 BOM</summary>
    /// <param name="text">要写出的文本</param>
    /// <param name="codePage">目标代码页</param>
    /// <param name="bom">是否在正文前拼接该编码的 BOM 前缀</param>
    /// <returns>编码后的字节数组</returns>
    public static byte[] Encode(string text, int codePage, bool bom)
    {
        var enc = GetEncodingByCodePage(codePage);
        byte[] body = enc.GetBytes(text);   // 先编出正文（enc 不带前缀，BOM 交由下面手动拼）

        // 仅当要求 BOM 且该编码确有前缀时才拼接；ANSI/GBK 等 GetPreamble 返回空数组
        byte[] preamble = bom ? GetPreamble(codePage) : Array.Empty<byte>();
        if (preamble.Length == 0) return body;

        // 手动拼装：前缀 + 正文
        var all = new byte[preamble.Length + body.Length];
        Buffer.BlockCopy(preamble, 0, all, 0, preamble.Length);
        Buffer.BlockCopy(body, 0, all, preamble.Length, body.Length);
        return all;
    }

    /// <summary>
    /// 逐字节比对文件开头与目标编码应有的 BOM 前缀，判断字节流是否以该 BOM 起始。
    /// </summary>
    /// <param name="bytes">文件字节</param>
    /// <param name="codePage">代码页</param>
    /// <returns>是否检测到与 GetPreamble 匹配的 BOM 前缀</returns>
    public static bool HasBomBytes(byte[] bytes, int codePage)
    {
        byte[] pre = GetPreamble(codePage);
        if (pre.Length == 0 || bytes.Length < pre.Length) return false;   // 该编码无 BOM，或文件比前缀还短
        for (int i = 0; i < pre.Length; i++)
        {
            if (bytes[i] != pre[i]) return false;   // 任一字节不符即否定
        }
        return true;
    }

    /// <summary>
    /// 返回各 Unicode 编码的 BOM 字节序列；ANSI、GBK 等非 Unicode 编码返回空数组（它们没有 BOM 概念）。
    /// </summary>
    /// <param name="codePage">代码页</param>
    /// <returns>BOM 前缀字节，无 BOM 时为空数组</returns>
    public static byte[] GetPreamble(int codePage)
    {
        return codePage switch
        {
            CpUtf8 => new byte[] { 0xEF, 0xBB, 0xBF },           // UTF-8 BOM
            CpUtf16Le => new byte[] { 0xFF, 0xFE },              // UTF-16 LE BOM
            CpUtf16Be => new byte[] { 0xFE, 0xFF },              // UTF-16 BE BOM
            CpUtf32Le => new byte[] { 0xFF, 0xFE, 0x00, 0x00 },  // UTF-32 LE：含尾随 00 00，靠长度与 UTF-16 LE 区分
            CpUtf32Be => new byte[] { 0x00, 0x00, 0xFE, 0xFF },  // UTF-32 BE
            _ => Array.Empty<byte>(),                            // 其余（ANSI/GBK/BIG5/…）无 BOM
        };
    }

    /// <summary>该编码 BOM 的字节数（无 BOM 返回 0），解码时据此跳过前缀</summary>
    /// <param name="codePage">代码页</param>
    /// <returns>BOM 长度（字节）</returns>
    private static int BomLength(int codePage) => GetPreamble(codePage).Length;

    /// <summary>状态栏/对话框显示名</summary>
    /// <param name="codePage">代码页</param>
    /// <param name="bom">是否带 BOM；带且该编码在 <see cref="GetPreamble"/> 中有前缀时，名称追加 -BOM</param>
    /// <returns>供界面展示的编码名称</returns>
    public static string GetDisplayName(int codePage, bool bom)
    {
        // 先对常用离散编码定名，未知代码页兜底显示为「CP+数字」
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

        // 再用区间规则覆盖成系列名：Windows-125x 与 ISO-8859-x 是连号代码页，按段命名更整齐
        // ISO-8859 用 codePage-28590 还原序号（28591→1、28598→8、28603→13、28605→15，均成立）
        if (codePage >= 1250 && codePage <= 1258) name = "Windows-" + codePage;
        else if (codePage >= 28591 && codePage <= 28605) name = "ISO-8859-" + (codePage - 28590);
        else if (codePage == 874) name = "TIS-620";
        else if (codePage == 850) name = "OEM-850";

        // 仅当确实有 BOM 前缀可拼时才加 -BOM，避免给 ANSI/GBK 等无 BOM 编码乱标
        return bom && GetPreamble(codePage).Length > 0 ? name + "-BOM" : name;
    }

    // ==================== 文件读写 ====================

    /// <summary>
    /// 把文本按指定编码（可选 BOM）编码后整文件写出。
    /// </summary>
    /// <param name="filePath">目标文件路径</param>
    /// <param name="content">要写入的文本</param>
    /// <param name="codePage">代码页</param>
    /// <param name="bom">是否写入 BOM</param>
    public static void WriteFile(string filePath, string content, int codePage, bool bom)
    {
        // Encode 负责转字节 + 拼 BOM，这里整块覆盖写盘
        File.WriteAllBytes(filePath, Encode(content, codePage, bom));
    }
}
