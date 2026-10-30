using System;
using System.Text;

// 从 NotepadSharp/Controls/EditorControl.LineOps.cs 原文抄出来的纯函数，改代码后重新生成：
//   sed -n "599,600p;621,722p;727p;753,898p" ../../NotepadSharp/Controls/EditorControl.LineOps.cs
/// <summary>
/// casetest 的「被测副本」容器：内嵌一份从主工程 EditorControl.LineOps.cs / EditorControl.CommentOps.cs
/// 原文抄出的纯函数（拆掉 UI/撤销等副作用，只留可单测的算法本体），外加注释判定用到的工具方法。
/// 注意：这里是副本不是引用——主工程改了对应实现后必须同步重新抄出，否则回归测的是旧代码。
/// </summary>
internal static class Harness
{
/// <summary>
/// 对应 ND2 的 TextCaseType（scintillaeditview.h:39）。
/// Proper/Sentence 各带一个 Blend 变体：非 Blend（FORCE）把「不该动」的字母也压平，Blend 对它们原样保留。
/// </summary>
public enum CaseMode { Upper, Lower, Proper, ProperBlend, Sentence, SentenceBlend, Invert, Random } // 8 个值一一对应 ND2「转换大小写」子菜单条目
/// <summary>逐条照 ND2 changeCase（scintillaeditview.cpp:2695-2811）搬过来，字符判断按 UTF-16 码元，和 Qt 的 QChar 一致</summary>
/// <param name="text">待转换原文；空串原样返回（对应 ND2 对空文档的直接 return）</param>
/// <param name="mode">转换模式，见 <see cref="CaseMode"/></param>
/// <returns>转换后的新字符串；除 Random 外逐字符确定，可作为回归期望值</returns>
public static string ConvertCase(string text, CaseMode mode)
{
    if (text.Length == 0) return text;   // 空输入短路，防后面按 [i-1]/[i+1] 邻接判断的逻辑空跑

    switch (mode)
    {
        case CaseMode.Upper:
            return text.ToUpperInvariant();      // Invariant：不随系统区域设置变，期望值才能跨机器稳定
        case CaseMode.Lower:
            return text.ToLowerInvariant();      // 同上，ToLower 的不变体

        case CaseMode.Proper:
        case CaseMode.ProperBlend:
        {
            // FORCE/BLEND 的全部差别收敛在这一个布尔上：BLEND 只是不执行「非词首压小写」这一步
            bool force = mode == CaseMode.Proper;
            char[] a = text.ToCharArray();
            for (int i = 0; i < a.Length; i++)
            {
                if (!char.IsLetter(a[i])) continue;   // 非字母（数字/标点/空白）一律原样透传

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
            // 同样只由 force 一个开关区分 FORCE/BLEND
            bool force = mode == CaseMode.Sentence;
            char[] a = text.ToCharArray();
            bool isNewSentence = true;   // 文档开头视为新句起点，所以首字母会被抬大写
            bool wasEolR = false;        // 上一行尾是否已见过 \r：连续两个 \r 才算「空行开新句」
            bool wasEolN = false;        // 同上，\n 版本（ND2 对两种换行分别数）
            for (int i = 0; i < a.Length; i++)
            {
                if (char.IsLetter(a[i]))
                {
                    // 新句的第一个字母抬成大写后关闭 isNewSentence；句中字母仅 FORCE 压小写
                    if (isNewSentence)
                    {
                        a[i] = char.ToUpperInvariant(a[i]);
                        isNewSentence = false;
                    }
                    else if (force) a[i] = char.ToLowerInvariant(a[i]);

                    wasEolR = false;
                    wasEolN = false;           // 一旦出现字母，行尾连续换行的计数清零，防空行误开新句

                    // 孤立的 i → I：前后是空格/括号/引号/撇号才算孤立
                    // 注意此时 a[i] 还是小写 i：句中规则若已把它升成大写 I，这里就不会再命中
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
                    // 第二次连续 \r 才认定「空行→新句」，单次回车只是普通换行
                    if (wasEolR) isNewSentence = true; else wasEolR = true;
                }
                else if (a[i] == '\n')
                {
                    // \n 版同理，且与 \r 各自独立计数（ND2 原样）
                    if (wasEolN) isNewSentence = true; else wasEolN = true;
                }
            }
            return new string(a);
        }

        case CaseMode.Invert:
        {
            char[] a = text.ToCharArray();
            // 三目只问 IsLower：小写升大写；大写和一切非字母统一走 ToLower（对数字/标点是恒等操作）
            for (int i = 0; i < a.Length; i++)
                a[i] = char.IsLower(a[i]) ? char.ToUpperInvariant(a[i]) : char.ToLowerInvariant(a[i]);
            return new string(a);
        }

        default:
        {
            // default 兜住 CaseMode.Random：ND2 用未播种的 rand()、结果串不可复现，
            // 所以 Program.cs 里只断言「长度不变、只动字母」这类不变量
            char[] a = text.ToCharArray();
            for (int i = 0; i < a.Length; i++)
            {
                if (!char.IsLetter(a[i])) continue;   // 非字母不参与翻牌
                a[i] = RandomBit() ? char.ToUpperInvariant(a[i]) : char.ToLowerInvariant(a[i]);
            }
            return new string(a);
        }
    }
}

// ND2 用没播种的 std::rand()，等价于每次启动都是同一串；这里只要"看起来随机"，不做那层复刻
/// <summary>Random 模式的随机源。故意不固定种子：回归只断言不变量（见 Program.cs），不比对具体串</summary>
private static readonly Random CaseRandom = new();
/// <summary>取一个随机位，充当 ND2 rand()%2 的等价物</summary>
/// <returns>true = 本轮字母转大写，false = 转小写</returns>
private static bool RandomBit() => (CaseRandom.Next(2) & 1) == 1;
/// <summary>spaceTabConvert 的三个方向，对应 ND2「TAB↔空格」子菜单的三个条目</summary>
public enum SpaceTabConversion { TabToSpace, SpaceToTabAll, SpaceToTabLeading }
/// <summary>
/// 上面那条的纯函数部分（拆出来只为能把状态机单独跑一遍）。返回 null = ND2 的那两个提前 return。
/// 写回文档、撤销点、UI 刷新等副作用不在这里，由主工程 EditorControl.LineOps.cs 的调用方负责。
/// </summary>
/// <param name="source">待转换的整段原文，按 UTF-16 码元下标扫描</param>
/// <param name="tabWidth">制表位宽度，ND2 传编辑器当前设置（回归用例统一用 4）</param>
/// <param name="currentPos">原光标位置；搬运规则是 ND2 的 i&lt;=currentPos，等号把当前字符也算进去</param>
/// <param name="type">转换方向：TAB→空格 / 空格→TAB（全部）/ 空格→TAB（行首）</param>
/// <returns>Text=null 表示无操作（空文档，或 TAB→空格时全文没有 TAB）；否则给新文本与搬运后的光标位置</returns>
public static (string? Text, int Caret) SpaceTabConvert(string source, int tabWidth, int currentPos, SpaceTabConversion type)
{
    if (source.Length == 0) return (null, 0);     // ND2: docLength < 2 直接 return

    // ND2 对 Tab2Space 先数 TAB，一个都没有就连 undo 都不开、直接返回
    if (type == SpaceTabConversion.TabToSpace && source.IndexOf('\t') < 0) return (null, 0);

    // ND2 的 source 是 NUL 结尾的缓冲，越界读拿到的是 '\0'；这里用同样的读法兜住边界
    char Ch(int index) => index >= 0 && index < source.Length ? source[index] : '\0';

    var dest = new StringBuilder(source.Length + 16);  // 输出缓冲，+16 只是容量预估，不影响行为
    int column = 0;          // 当前显示列：按码元计数，东亚字符也只算 1 列（ND2 原样）
    int newCurrentPos = 0;   // 搬运后的新光标位置：从 0 起，按「处理过的每个源字符/展开空格」累计

    if (type == SpaceTabConversion.TabToSpace)
    {
        // 展开方向：逐字符照抄，遇到 TAB 按当前列补空格到下一个制表位
        for (int i = 0; i < source.Length; i++)
        {
            char c = Ch(i);   // 统一走带越界补 '\0' 的读法，模拟 ND2 的 NUL 结尾缓冲
            if (c == '\t')
            {
                // 补位量 = 到下一个制表位的距离；恰好踩在制表位上也补满一整档（ND2 原样）
                int insertTabs = tabWidth - (column % tabWidth);
                for (int j = 0; j < insertTabs; j++)
                {
                    dest.Append(' ');
                    // 展开出的每个空格都是「新增列」：只要 TAB 本身没走过光标（i<=currentPos），
                    // 光标就跟着这些空格一起右移
                    if (i <= currentPos) newCurrentPos++;
                }
                column += insertTabs;
            }
            else
            {
                dest.Append(c);
                if (i <= currentPos) newCurrentPos++;     // 普通字符一列换光标一步
                if (c == '\n' || c == '\r') column = 0;    // 换行把列清零，下一行从列 0 起算
                else if (!char.IsLowSurrogate(c)) column++;// 代理对的低半码元不占列，一个字素只算 1 列
            }
        }
    }
    else
    {
        bool onlyLeading = type == SpaceTabConversion.SpaceToTabLeading;  // 「行首」与「全部」共用同一台状态机，只差这一个开关
        // 三个变量都活在循环外，这是 ND2 的原样（counter 只在特定分支里归零）
        bool nextChar = false;      // 置 true 表示本轮分支已自行推进过 i，回到循环顶前要先 continue
        int counter = 0;            // 从当前 i 起向后连续数到的空格个数
        int tabStop = tabWidth - 1;   // remember, counting from zero!  下一个制表位的列号（0 基）
        bool nonSpaceFound = false;   // 「行首」版用它记住本行是否已出现第一个非空白字符

        for (int i = 0; i < source.Length; i++)
        {
            // 只在还没越过行首空格串时做贪心扫描；ALL 版扫完整串，LEADING 版见下条 nonSpaceFound
            if (!nonSpaceFound)
            {
                // 向后贪心数空格：counter 是已数到的个数，数到非空格（或越界 '\0'）为止
                while (Ch(i + counter) == ' ')
                {
                    // 数到的这串空格恰好顶到制表位，才允许触发合并
                    if (column + counter == tabStop)
                    {
                        tabStop += tabWidth;
                        // 凑满且不止一格（counter>=1）：整串空格并成一根 TAB，推进 i 后 break 出扫描
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
                    else counter++;   // 还没够到一个制表位：候选串加长一格，回到 while 顶再数
                }

                // 上一轮合并分支已经自行推进过 i：跳过本行剩余逻辑，从新位置继续扫
                if (nextChar)
                {
                    nextChar = false;
                    continue;
                }

                // 空格被右边的 TAB「吸收」，一起并成一根 TAB
                // while 因下一个字符是 TAB 而退出后走这里：空格串+那个 TAB 合成一根 TAB，列直接落到制表位右侧
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

            char c = Ch(i);   // 走到这里说明本字符不是待合并的行首空格串：按普通字符搬运
            if (c == '\n' || c == '\r')
            {
                // 换行：列、制表位、行首状态全部复位，下一行重新起算（LEADING 版也因此重新生效）
                dest.Append(c);
                column = 0;
                tabStop = tabWidth - 1;
                nonSpaceFound = false;
            }
            else if (c == '\t')
            {
                // 原文 TAB 原样保留，只负责推进 column 并抬高下一个 tabStop
                dest.Append(c);
                column = tabStop + 1;
                tabStop += tabWidth;
                counter = 0;
            }
            else
            {
                // 普通字符：原样写出，之前的空格候选作废（counter 归零）
                dest.Append(c);
                counter = 0;
                if (!char.IsLowSurrogate(c))
                {
                    column++;
                    // 列刚好踩上制表位边界时把 tabStop 同步抬高一档，保持「下一个制表位」语义
                    if (column > 0 && column % tabWidth == 0) tabStop += tabWidth;
                }
            }

            if (i <= currentPos) newCurrentPos++;   // 统一的搬运计数：含等号，当前字符本身也被算进去
        }
    }

    return (dest.ToString(), newCurrentPos);   // 新文本 + 搬运后的光标；是否写回文档由调用方决定
}

// ===== 以下两条从 NotepadSharp/Controls/EditorControl.CommentOps.cs 原文抄出 =====

/// <summary>
/// ND2:2136 <c>qstrncmp(linebuf, comment, comment_length - 1)</c>：命中给出要删的长度，没命中返回 -1。
/// 比较时刻意少比一个字符：comment 尾部那格空格不参与「是不是注释」的判定，只影响「删多长」。
/// </summary>
/// <param name="linebuf">整行文本（不含行尾换行符）</param>
/// <param name="comment">注释前缀，ND2 传的是带尾空格的形态（如 "// "、"# "、"REM "）</param>
/// <returns>要删除的前缀字符数（紧跟空格删全长 n，否则删 n-1）；-1 = 行首不是这个注释符</returns>
public static int LineCommentRemoveLength(string linebuf, string comment)
{
    int n = comment.Length;                                                          // 前缀全长（含尾空格）
    if (!linebuf.StartsWith(comment[..(n - 1)], StringComparison.Ordinal)) return -1; // 只比前 n-1 个：Ordinal、从行首起
    return CharAt(linebuf, n - 1) == ' ' ? n : n - 1;                                 // 紧跟的是空格就连空格删；行正好到符号为止时越界读出 '\0'，落到 n-1 档
}

/// <summary>
/// ND2:2172-2176：行首对上 <c>/*</c>（不带尾空格）、行尾对上 <c>*/</c>（不带前空格）才算命中，
/// 挨着符号那一侧的空格一并删。没命中返回 null。
/// 首尾两侧独立判定：各自看紧挨符号的那一格是不是空格。
/// </summary>
/// <param name="linebuf">整行文本</param>
/// <param name="advStart">区块注释起始前缀，带尾空格形态（如 "&lt;!-- "、"/* "）</param>
/// <param name="advEnd">区块注释结束后缀，带前导空格形态（如 " -->"、" */"）</param>
/// <returns>(头部删除长度, 尾部删除长度)；任一侧没对上返回 null，调用方整行不动</returns>
public static (int StartLen, int EndLen)? StreamRemoveLengths(string linebuf, string advStart, string advEnd)
{
    int s = advStart.Length, e = advEnd.Length;                       // 前缀/后缀全长（各含一格 padding 空格）
    if (!linebuf.StartsWith(advStart[..(s - 1)], StringComparison.Ordinal)) return null;  // 行首只比前 s-1 个，尾空格不算匹配条件
    if (!linebuf.EndsWith(advEnd[1..], StringComparison.Ordinal)) return null;            // 行尾只比后 e-1 个，前导空格同理
    return (CharAt(linebuf, s - 1) == ' ' ? s : s - 1, CharAt(linebuf, linebuf.Length - e) == ' ' ? e : e - 1);  // 挨着符号的空格一并删，两侧各算各的
}

/// <summary>QByteArray::operator[] 越界读到的是 '\0'，这里同样补一个 '\0'</summary>
/// <param name="s">被当作字节数组读取的字符串</param>
/// <param name="index">下标，允许越界（负数或 >=Length）</param>
/// <returns>界内返回对应字符；界外返回 '\0'，复现 Qt 的越界语义而不是抛异常</returns>
private static char CharAt(string s, int index) => index >= 0 && index < s.Length ? s[index] : '\0';
}
