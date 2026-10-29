using System;
using System.Text;

// 从 NotepadSharp/Controls/EditorControl.LineOps.cs 原文抄出来的纯函数，改代码后重新生成：
//   sed -n "599,600p;621,722p;727p;753,898p" ../../NotepadSharp/Controls/EditorControl.LineOps.cs
internal static class Harness
{
/// <summary>对应 ND2 的 TextCaseType（scintillaeditview.h:39）</summary>
public enum CaseMode { Upper, Lower, Proper, ProperBlend, Sentence, SentenceBlend, Invert, Random }
/// <summary>逐条照 ND2 changeCase（scintillaeditview.cpp:2695-2811）搬过来，字符判断按 UTF-16 码元，和 Qt 的 QChar 一致</summary>
public static string ConvertCase(string text, CaseMode mode)
{
    if (text.Length == 0) return text;

    switch (mode)
    {
        case CaseMode.Upper:
            return text.ToUpperInvariant();
        case CaseMode.Lower:
            return text.ToLowerInvariant();

        case CaseMode.Proper:
        case CaseMode.ProperBlend:
        {
            bool force = mode == CaseMode.Proper;
            char[] a = text.ToCharArray();
            for (int i = 0; i < a.Length; i++)
            {
                if (!char.IsLetter(a[i])) continue;

                // 前一个字符不是字母也不是数字才算词首
                if (i < 1 || !char.IsLetterOrDigit(a[i - 1])) a[i] = char.ToUpperInvariant(a[i]);
                else if (force) a[i] = char.ToLowerInvariant(a[i]);

                // 撇号例外：it's / rock'n'roll 里的词首字母要压回小写（BLEND 也生效）
                if (i >= 2 && a[i - 1] == '\'' && char.IsLetter(a[i - 2])) a[i] = char.ToLowerInvariant(a[i]);
            }
            return new string(a);
        }

        case CaseMode.Sentence:
        case CaseMode.SentenceBlend:
        {
            bool force = mode == CaseMode.Sentence;
            char[] a = text.ToCharArray();
            bool isNewSentence = true;
            bool wasEolR = false;
            bool wasEolN = false;
            for (int i = 0; i < a.Length; i++)
            {
                if (char.IsLetter(a[i]))
                {
                    if (isNewSentence)
                    {
                        a[i] = char.ToUpperInvariant(a[i]);
                        isNewSentence = false;
                    }
                    else if (force) a[i] = char.ToLowerInvariant(a[i]);

                    wasEolR = false;
                    wasEolN = false;

                    // 孤立的 i → I：前后是空格/括号/引号/撇号才算孤立
                    if (a[i] == 'i'
                        && i >= 1 && (char.IsWhiteSpace(a[i - 1]) || a[i - 1] == '(' || a[i - 1] == '"')
                        && i + 1 < a.Length && (char.IsWhiteSpace(a[i + 1]) || a[i + 1] == '\''))
                    {
                        a[i] = 'I';
                    }
                }
                else if (a[i] is '.' or '!' or '?')
                {
                    // 后面紧跟字母数字不算新句，防 3.14；ND2 只认半角标点
                    isNewSentence = i + 1 < a.Length && !char.IsLetterOrDigit(a[i + 1]);
                }
                else if (a[i] == '\r')
                {
                    if (wasEolR) isNewSentence = true; else wasEolR = true;
                }
                else if (a[i] == '\n')
                {
                    if (wasEolN) isNewSentence = true; else wasEolN = true;
                }
            }
            return new string(a);
        }

        case CaseMode.Invert:
        {
            char[] a = text.ToCharArray();
            for (int i = 0; i < a.Length; i++)
                a[i] = char.IsLower(a[i]) ? char.ToUpperInvariant(a[i]) : char.ToLowerInvariant(a[i]);
            return new string(a);
        }

        default:
        {
            char[] a = text.ToCharArray();
            for (int i = 0; i < a.Length; i++)
            {
                if (!char.IsLetter(a[i])) continue;
                a[i] = RandomBit() ? char.ToUpperInvariant(a[i]) : char.ToLowerInvariant(a[i]);
            }
            return new string(a);
        }
    }
}

// ND2 用没播种的 std::rand()，等价于每次启动都是同一串；这里只要"看起来随机"，不做那层复刻
private static readonly Random CaseRandom = new();
private static bool RandomBit() => (CaseRandom.Next(2) & 1) == 1;
public enum SpaceTabConversion { TabToSpace, SpaceToTabAll, SpaceToTabLeading }
/// <summary>
/// 上面那条的纯函数部分（拆出来只为能把状态机单独跑一遍）。返回 null = ND2 的那两个提前 return。
/// </summary>
public static (string? Text, int Caret) SpaceTabConvert(string source, int tabWidth, int currentPos, SpaceTabConversion type)
{
    if (source.Length == 0) return (null, 0);     // ND2: docLength < 2 直接 return

    // ND2 对 Tab2Space 先数 TAB，一个都没有就连 undo 都不开、直接返回
    if (type == SpaceTabConversion.TabToSpace && source.IndexOf('\t') < 0) return (null, 0);

    // ND2 的 source 是 NUL 结尾的缓冲，越界读拿到的是 '\0'；这里用同样的读法兜住边界
    char Ch(int index) => index >= 0 && index < source.Length ? source[index] : '\0';

    var dest = new StringBuilder(source.Length + 16);
    int column = 0;
    int newCurrentPos = 0;

    if (type == SpaceTabConversion.TabToSpace)
    {
        for (int i = 0; i < source.Length; i++)
        {
            char c = Ch(i);
            if (c == '\t')
            {
                int insertTabs = tabWidth - (column % tabWidth);
                for (int j = 0; j < insertTabs; j++)
                {
                    dest.Append(' ');
                    if (i <= currentPos) newCurrentPos++;
                }
                column += insertTabs;
            }
            else
            {
                dest.Append(c);
                if (i <= currentPos) newCurrentPos++;
                if (c == '\n' || c == '\r') column = 0;
                else if (!char.IsLowSurrogate(c)) column++;
            }
        }
    }
    else
    {
        bool onlyLeading = type == SpaceTabConversion.SpaceToTabLeading;
        // 三个变量都活在循环外，这是 ND2 的原样（counter 只在特定分支里归零）
        bool nextChar = false;
        int counter = 0;
        int tabStop = tabWidth - 1;   // remember, counting from zero!
        bool nonSpaceFound = false;

        for (int i = 0; i < source.Length; i++)
        {
            if (!nonSpaceFound)
            {
                while (Ch(i + counter) == ' ')
                {
                    if (column + counter == tabStop)
                    {
                        tabStop += tabWidth;
                        if (counter >= 1)
                        {
                            dest.Append('\t');
                            i += counter;
                            column += counter + 1;
                            counter = 0;
                            nextChar = true;
                            if (i <= currentPos) newCurrentPos++;
                            break;
                        }
                        // 后面还跟着空格或 TAB，单个空格也并成 TAB（这一支不 break，接着扫）
                        else if (Ch(i + 1) == ' ' || Ch(i + 1) == '\t')
                        {
                            dest.Append('\t');
                            i++;
                            column += 1;
                            counter = 0;
                            if (i <= currentPos) newCurrentPos++;
                        }
                        else
                        {
                            // 孤零零一个空格，不并
                            dest.Append(Ch(i));
                            column += 1;
                            counter = 0;
                            nextChar = true;
                            if (i <= currentPos) newCurrentPos++;
                            break;
                        }
                    }
                    else counter++;
                }

                if (nextChar)
                {
                    nextChar = false;
                    continue;
                }

                // 空格被右边的 TAB「吸收」，一起并成一根 TAB
                if (Ch(i) == ' ' && Ch(i + counter) == '\t')
                {
                    dest.Append('\t');
                    i += counter;
                    column = tabStop + 1;
                    tabStop += tabWidth;
                    counter = 0;
                    if (i <= currentPos) newCurrentPos++;
                    continue;
                }
            }

            // 「行首」版：本行第一个非空白字符之后就不再转换
            if (onlyLeading && !nonSpaceFound) nonSpaceFound = true;

            char c = Ch(i);
            if (c == '\n' || c == '\r')
            {
                dest.Append(c);
                column = 0;
                tabStop = tabWidth - 1;
                nonSpaceFound = false;
            }
            else if (c == '\t')
            {
                dest.Append(c);
                column = tabStop + 1;
                tabStop += tabWidth;
                counter = 0;
            }
            else
            {
                dest.Append(c);
                counter = 0;
                if (!char.IsLowSurrogate(c))
                {
                    column++;
                    if (column > 0 && column % tabWidth == 0) tabStop += tabWidth;
                }
            }

            if (i <= currentPos) newCurrentPos++;
        }
    }

    return (dest.ToString(), newCurrentPos);
}

// ===== 以下两条从 NotepadSharp/Controls/EditorControl.CommentOps.cs 原文抄出 =====

/// <summary>ND2:2136 <c>qstrncmp(linebuf, comment, comment_length - 1)</c>：命中给出要删的长度，没命中返回 -1</summary>
public static int LineCommentRemoveLength(string linebuf, string comment)
{
    int n = comment.Length;
    if (!linebuf.StartsWith(comment[..(n - 1)], StringComparison.Ordinal)) return -1;
    return CharAt(linebuf, n - 1) == ' ' ? n : n - 1;
}

/// <summary>
/// ND2:2172-2176：行首对上 <c>/*</c>（不带尾空格）、行尾对上 <c>*/</c>（不带前空格）才算命中，
/// 挨着符号那一侧的空格一并删。没命中返回 null。
/// </summary>
public static (int StartLen, int EndLen)? StreamRemoveLengths(string linebuf, string advStart, string advEnd)
{
    int s = advStart.Length, e = advEnd.Length;
    if (!linebuf.StartsWith(advStart[..(s - 1)], StringComparison.Ordinal)) return null;
    if (!linebuf.EndsWith(advEnd[1..], StringComparison.Ordinal)) return null;
    return (CharAt(linebuf, s - 1) == ' ' ? s : s - 1, CharAt(linebuf, linebuf.Length - e) == ' ' ? e : e - 1);
}

/// <summary>QByteArray::operator[] 越界读到的是 '\0'，这里同样补一个 '\0'</summary>
private static char CharAt(string s, int index) => index >= 0 && index < s.Length ? s[index] : '\0';
}
