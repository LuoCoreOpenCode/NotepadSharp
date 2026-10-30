using System;
using System.Collections.Generic;
using System.Text;

namespace NotepadSharp.Services;

/// <summary>
/// ND2 的 URL 识别状态机，逐条照搬 scintillaeditview.cpp:3344-3600（没有正则，逐字符扫）。
/// 下标一律按 UTF-16 码元，和 ND2 的 QChar 索引一致；字节偏移由调用方逐段累加
/// （ND2 addHotSpot :3669 也是这么把码元段换算成 UTF-8 字节段的）。
/// </summary>
public static class UrlScanner
{
    /// <summary>QChar::digitValue：只有十进制数字返回 0..9，其余返回 -1。ND2 的两处 switch 全靠它，见下面注释</summary>
    /// <param name="c">待判定的字符</param>
    /// <returns>数字字符对应的 0..9；所有其他字符（含字母、标点、'\0'）一律 -1</returns>
    private static int DigitValue(char c) => c >= '0' && c <= '9' ? c - '0' : -1;

    /// <summary>越界读按 '\0' 算：ND2 的 text.data() 是 NUL 结尾的缓冲，越界拿到 0（:3594 的循环就靠这个收口）</summary>
    /// <param name="text">源文本</param>
    /// <param name="index">UTF-16 码元下标，允许越界（负数或末尾之外都视作 NUL）</param>
    /// <returns>界内返回该位置字符；界外返回 '\0'</returns>
    private static char Ch(string text, int index) => index >= 0 && index < text.Length ? text[index] : '\0';

