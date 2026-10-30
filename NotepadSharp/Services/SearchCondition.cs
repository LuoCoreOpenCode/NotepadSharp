using System.Text;
using System.Text.RegularExpressions;
using ScintillaNET;

namespace NotepadSharp.Services;

/// <summary>查找模式，与 ND2 查找窗的三态单选一致：普通 / 扩展（\n \t \xNN 等转义）/ 正则</summary>
public enum SearchMode
{
    /// <summary>普通：查找文本原样参与匹配，不做转义还原、不当正则解释</summary>
    Normal,
    /// <summary>扩展：查找与替换文本先经 Unescape 还原 \n \t \xNN 等转义，再按字面参与匹配</summary>
    Extend,
    /// <summary>正则：查找文本直接作为正则表达式交给匹配引擎</summary>
    Regex,
}

/// <summary>
/// 同一个查找条件用在不同动作上时，Scintilla 的正则零长匹配策略不同。
/// 对齐 ND2 <c>buildSearchFlags</c>（findwin.cpp:2211）的 FindNextType 三态：
/// 查找/计数/标记用 FINDNEXT，替换链路用 REPLACENEXT，替换前的定位用 FINDNEXTFORREPLACE。
/// </summary>
public enum FindVerb
{
    /// <summary>查找/计数/标记（ND2 FINDNEXT）：允许任意位置的零长匹配</summary>
    FindNext,
    /// <summary>替换执行（ND2 REPLACENEXT）：禁止紧跟上一次匹配之后的零长匹配，避免替换原地空转</summary>
    ReplaceNext,
    /// <summary>替换前定位（ND2 FINDNEXTFORREPLACE）：零长匹配处处允许，也允许落在区段起点</summary>
    FindNextForReplace,
}

/// <summary>
/// 一次查找、替换或标记的全部条件。编辑器与目录扫描共用同一个描述，
/// 避免把四个 bool 一路传下去；替换文本的扩展转义也按同一个 Mode 处理。
/// </summary>
/// <param name="Text">查找框原文，未做转义还原；实际参与匹配的是 EffectiveText</param>
/// <param name="Mode">普通/扩展/正则三态，决定转义与匹配引擎口径</param>
/// <param name="MatchCase">是否区分大小写</param>
/// <param name="WholeWord">是否全词匹配</param>
public sealed record SearchCondition(string Text, SearchMode Mode, bool MatchCase, bool WholeWord)
{
    /// <summary>空条件单例：普通模式、空文本，用作默认占位，避免各处重复 new</summary>
    public static SearchCondition Empty { get; } = new(string.Empty, SearchMode.Normal, false, false);

    /// <summary>查找文本是否为空——只看原始输入，不做转义还原（扩展模式下 "\n" 虽匹配不到内容也不算空）</summary>
    public bool IsEmpty => Text.Length == 0;

    /// <summary>送进匹配引擎的文本：扩展模式先还原转义，其余原样</summary>
    public string EffectiveText => Mode == SearchMode.Extend ? Unescape(Text) : Text;

    /// <summary>替换文本按同一模式还原转义（ND2 对替换框做的是同一个 convertExtendedToString）</summary>
    /// <param name="raw">替换输入框的原始文本</param>
    /// <returns>扩展模式返回 Unescape 后的文本；普通模式原样返回；正则模式也原样返回，反向引用在别处处理</returns>
    public string PrepareReplacement(string raw)
        // 与 EffectiveText 同一口径：只有扩展模式吃转义，普通/正则都不碰反斜杠
        => Mode == SearchMode.Extend ? Unescape(raw) : raw;

    // 上游 Scintilla 5.x 的空匹配位；ND2 自带的那份把它们挪到了最高半字节，值不同但语义一致。
    /// <summary>零长匹配策略位：允许正则匹配空串（SCFIND_REGEXP_EMPTYMATCH_ALL 的等价物）</summary>
    private const int RegExpEmptyMatchAll = 0x01000000;
    /// <summary>零长匹配策略位：不允许紧跟在上一次匹配末尾之后的空匹配，防止替换原地死循环</summary>
    private const int RegExpEmptyMatchNotAfterMatch = 0x02000000;
    /// <summary>零长匹配策略位：允许空匹配落在搜索区段的起始位置</summary>
    private const int RegExpEmptyMatchAllowAtStart = 0x04000000;
    /// <summary>把 \r\n 当成单个字符跨过：正则匹配不会把一个 CRLF 换行拆成两处命中</summary>
    private const int RegExpSkipCrlfAsOne = 0x08000000;

    /// <summary>Scintilla 搜索标志：正则零长匹配的策略随动作变化，与 ND2 的三态一致</summary>
    /// <param name="verb">本次条件用到的动作（查找/替换/替换前定位），决定空匹配策略位的组合</param>
    /// <returns>可直接交给 Scintilla SearchText 的 SearchFlags 组合值</returns>
    public SearchFlags ToScintillaFlags(FindVerb verb = FindVerb.FindNext)
    {
        // 基础三位：与查找窗复选框一一对应，未勾选就不置位
        var flags = SearchFlags.None;
        if (MatchCase) flags |= SearchFlags.MatchCase;
        if (WholeWord) flags |= SearchFlags.WholeWord;
        if (Mode == SearchMode.Regex) flags |= SearchFlags.Regex;

        // 零长匹配策略随动作三选一：替换链路最保守，避免在上次匹配末尾再空匹配一次原地打转
        int empty = verb switch
        {
            FindVerb.ReplaceNext => RegExpEmptyMatchNotAfterMatch,
            FindVerb.FindNextForReplace => RegExpEmptyMatchAll | RegExpEmptyMatchAllowAtStart,
            _ => RegExpEmptyMatchAll,
        };
        // 统一叠加上 CRLF 视为单字符的位；SearchFlags 枚举本身没开放这个值，只能转 int 强拼
        return (SearchFlags)((int)flags | empty | RegExpSkipCrlfAsOne);
    }

