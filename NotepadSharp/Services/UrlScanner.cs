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
    private static int DigitValue(char c) => c >= '0' && c <= '9' ? c - '0' : -1;

    /// <summary>越界读按 '\0' 算：ND2 的 text.data() 是 NUL 结尾的缓冲，越界拿到 0（:3594 的循环就靠这个收口）</summary>
    private static char Ch(string text, int index) => index >= 0 && index < text.Length ? text[index] : '\0';

    public static bool IsUrlSchemeStartChar(char c)
        => (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z');

    public static bool IsUrlSchemeDelimiter(char c)
        => !((c >= '0' && c <= '9') || (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || c == '_');

    /// <summary>
    /// 找 scheme（冒号结尾）。命中时 distance = 从 start 到 scheme 起点的跳数、schemeLength 含冒号；
    /// 没命中时 schemeLength = 0、distance = 扫到末尾的剩余长度。
    /// </summary>
    public static bool ScanToUrlStart(string text, int start, out int distance, out int schemeLength)
    {
        int textLen = text.Length;
        int p = start;
        int p0 = 0;
        bool inScheme = false;

        while (p < textLen)
        {
            if (!inScheme)
            {
                // ND2 判的是「绝对下标 0」而不是「本段第一个字符」，所以可视区一滚动，
                // 正好落在段首的那条 URL 可能认不出来。它的原有行为，照抄不改。
                if (IsUrlSchemeStartChar(text[p]) && (p == 0 || IsUrlSchemeDelimiter(text[p - 1])))
                {
                    p0 = p;
                    inScheme = true;
                }
            }
            else
            {
                if (text[p] == ':')
                {
                    distance = p0 - start;
                    schemeLength = p - p0 + 1;
                    return true;
                }
                if (!IsUrlSchemeStartChar(text[p])) inScheme = false;
            }
            p++;
        }

        schemeLength = 0;
        distance = p - start;
        return false;
    }

    /// <summary>
    /// URL 正文允许的字符。ND2 这里写的是 <c>switch (c.digitValue())</c>，
    /// 而 digitValue 只对十进制数字返回 0..9，下面这些 case（34/35/60/62/123/125/63/127）永远命中不了
    /// ——于是 " # &lt; &gt; { } ? 其实没被排除，实际等价于「大于空格」。照它的行为复刻，不顺手修正。
    /// </summary>
    public static bool IsUrlTextChar(char c)
    {
        if (c <= ' ') return false;
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
    public static bool IsUrlQueryDelimiter(char c)
    {
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
    public static void ScanToUrlEnd(string text, int start, out int distance)
    {
        int textLen = text.Length;
        int p = start;
        char q = '\0';
        // sHostAndPath → sQuery → sQueryAfterDelimiter → sQueryQuotes → sQueryAfterQuotes → sFragment
        const int sHostAndPath = 0, sQuery = 1, sQueryAfterDelimiter = 2,
                  sQueryQuotes = 3, sQueryAfterQuotes = 4, sFragment = 5;
        int s = sHostAndPath;

        while (p < textLen)
        {
            char c = text[p];
            switch (s)
            {
                case sHostAndPath:
                    if (c == '?') s = sQuery;
                    else if (c == '#') s = sFragment;
                    else if (!IsUrlTextChar(c)) { distance = p - start; return; }
                    break;

                case sQuery:
                    if (c == '#') s = sFragment;
                    else if (IsUrlQueryDelimiter(c)) s = sQueryAfterDelimiter;
                    else if (!IsUrlTextChar(c)) { distance = p - start; return; }
                    break;

                case sQueryAfterDelimiter:
                    if (c == '\'' || c == '"' || c == '`') { q = c; s = sQueryQuotes; }
                    else if (c == '(') { q = ')'; s = sQueryQuotes; }
                    else if (c == '[') { q = ']'; s = sQueryQuotes; }
                    else if (c == '{') { q = '}'; s = sQueryQuotes; }
                    else if (IsUrlTextChar(c)) s = sQuery;
                    else { distance = p - start; return; }
                    break;

                case sQueryQuotes:
                    if (c < ' ') { distance = p - start; return; }
                    if (c == q) s = sQueryAfterQuotes;
                    break;

                case sQueryAfterQuotes:
                    if (IsUrlQueryDelimiter(c)) s = sQueryAfterDelimiter;
                    else { distance = p - start; return; }
                    break;

                case sFragment:
                    if (!IsUrlTextChar(c)) { distance = p - start; return; }
                    break;
            }
            p++;
        }

        distance = p - start;
    }

    /// <summary>
    /// 去掉一个多余的尾字符，去掉一个就返回 true（ND2 是 while 一直剥到剥不动为止）。
    /// 单个字符表 ".,:;?!#"；成对的 )] 只有在整段里配平的情况下才剥。
    /// </summary>
    public static bool RemoveUnwantedTrailingCharFromUrl(string text, int start, ref int length)
    {
        int l = length - 1;
        if (l <= 0) return false;

        const string singleChars = ".,:;?!#";
        for (int i = 0; i < singleChars.Length; i++)
        {
            if (Ch(text, start + l) == singleChars[i])
            {
                length = l;
                return true;
            }
        }

        const string closingParenthesis = ")]";
        const string openingParenthesis = "([";
        for (int i = 0; i < closingParenthesis.Length; i++)
        {
            if (Ch(text, start + l) != closingParenthesis[i]) continue;

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
            if (count != 0) return false;

            length = l;
            return true;
        }

        return false;
    }
    /// <summary>QUrl 对 http/https 的要求：必须有主机名。ND2 用 url.isValid() 卡这一条（:3583）</summary>
    public static bool IsValidUrl(string text)
    {
        if (!System.Uri.TryCreate(text, UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return false;
        return uri.Host.Length > 0;
    }

    /// <summary>
    /// 判断 start 处是不是一条 URL。是 → segmentLen = URL 长度；不是 → segmentLen = 可以放心跳过的长度
    /// （调用方据此推进游标，所以对「非 URL」也总有正数，循环不会卡住）。
    /// </summary>
    public static bool IsUrl(string text, int start, out int segmentLen)
    {
        int textLen = text.Length;

        if (ScanToUrlStart(text, start, out int dist, out int schemeLen))
        {
            if (dist != 0)
            {
                segmentLen = dist;
                return false;
            }

            ScanToUrlEnd(text, start + schemeLen, out int len);
            if (len != 0)
            {
                len += schemeLen;
                string urlStr = text.Substring(start, len);
                // 只认 http:// 和 https://，别的 scheme（含 mailto:）一律不算热点
                if ((urlStr.StartsWith("http://") || urlStr.StartsWith("https://")) && IsValidUrl(urlStr))
                {
                    while (RemoveUnwantedTrailingCharFromUrl(text, start, ref len)) { }
                    segmentLen = len;
                    return true;
                }
            }

            // 不是 http(s)：只跳过这一段长得像 scheme 的开头（ND2 :3592-3596，越界读到 NUL 自然收口）
            len = 1;
            int lMax = textLen - start;
            while (IsUrlSchemeStartChar(Ch(text, start + len)) && len < lMax) len++;
            segmentLen = len;
            return false;
        }

        segmentLen = dist;
        return false;
    }

    /// <summary>
    /// 一段文本切成 ND2 那个循环的步进序列：每项 = (码元起点, 码元长度, 是否 URL)。
    /// addHotSpot 靠它把可视区逐段刷/清 indicator，抽出来纯粹是为了能让状态机单独跑一遍。
    /// </summary>
    public static List<(int Start, int Length, bool IsUrl)> ScanSegments(string text)
    {
        var segments = new List<(int, int, bool)>();
        int startWide = 0;
        while (true)
        {
            bool r = IsUrl(text, startWide, out int lenWide);
            if (lenWide <= 0) break;
            segments.Add((startWide, lenWide, r));
            startWide += lenWide;
            if (startWide >= text.Length) break;
        }
        return segments;
    }

    /// <summary>码元段对应的 UTF-8 字节数（ND2 :3669 的 toUtf8().size()）</summary>
    public static int Utf8Bytes(string text, int start, int length)
        => Encoding.UTF8.GetByteCount(text.AsSpan(start, length));
}
