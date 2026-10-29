using System.Text;
using System.Text.RegularExpressions;
using ScintillaNET;

namespace NotepadSharp.Services;

/// <summary>查找模式，与 ND2 查找窗的三态单选一致：普通 / 扩展（\n \t \xNN 等转义）/ 正则</summary>
public enum SearchMode
{
    Normal,
    Extend,
    Regex,
}

/// <summary>
/// 同一个查找条件用在不同动作上时，Scintilla 的正则零长匹配策略不同。
/// 对齐 ND2 <c>buildSearchFlags</c>（findwin.cpp:2211）的 FindNextType 三态：
/// 查找/计数/标记用 FINDNEXT，替换链路用 REPLACENEXT，替换前的定位用 FINDNEXTFORREPLACE。
/// </summary>
public enum FindVerb
{
    FindNext,
    ReplaceNext,
    FindNextForReplace,
}

/// <summary>
/// 一次查找、替换或标记的全部条件。编辑器与目录扫描共用同一个描述，
/// 避免把四个 bool 一路传下去；替换文本的扩展转义也按同一个 Mode 处理。
/// </summary>
public sealed record SearchCondition(string Text, SearchMode Mode, bool MatchCase, bool WholeWord)
{
    public static SearchCondition Empty { get; } = new(string.Empty, SearchMode.Normal, false, false);

    public bool IsEmpty => Text.Length == 0;

    /// <summary>送进匹配引擎的文本：扩展模式先还原转义，其余原样</summary>
    public string EffectiveText => Mode == SearchMode.Extend ? Unescape(Text) : Text;

    /// <summary>替换文本按同一模式还原转义（ND2 对替换框做的是同一个 convertExtendedToString）</summary>
    public string PrepareReplacement(string raw)
        => Mode == SearchMode.Extend ? Unescape(raw) : raw;

    // 上游 Scintilla 5.x 的空匹配位；ND2 自带的那份把它们挪到了最高半字节，值不同但语义一致。
    private const int RegExpEmptyMatchAll = 0x01000000;
    private const int RegExpEmptyMatchNotAfterMatch = 0x02000000;
    private const int RegExpEmptyMatchAllowAtStart = 0x04000000;
    private const int RegExpSkipCrlfAsOne = 0x08000000;

    /// <summary>Scintilla 搜索标志：正则零长匹配的策略随动作变化，与 ND2 的三态一致</summary>
    public SearchFlags ToScintillaFlags(FindVerb verb = FindVerb.FindNext)
    {
        var flags = SearchFlags.None;
        if (MatchCase) flags |= SearchFlags.MatchCase;
        if (WholeWord) flags |= SearchFlags.WholeWord;
        if (Mode == SearchMode.Regex) flags |= SearchFlags.Regex;

        int empty = verb switch
        {
            FindVerb.ReplaceNext => RegExpEmptyMatchNotAfterMatch,
            FindVerb.FindNextForReplace => RegExpEmptyMatchAll | RegExpEmptyMatchAllowAtStart,
            _ => RegExpEmptyMatchAll,
        };
        return (SearchFlags)((int)flags | empty | RegExpSkipCrlfAsOne);
    }

    public RegexOptions ToRegexOptions() => MatchCase ? RegexOptions.None : RegexOptions.IgnoreCase;

    /// <summary>
    /// 扩展模式转义：\r \n \0 \t \\ 与 \b(8位二进制) \o(3位八进制) \d(3位十进制)
    /// \x(2位十六进制) \u(4位十六进制)。数字位数不足或非法时，连同反斜杠一起当普通文本，
    /// 与 ND2 convertExtendedToString 的 default 分支一致。
    /// </summary>
    public static string Unescape(string text)
    {
        if (text.Length == 0 || text.IndexOf('\\') < 0) return text;

        var sb = new StringBuilder(text.Length);
        int i = 0;
        while (i < text.Length)
        {
            if (text[i] != '\\' || i == text.Length - 1)
            {
                sb.Append(text[i++]);
                continue;
            }

            char esc = text[i + 1];
            int consumed = 2;
            switch (esc)
            {
                case 'r': sb.Append('\r'); break;
                case 'n': sb.Append('\n'); break;
                case 't': sb.Append('\t'); break;
                case '0': sb.Append('\0'); break;
                case '\\': sb.Append('\\'); break;
                case 'b': case 'o': case 'd': case 'x': case 'u':
                    int size = esc switch { 'b' => 8, 'o' => 3, 'd' => 3, 'x' => 2, _ => 4 };
                    int from = i + 2;
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
                default:
                    sb.Append('\\').Append(esc);
                    break;
            }
            i += consumed;
        }
        return sb.ToString();
    }

    private static int RadixOf(char esc) => esc switch { 'b' => 2, 'o' => 8, 'd' => 10, _ => 16 };

    /// <summary>恰好读 size 位，任何一位超出该进制就整体失败（ND2 readBase 的严格语义）</summary>
    private static bool TryReadRadix(string s, int start, int size, int radix, out int value)
    {
        value = 0;
        for (int k = 0; k < size; k++)
        {
            int digit = CharToDigit(s[start + k]);
            if (digit < 0 || digit >= radix) return false;
            value = value * radix + digit;
        }
        return true;
    }

    private static int CharToDigit(char c)
        => c is >= '0' and <= '9' ? c - '0'
         : c is >= 'a' and <= 'z' ? c - 'a' + 10
         : c is >= 'A' and <= 'Z' ? c - 'A' + 10
         : -1;
}