    /// <summary>目录扫描一侧用的 .NET 正则参数：只映射大小写；全词由调用方用 \b 包裹实现，多行不需要（扫描按行切好了）</summary>
    /// <returns>区分大小写时 RegexOptions.None，否则 RegexOptions.IgnoreCase</returns>
    public RegexOptions ToRegexOptions() => MatchCase ? RegexOptions.None : RegexOptions.IgnoreCase;

    /// <summary>
    /// 扩展模式转义：\r \n \0 \t \\ 与 \b(8位二进制) \o(3位八进制) \d(3位十进制)
    /// \x(2位十六进制) \u(4位十六进制)。数字位数不足或非法时，连同反斜杠一起当普通文本，
    /// 与 ND2 convertExtendedToString 的 default 分支一致。
    /// </summary>
    /// <param name="text">输入框原始文本</param>
    /// <returns>转义还原后的文本；不含反斜杠时直接返回原引用，不重新分配</returns>
    public static string Unescape(string text)
    {
        // 快速路径：空串或没有反斜杠就不可能有转义，原样返回
        if (text.Length == 0 || text.IndexOf('\\') < 0) return text;

        var sb = new StringBuilder(text.Length);
        int i = 0;
        while (i < text.Length)
        {
            // 普通字符照抄；行尾孤立的反斜杠（后面没有字符了）也按字面保留
            if (text[i] != '\\' || i == text.Length - 1)
            {
                sb.Append(text[i++]);
                continue;
            }

            // 走到这里说明 text[i] 是反斜杠且至少还有一个后继字符，先看它是不是转义标识
            char esc = text[i + 1];
            // 默认只吃掉「\ + 标识」两个字符；数字转义成功后改成 2 + 位数
            int consumed = 2;
            switch (esc)
            {
                // 五个单字符固定转义：直接追加目标字符
                case 'r': sb.Append('\r'); break;
                case 'n': sb.Append('\n'); break;
                case 't': sb.Append('\t'); break;
                case '0': sb.Append('\0'); break;
                case '\\': sb.Append('\\'); break;
                // 数字转义：位数按进制固定——\b 8 位、\o/\d 3 位、\x 2 位、\u 4 位
                case 'b': case 'o': case 'd': case 'x': case 'u':
                    int size = esc switch { 'b' => 8, 'o' => 3, 'd' => 3, 'x' => 2, _ => 4 };
                    int from = i + 2;
                    // 必须剩余长度够 size 位且每位都在该进制内才生效；
                    // 值直接按 char（UTF-16 码元）截断，不做代理对合成，与 ND2 逐字符口径一致
                    if (from + size <= text.Length && TryReadRadix(text, from, size, RadixOf(esc), out int value))
                    {
                        sb.Append((char)value);
                        consumed = 2 + size;
                    }
                    else
                    {
                        // 数字不够或非法：不吞掉，按字面写回
                        sb.Append('\\').Append(esc);
                    }
                    break;
                // 未知标识（如 \q）：反斜杠与字符都按字面保留，consumed 保持 2
                default:
                    sb.Append('\\').Append(esc);
                    break;
            }
            // 按实际消耗长度推进，数字转义一次跳过 2+size 个字符
            i += consumed;
        }
        return sb.ToString();
    }

    /// <summary>数字转义标识对应的进制：\b 二进制、\o 八进制、\d 十进制、\x 与 \u 十六进制</summary>
    /// <param name="esc">反斜杠后面的转义标识字符</param>
    /// <returns>按该标识读数字时应使用的进制</returns>
    private static int RadixOf(char esc) => esc switch { 'b' => 2, 'o' => 8, 'd' => 10, _ => 16 };

    /// <summary>恰好读 size 位，任何一位超出该进制就整体失败（ND2 readBase 的严格语义）</summary>
    /// <param name="s">源文本</param>
    /// <param name="start">首位数字在 s 中的下标</param>
    /// <param name="size">必须恰好读满的位数</param>
    /// <param name="radix">进制（2/8/10/16）</param>
    /// <param name="value">成功时为换算出的整数值；失败时可能已累加出一部分，调用方必须丢弃</param>
    /// <returns>size 位全部是该进制下的合法数字才返回 true</returns>
    private static bool TryReadRadix(string s, int start, int size, int radix, out int value)
    {
        value = 0;
        // 逐位按进位累加（Horner）：任一 digit 非数字或 ≥radix 立即 false，不做宽容截断
        for (int k = 0; k < size; k++)
        {
            int digit = CharToDigit(s[start + k]);
            if (digit < 0 || digit >= radix) return false;
            value = value * radix + digit;
        }
        return true;
    }

    /// <summary>字符换算位值：0-9 得 0..9，a-z 与 A-Z 得 10..35，其余 -1；是否越界由 TryReadRadix 按进制再筛</summary>
    /// <param name="c">待换算的字符</param>
    /// <returns>该字符的数位值，非字母数字返回 -1</returns>
    private static int CharToDigit(char c)
        // 三段模式匹配依次尝试：十进制数字 → 小写字母 → 大写字母，都不中就是 -1
        => c is >= '0' and <= '9' ? c - '0'
         : c is >= 'a' and <= 'z' ? c - 'a' + 10
         : c is >= 'A' and <= 'Z' ? c - 'A' + 10
         : -1;
}