    /// <summary>scheme 合法的组成字符：仅 ASCII 字母。注意数字不算——scheme 中途遇到数字会断掉识别（ND2 原样）</summary>
    /// <param name="c">待判定的字符</param>
    /// <returns>是大小写英文字母则 true</returns>
    public static bool IsUrlSchemeStartChar(char c)
        => (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z');

    /// <summary>紧贴 scheme 前面允许的分隔字符：非数字、非字母、非下划线（'\0' 也算），日常就是空格和行首</summary>
    /// <param name="c">待判定的字符</param>
    /// <returns>该字符不可能属于 scheme 则 true</returns>
    public static bool IsUrlSchemeDelimiter(char c)
        => !((c >= '0' && c <= '9') || (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || c == '_');

    /// <summary>
    /// 找 scheme（冒号结尾）。命中时 distance = 从 start 到 scheme 起点的跳数、schemeLength 含冒号；
    /// 没命中时 schemeLength = 0、distance = 扫到末尾的剩余长度。
    /// </summary>
    /// <param name="text">整段可视文本，下标按 UTF-16 码元</param>
    /// <param name="start">本次扫描的起点下标</param>
    /// <param name="distance">成功时为 start 到 scheme 起点的距离，失败时为扫到文本尾的距离</param>
    /// <param name="schemeLength">scheme 长度（含结尾冒号）；未命中为 0</param>
    /// <returns>是否找到了以冒号收尾的合法 scheme</returns>
    public static bool ScanToUrlStart(string text, int start, out int distance, out int schemeLength)
    {
        int textLen = text.Length;
        int p = start;      // 扫描游标
        int p0 = 0;         // 候选 scheme 的起点，命中冒号时才用
        bool inScheme = false; // 是否正处在一个候选 scheme 里

        while (p < textLen)
        {
            if (!inScheme)
            {
                // ND2 判的是「绝对下标 0」而不是「本段第一个字符」，所以可视区一滚动，
                // 正好落在段首的那条 URL 可能认不出来。它的原有行为，照抄不改。
                if (IsUrlSchemeStartChar(text[p]) && (p == 0 || IsUrlSchemeDelimiter(text[p - 1])))
                {
                    // 字母且前一个字符是分隔符（或在文本最开头）：记为候选 scheme 起点
                    p0 = p;
                    inScheme = true;
                }
            }
            else
            {
                if (text[p] == ':')
                {
                    // 冒号收口：scheme 成立，distance 是给调用方的「先跳过这么多非 URL 字符」
                    distance = p0 - start;
                    schemeLength = p - p0 + 1; // +1 把冒号本身算进去
                    return true;
                }
                // scheme 内只允许字母：数字或别的字符一出现就退出候选态，
                // 游标继续往后找下一个「分隔符后的字母」（p0 留着无所谓，重入口会覆盖）
                if (!IsUrlSchemeStartChar(text[p])) inScheme = false;
            }
            p++;
        }

        // 扫到文本尾也没等到冒号：整段没有 scheme，distance = 剩余全部长度供调用方一步跳过
        schemeLength = 0;
        distance = p - start;
        return false;
    }

    /// <summary>
    /// URL 正文允许的字符。ND2 这里写的是 <c>switch (c.digitValue())</c>，
    /// 而 digitValue 只对十进制数字返回 0..9，下面这些 case（34/35/60/62/123/125/63/127）永远命中不了
    /// ——于是 " # &lt; &gt; { } ? 其实没被排除，实际等价于「大于空格」。照它的行为复刻，不顺手修正。
    /// </summary>
    /// <param name="c">URL 正文里待检的字符</param>
    /// <returns>永远等价于「c 大于空格」；空格与控制字符（含越界读来的 '\0'）为 false</returns>
    public static bool IsUrlTextChar(char c)
    {
        // 唯一的实际判定点：空格、制表、换行等 c <= ' ' 一律终止 URL 正文
        if (c <= ' ') return false;
        // 下面这个 switch 按 ND2 原样保留：case 值是字符字面量（34/35/60/62/123/125/63/127），
        // 而 DigitValue 只会返回 -1..9，两边永远对不上，等于没有排除任何字符
        switch (DigitValue(c))
        {
            case '"':
            case '#':
            case '<':
            case '>':
            case '{':
            case '}':
            case '?':
            case 0x7F:
                return false;
        }
        return true;
    }

    /// <summary>
    /// 同上，ND2 的 <c>switch (c.digitValue())</c> 让 &amp; + = ; 一个都不算分隔符，恒为 false；
    /// 连带 sQueryAfterDelimiter / sQueryQuotes / sQueryAfterQuotes 三个态在实际运行里到不了。
    /// </summary>
    /// <param name="c">query 段里待检的字符</param>
    /// <returns>实际恒为 false：&amp; + = ; 全都检不出来，query 分隔符形同虚设</returns>
    public static bool IsUrlQueryDelimiter(char c)
    {
        // 与 IsUrlTextChar 同源的 ND2 写法：case 是字符字面量（38/43/61/59），
        // DigitValue 只可能返回 -1..9，任何 case 都命中不了，直接落到 return false
        switch (DigitValue(c))
        {
            case '&':
            case '+':
            case '=':
            case ';':
                return true;
        }
        return false;
    }

    /// <summary>从 scheme 之后开始扫 URL 正文，distance = 扫到的长度（可能为 0）</summary>
    /// <param name="text">整段可视文本，下标按 UTF-16 码元</param>
    /// <param name="start">正文起点（即 scheme 冒号的下一位）</param>
    /// <param name="distance">URL 正文在此结束，含起始点的相对长度；非法字符处截断时为 0 也可能</param>
    public static void ScanToUrlEnd(string text, int start, out int distance)
    {
        int textLen = text.Length;
        int p = start;
        char q = '\0'; // 期望的闭 delimiter：进引号态时记下与之配对的那个字符
        // sHostAndPath → sQuery → sQueryAfterDelimiter → sQueryQuotes → sQueryAfterQuotes → sFragment
        // 六个态的编号照抄 ND2 状态机；实际可达的只有 0、1、5 三个（分隔符态进不去，原因见 IsUrlQueryDelimiter）
        const int sHostAndPath = 0, sQuery = 1, sQueryAfterDelimiter = 2,
                  sQueryQuotes = 3, sQueryAfterQuotes = 4, sFragment = 5; // 主机路径 / query 开头 / query 分隔符后 / 引号内 / 引号闭合后 / 锚点片段
        int s = sHostAndPath;

        while (p < textLen)
        {
            char c = text[p];
            switch (s)
            {
                case sHostAndPath:
                    // 主机与路径段：? 进 query、# 进锚点，其余靠 IsUrlTextChar 兜底（等价于遇空格即止）
                    if (c == '?') s = sQuery;
                    else if (c == '#') s = sFragment;
                    else if (!IsUrlTextChar(c)) { distance = p - start; return; }
                    break;

                case sQuery:
                    // query 段：# 可切入锚点；理论上 & + = ; 会转入分隔符态，但实际检不到，见上文
                    if (c == '#') s = sFragment;
                    else if (IsUrlQueryDelimiter(c)) s = sQueryAfterDelimiter;
                    else if (!IsUrlTextChar(c)) { distance = p - start; return; }
                    break;

                case sQueryAfterDelimiter:
                    // 以下三个态在当前复刻口径下运行到不了，保留只为与 ND2 结构对齐：
                    // 分隔符后若出现引号/括号，记下配对的闭合字符再进引号态
                    if (c == '\'' || c == '"' || c == '`') { q = c; s = sQueryQuotes; }
                    else if (c == '(') { q = ')'; s = sQueryQuotes; }
                    else if (c == '[') { q = ']'; s = sQueryQuotes; }
                    else if (c == '{') { q = '}'; s = sQueryQuotes; }
                    else if (IsUrlTextChar(c)) s = sQuery; // 普通字符：回到 query 继续
                    else { distance = p - start; return; } // 控制字符：截断
                    break;

                case sQueryQuotes:
                    // 引号态：一路吞到闭合字符；空格以下（含 NUL）直接截断，URL 不带控制字符
                    if (c < ' ') { distance = p - start; return; }
                    if (c == q) s = sQueryAfterQuotes;
                    break;

                case sQueryAfterQuotes:
                    // 闭合后又见分隔符则再来一轮引号，否则到此为止
                    if (IsUrlQueryDelimiter(c)) s = sQueryAfterDelimiter;
                    else { distance = p - start; return; }
                    break;

                case sFragment:
                    // 锚点段：没有特殊规则，只看正文合法性
                    if (!IsUrlTextChar(c)) { distance = p - start; return; }
                    break;
            }
            p++;
        }

        // 一路扫到文本尾都没截断：整段剩余都算 URL 正文
        distance = p - start;
    }

    /// <summary>
    /// 去掉一个多余的尾字符，去掉一个就返回 true（ND2 是 while 一直剥到剥不动为止）。
    /// 单个字符表 ".,:;?!#"；成对的 )] 只有在整段里配平的情况下才剥。
    /// </summary>
    /// <param name="text">整段可视文本</param>
    /// <param name="start">URL 段起点（码元下标）</param>
    /// <param name="length">URL 段长度；剥成功时被就地改为 length-1</param>
    /// <returns>本次是否剥掉了一个尾字符</returns>
    public static bool RemoveUnwantedTrailingCharFromUrl(string text, int start, ref int length)
    {
        // l 是尾字符下标；长度不足 2 没有可剥的对象（也是 while 调用方的收口之一）
        int l = length - 1;
        if (l <= 0) return false;

        // 单字符表：尾号命中任意一个就直接剥，不讲任何配平道理
        const string singleChars = ".,:;?!#";
        for (int i = 0; i < singleChars.Length; i++)
        {
            if (Ch(text, start + l) == singleChars[i])
            {
                length = l;
                return true;
            }
        }

        // 成对括号表：尾号是 ) 或 ] 时，还要检查段内是否配平，配得起来才敢剥
        const string closingParenthesis = ")]";
        const string openingParenthesis = "([";
        for (int i = 0; i < closingParenthesis.Length; i++)
        {
            if (Ch(text, start + l) != closingParenthesis[i]) continue;

            // 从尾字符前一位往回扫做配平检查：闭合符计数 +1；遇到开口符，
            // 有账就销掉一笔（它俩成对），无账说明这个开口符唯一对应的是尾字符本身，
            // 剥掉尾字符就把 URL 剥瘸了，判不配平、保留尾字符
            int count = 0;
            for (int j = l - 1; j >= 0; j--)
            {
                if (Ch(text, start + j) == closingParenthesis[i]) count++;
                if (Ch(text, start + j) == openingParenthesis[i])
                {
                    if (count > 0) count--;
                    else return false;
                }
            }
            // 扫完还有富余闭合符：段内本来就有落单的 )/]，尾号可能是正文的一部分，不剥
            if (count != 0) return false;

            // 配平成立：整个 URL 就是被这对括号包住的，尾号是标点，剥掉
            length = l;
            return true;
        }

        return false;
    }
    /// <summary>QUrl 对 http/https 的要求：必须有主机名。ND2 用 url.isValid() 卡这一条（:3583）</summary>
    /// <param name="text">候选 URL 全文（含 scheme）</param>
    /// <returns>能被解析成 http/https 绝对地址且 Host 非空则 true</returns>
    public static bool IsValidUrl(string text)
    {
        // 三道门：能被 Uri 解析 / 是绝对地址 / 限定 http 与 https 两种 scheme
        if (!System.Uri.TryCreate(text, UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return false;
        // mailto:foo@bar 这类没主机名的地址在这里被拦下，与 ND2 isValid() 口径一致
        return uri.Host.Length > 0;
    }

    /// <summary>
    /// 判断 start 处是不是一条 URL。是 → segmentLen = URL 长度；不是 → segmentLen = 可以放心跳过的长度
    /// （调用方据此推进游标，所以对「非 URL」也总有正数，循环不会卡住）。
    /// </summary>
    /// <param name="text">整段可视文本，下标按 UTF-16 码元</param>
    /// <param name="start">本次判断的起点</param>
    /// <param name="segmentLen">本段步进长度：是 URL 时为剥完尾字符的 URL 全长，否则为可安全跳过的非 URL 长度</param>
    /// <returns>start 处（或其 scheme 之后）是否构成一条可点开的 http(s) URL</returns>
    public static bool IsUrl(string text, int start, out int segmentLen)
    {
        int textLen = text.Length;

        if (ScanToUrlStart(text, start, out int dist, out int schemeLen))
        {
            if (dist != 0)
            {
                // scheme 前面还有一段杂文：这段肯定不是 URL，返回可跳过的距离让调用方推进
                segmentLen = dist;
                return false;
            }

            // scheme 正好顶在 start 上：继续扫正文，len 是冒号之后属于 URL 的码元数
            ScanToUrlEnd(text, start + schemeLen, out int len);
            if (len != 0)
            {
                len += schemeLen; // 凑成含 scheme 的完整 URL 长度
                string urlStr = text.Substring(start, len);
                // 只认 http:// 和 https://，别的 scheme（含 mailto:）一律不算热点
                if ((urlStr.StartsWith("http://") || urlStr.StartsWith("https://")) && IsValidUrl(urlStr))
                {
                    // 逐次剥尾标点：每调用一次最多剥一个字符，while 剥到剥不动为止，
                    // 把句子结尾的 "." 或右括号这类误吞排除掉
                    while (RemoveUnwantedTrailingCharFromUrl(text, start, ref len)) { }
                    segmentLen = len;
                    return true;
                }
            }

            // 不是 http(s)：只跳过这一段长得像 scheme 的开头（ND2 :3592-3596，越界读到 NUL 自然收口）
            // len 从 1 起步：首字符已被确认是 scheme 字母；撞上冒号、数字或文本尾就停
            len = 1;
            int lMax = textLen - start;
            while (IsUrlSchemeStartChar(Ch(text, start + len)) && len < lMax) len++;
            segmentLen = len;
            return false;
        }

        // 全文找不到合法 scheme：dist 是扫到文本尾的距离，整段一次性跳过
        segmentLen = dist;
        return false;
    }

    /// <summary>
    /// 一段文本切成 ND2 那个循环的步进序列：每项 = (码元起点, 码元长度, 是否 URL)。
    /// addHotSpot 靠它把可视区逐段刷/清 indicator，抽出来纯粹是为了能让状态机单独跑一遍。
    /// </summary>
    /// <param name="text">要切分的整段文本（码元下标）</param>
    /// <returns>步进序列 (码元起点, 码元长度, 是否 URL)，首尾相接无缝覆盖整段</returns>
    public static List<(int Start, int Length, bool IsUrl)> ScanSegments(string text)
    {
        var segments = new List<(int, int, bool)>();
        int startWide = 0; // 当前游标；Wide 指宽字符/码元坐标，不是字节
        while (true)
        {
            // IsUrl 保证：非 URL 段也返回正数步进量，因此这里只需防御性检查 <= 0 收口
            bool r = IsUrl(text, startWide, out int lenWide);
            if (lenWide <= 0) break;
            segments.Add((startWide, lenWide, r));
            startWide += lenWide;
            if (startWide >= text.Length) break;
        }
        return segments;
    }

    /// <summary>码元段对应的 UTF-8 字节数（ND2 :3669 的 toUtf8().size()）</summary>
    /// <param name="text">整段文本</param>
    /// <param name="start">段的 UTF-16 码元起点（不是字节起点！）</param>
    /// <param name="length">段的码元长度</param>
    /// <returns>该段子串编码成 UTF-8 后的字节数；调用方累加它即可把码元坐标换算成字节坐标</returns>
    public static int Utf8Bytes(string text, int start, int length)
        // 只数长度不造子串：AsSpan 零拷贝切段后直接统计字节数
        => Encoding.UTF8.GetByteCount(text.AsSpan(start, length));
}
