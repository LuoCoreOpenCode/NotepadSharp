using System;
using ScintillaNET;

namespace NotepadSharp.Controls;

/// <summary>
/// 注释三件套，逐条照 ND2 搬：<c>doBlockComment</c>（scintillaeditview.cpp:1991-2282）、
/// <c>doStreamComment</c>（:2521-2600）、<c>undoStreamComment</c>（:2324-2519）、
/// 分发器 <c>comment(int type)</c>（:4274）。
/// 符号取自当前语言的词法器（见 <see cref="Services.LexerService.GetCommentSymbols"/>）；
/// 三个符号都空 = 这门语言没有注释动作，和 ND2 一样直接不动作、不提示。
/// </summary>
public partial class EditorControl
{
    /// <summary>
    /// ND2 的 Comment_Mode（scintillaeditview.h:51）三个值：
    /// <c>Comment</c> 只加不删；<c>Uncomment</c> 只删不加，一行都解不开时再试着拆成对注释；
    /// <c>Toggle</c> 逐行看现状——已有注释符的行删掉、没有的行加上（Ctrl+Q 走的就是它）。
    /// </summary>
    private enum CommentMode { Comment, Uncomment, Toggle }

    /// <summary>
    /// ND2 的 ADD_DEL_LINE_COM / ADD_BK_COM / DEL_BK_COM 三个动作号，
    /// 分别落到本文件的 <see cref="DoBlockComment"/>（切换模式）、
    /// <see cref="DoStreamComment"/>、<see cref="UndoStreamComment"/>，见 <see cref="Comment"/>。
    /// </summary>
    public enum CommentAction { AddDelLineComment, AddBlockComment, DelBlockComment }

    /// <summary>
    /// ND2 在注释符与正文之间固定补的那一个空格：加注释时拼在符号后面（行注释符 <c>//</c> 后面跟一个空格），
    /// 去注释时靠它判断能不能顺手多吃掉一格——挨着符号那一侧的空格一并删。
    /// </summary>
    private const string CommentSpace = " ";

    /// <summary>
    /// ND2 <c>registerCommentShortKey</c>（ccnotepad.cpp:1635）注册的那三条快捷键最终都落到这个分发：
    /// Ctrl+Q 行注释开关、Ctrl+K 加区块注释、Ctrl+Shift+K 清区块注释。
    /// </summary>
    /// <param name="action">要执行的动作号，取值与 ND2 的三个注释命令一一对应</param>
    public void Comment(CommentAction action)
    {
        // ND2 的分发器只做转译，不带任何状态；未列出的动作号（理论上不会出现）直接忽略
        switch (action)
        {
            case CommentAction.AddDelLineComment:
                // Ctrl+Q：切换模式，逐行决定「加」还是「删」
                DoBlockComment(CommentMode.Toggle);
                break;
            case CommentAction.AddBlockComment:
                // Ctrl+K：给选区（无选区时是当前行正文）套上一对成对注释符
                DoStreamComment();
                break;
            case CommentAction.DelBlockComment:
                // Ctrl+Shift+K：拆成对注释；拆不动时允许退回逐行解行注释（ND2 传的就是 true）
                UndoStreamComment(true);
                break;
        }
    }

    // ==================== 行注释 / 单行区块注释（doBlockComment） ====================

    /// <summary>
    /// ND2 <c>doBlockComment</c>（scintillaeditview.cpp:1991-2282）：对选区覆盖到的每一行，
    /// 按 <paramref name="mode"/> 加/解/切换行注释前缀；这门语言没有行注释符时（HTML/XML/CSS/Pascal），
    /// 改用成对符号把整行包起来（代码里叫 advanced 模式）。
    /// </summary>
    /// <param name="mode">注释方向：只加、只解、或逐行自动切换</param>
    /// <returns>true = 执行了逐行处理（哪怕实际一行都没改）；false = 这门语言两种符号都没有，或 Uncomment 直接转交给了 <see cref="UndoStreamComment"/></returns>
    private bool DoBlockComment(CommentMode mode)
    {
        // 当前语言的三种注释符号（行前缀 + 成对起止），随词法器切换在 ApplyLexer 里刷新
        var symbols = CommentSymbols;
        bool hasLine = symbols.Line.Length > 0;                                   // 有没有行注释符，如 C 系的 //
        bool hasStream = symbols.Start.Length > 0 && symbols.End.Length > 0;      // 成对符号是否齐（起止都得有）

        // ND2:2023 —— 没有行注释符时，只要有成对符号就改走「整行包起来」的高级模式；两者都没有则不动作。
        // 这就是 HTML/XML/CSS/Pascal 的 Ctrl+Q 变成区块注释、而 C 系永远走行注释的原因。
        bool advanced = false;
        if (!hasLine)
        {
            if (!hasStream) return false;                             // 这门语言压根没有注释概念，什么都不做也不提示
            if (mode == CommentMode.Uncomment) return UndoStreamComment(false);   // 纯「解注释」时交给成对注释那条路，不许再回退到行注释
            advanced = true;                                          // 加/切换 → 用成对符号包整行
        }

        // ND2:2065-2085 前缀后面总带一个空格；BaanC 不带空格、且要注释空行、Fortran77 要贴在列 0，
        // 这三种语言本项目的语言表里都没有，相应特判照旧不搬。
        string comment = advanced ? string.Empty : symbols.Line + CommentSpace;
        string advStart = advanced ? symbols.Start + CommentSpace : string.Empty;
        string advEnd = advanced ? CommentSpace + symbols.End : string.Empty;
        // 三个长度在下面「选区回算」里反复要用，先算好缓存，省得每行重算
        int commentLength = comment.Length;
        int advStartLength = advStart.Length;
        int advEndLength = advEnd.Length;

        // 自己维护一份选区端点副本：逐行插/删的同时同步平移，全部改完再一次性还原
        int selectionStart = _scintilla.SelectionStart;
        int selectionEnd = _scintilla.SelectionEnd;
        bool moveCaret = _scintilla.CurrentPosition < selectionEnd;   // 光标停在选区开头

        // 选区落在哪几行：起止行号（同一行时两者相等，就是当前行）
        int selStartLine = _scintilla.LineFromPosition(selectionStart);
        int selEndLine = _scintilla.LineFromPosition(selectionEnd);
        // 选区尾正好落在下一行行首时，那一行不算进选区（ND2:2096）
        if (selEndLine > selStartLine && selectionEnd == LineStartPos(selEndLine)) selEndLine--;

        // 成功解开的行数：Uncomment/Toggle 收尾时据此决定要不要再试一次拆成对注释
        int nUncomments = 0;

        // 整个多行操作并成一个撤销单元：按一次 Ctrl+Z 就整体还原，不会一行一行地退
        _scintilla.BeginUndoAction();
        try
        {
            // 逐行独立判定——Toggle 模式下同一选区里可以有的行被加、有的行被删
            for (int i = selStartLine; i <= selEndLine; i++)
            {
                int lineStart = LineStartPos(i);                                  // 列 0 位置，用来判断这行有没有缩进
                int lineIndent = _scintilla.Lines[i].IndentPosition;   // 前缀插在缩进之后，不插在列 0
                int lineEnd = LineContentEndPos(i);                               // 行内容末尾（不含换行符），成对符号的止就插在这
                if (lineIndent == lineEnd) continue;                   // 空行不注释

                // 只取「缩进之后到行尾」这一段来做前缀判定，免受缩进空白干扰
                string linebuf = _scintilla.GetTextRange(lineIndent, lineEnd - lineIndent);

                // ---- 第 1 步：删除分支（Uncomment / Toggle 才进；Comment 直接跳到第 2 步）----
                if (mode != CommentMode.Comment)
                {
                    if (!advanced)
                    {
                        // 普通模式：认 //、#、' 这类行前缀
                        int len = LineCommentRemoveLength(linebuf, comment);
                        if (len >= 0)
                        {
                            // 命中：把选区摆到 [行缩进, 行缩进+len) 再整段替换为空，等价于删掉这段前缀
                            ReplaceRange(lineIndent, lineIndent + len, string.Empty);

                            // ND2:2144-2164 选区回算
                            if (i == selStartLine)
                            {
                                if (selectionStart > lineIndent + len) selectionStart -= len;       // 起点在被删段右侧：整体左移 len
                                else if (selectionStart > lineIndent) selectionStart = lineIndent;  // 起点落在被删段里：贴回行缩进
                            }
                            if (i == selEndLine)
                            {
                                if (selectionEnd > lineIndent + len) selectionEnd -= len;           // 终点在被删段右侧：左移 len
                                else if (selectionEnd > lineIndent)
                                {
                                    // 终点落在被删段里：贴回行缩进
                                    selectionEnd = lineIndent;
                                    // 唯一例外——首行无缩进且它不是选区首行时留一个字符，免得选区塌成零宽跑到下一行去
                                    if (lineIndent == lineStart && i != selStartLine) selectionEnd++;
                                }
                            }
                            else selectionEnd -= len;                                               // 中间行：终点在更下面，跟着本行删掉的长度一起左移

                            nUncomments++;                                                          // 这一行确实解开了
                            continue;                                                               // 该行处理完毕，不再走下面的添加分支
                        }
                    }
                    else if (StreamRemoveLengths(linebuf, advStart, advEnd) is { } lens)
                    {
                        // 高级模式：行首的 <!-- 和行尾的 --> 同时对上才算这一行被注释过；
                        // lens 里是两段各自要删的长度（挨着符号那一侧的空格可能不算在内）
                        ReplaceRange(lineIndent, lineIndent + lens.StartLen, string.Empty);                                   // 先删头部符号
                        ReplaceRange(lineEnd - lens.StartLen - lens.EndLen, lineEnd - lens.StartLen, string.Empty);          // 再删尾部符号：位置要扣掉刚删掉的头部长度

                        // ND2:2184-2210 选区回算（前后各删一段，分支比行注释多一层）
                        if (i == selStartLine)
                        {
                            if (selectionStart > lineEnd - lens.EndLen) selectionStart = lineEnd - lens.StartLen - lens.EndLen;  // 起点落在被删的尾段里：贴到正文末尾
                            else if (selectionStart > lineIndent + lens.StartLen) selectionStart -= lens.StartLen;               // 起点在两段之间：只被头部那次删除影响
                            else if (selectionStart > lineIndent) selectionStart = lineIndent;                                  // 起点在头段内部：贴回行缩进
                        }
                        if (i == selEndLine)
                        {
                            if (selectionEnd > lineEnd) selectionEnd -= lens.StartLen + lens.EndLen;                            // 终点在行尾符之后：两段都算删掉了
                            else if (selectionEnd > lineEnd - lens.EndLen) selectionEnd = lineEnd - lens.StartLen - lens.EndLen; // 终点在尾段内部：跟正文末尾对齐
                            else if (selectionEnd > lineIndent + lens.StartLen) selectionEnd -= lens.StartLen;                  // 终点在两段之间
                            else if (selectionEnd > lineIndent)
                            {
                                // 终点在头段内部：贴回行缩进
                                selectionEnd = lineIndent;
                                // 同样是「首行无缩进且不是选区首行」的例外，留一个字符防止选区塌成零宽
                                if (lineIndent == lineStart && i != selStartLine) selectionEnd++;
                            }
                        }
                        else selectionEnd -= lens.StartLen + lens.EndLen;                                                       // 中间行：终点在更下面，两段长度一起左移

                        nUncomments++;                                                                                          // 这一行的成对注释拆掉了
                        continue;                                                                                               // 不再走添加分支
                    }
                }

                // ---- 第 2 步：添加分支（Uncomment 永不进；Toggle 在第 1 步没删成的行上落到这里，即为「补上」）----
                if (mode != CommentMode.Uncomment)
                {
                    if (!advanced)
                    {
                        _scintilla.InsertText(lineIndent, comment);               // 在缩进之后插入「行注释符 + 空格」

                        // ND2:2225-2237 选区回算：本行内的端点跟着右移一个前缀长度
                        if (i == selStartLine && selectionStart >= lineIndent) selectionStart += commentLength;
                        if (i == selEndLine) { if (selectionEnd >= lineIndent) selectionEnd += commentLength; }
                        else selectionEnd += commentLength;                       // 非末行：终点在下面，同样要补偿本行插入的长度
                    }
                    else
                    {
                        _scintilla.InsertText(lineIndent, advStart);              // 先在缩进后插起符号
                        _scintilla.InsertText(lineEnd + advStartLength, advEnd);  // 再插止符号：lineEnd 已被上一次插入整体推后

                        // ND2:2245-2259 选区回算：末行内的端点只跟着起符号走，越过了正文末尾才连止符号一起算
                        if (i == selStartLine && selectionStart >= lineIndent) selectionStart += advStartLength;
                        if (i == selEndLine)
                        {
                            if (selectionEnd > lineEnd) selectionEnd += advStartLength + advEndLength;   // 落在行尾符之后：两段都补偿
                            else if (selectionEnd >= lineIndent) selectionEnd += advStartLength;         // 仍在正文内：只补偿起符号
                        }
                        else selectionEnd += advStartLength + advEndLength;                               // 中间行：整行的净增长是两段之和
                    }
                }
            }

            RestoreSelection(moveCaret, selectionStart, selectionEnd);   // 全部行改完，一次性把选区/光标摆回原位
        }
        finally
        {
            _scintilla.EndUndoAction();                                   // 撤销组收尾；出异常也要闭合，否则后续编辑会被并进同一组
        }

        // ND2:2277 —— cm_uncomment 一行都没解开时，再试着拆一次成对注释
        if (mode == CommentMode.Uncomment && nUncomments == 0) return UndoStreamComment(false);
        return true;
    }

    // ==================== 区块注释（doStreamComment） ====================

    /// <summary>
    /// ND2 <c>doStreamComment</c>（scintillaeditview.cpp:2521-2600）：在选区两端各插一段成对注释符号
    /// （起符号带尾空格、止符号带头空格），插完把选区整体右移并保持选中包住的内容。
    /// 这门语言没有成对符号时，退回行注释或直接不动作。
    /// </summary>
    /// <returns>true = 插上了成对注释；false = 没有成对符号且没有行注释符，什么都没做</returns>
    private bool DoStreamComment()
    {
        var symbols = CommentSymbols;

        // ND2:2552 —— 没有成对符号时，有行注释符就退回行注释，否则不动作
        if (symbols.Start.Length == 0 || symbols.End.Length == 0)
        {
            if (symbols.Line.Length > 0) return DoBlockComment(CommentMode.Comment);   // 退回「只加」模式，逐行加行前缀
            return false;
        }

        string startComment = symbols.Start + CommentSpace;   // 如 "/* "
        string endComment = CommentSpace + symbols.End;       // 如 " */"
        int startCommentLength = startComment.Length;

        int selectionStart = _scintilla.SelectionStart;
        int selectionEnd = _scintilla.SelectionEnd;
        bool moveCaret = _scintilla.CurrentPosition < selectionEnd;   // 光标原本停在选区开头（Shift+方向键选出来的那种）

        // ND2:2577 —— 没有选区时取的是「当前行的缩进位置到行尾」，不是整个文档
        if (selectionEnd - selectionStart <= 0)
        {
            int selLine = _scintilla.LineFromPosition(selectionStart);
            selectionStart = _scintilla.Lines[selLine].IndentPosition;   // 跳过缩进，注释符不贴在列 0
            selectionEnd = LineContentEndPos(selLine);                   // 到正文末尾为止，不含换行符
        }

        // 两次插入并进一个撤销单元：Ctrl+Z 一次就把成对符号整体撤掉
        _scintilla.BeginUndoAction();
        try
        {
            _scintilla.InsertText(selectionStart, startComment);   // 先插起符号——它会把后面的内容整体推后
            selectionEnd += startCommentLength;                    // 所以选区两端都要跟着右移
            selectionStart += startCommentLength;
            _scintilla.InsertText(selectionEnd, endComment);       // 再在（已右移的）选区尾插止符号
            RestoreSelection(moveCaret, selectionStart, selectionEnd);   // 保持选中原文，只是被包进了注释里
        }
        finally
        {
            _scintilla.EndUndoAction();
        }
        return true;
    }

    // ==================== 清除区块注释（undoStreamComment） ====================

    /// <summary>
    /// ND2 <c>undoStreamComment</c>（scintillaeditview.cpp:2324-2519）：以选区两端为锚点，
    /// 找包住选区的那一对成对注释符并删掉；找到一对就删一对，循环到再也找不到为止
    /// （ND2 原文是 do…while(1)，这里改成 while(true) + 内部 return，语义一致）。
    /// </summary>
    /// <param name="tryBlockComment">没有成对符号时，是否允许退回 <see cref="DoBlockComment"/> 去逐行解行注释。
    /// Ctrl+Shift+K 传 true；由 <see cref="DoBlockComment"/> 内部转来时传 false，防止两条路互相回调死循环</param>
    /// <returns>true = 至少拆掉了一对；false = 一对都没拆成（且回退也没做）</returns>
    private bool UndoStreamComment(bool tryBlockComment)
    {
        var symbols = CommentSymbols;

        // 成对符号不全：只认行注释的语言（Lua、MATLAB 等）在这里改走逐行解注释
        if (symbols.Start.Length == 0 || symbols.End.Length == 0)
        {
            if (symbols.Line.Length > 0 && tryBlockComment) return DoBlockComment(CommentMode.Uncomment);
            return false;   // 什么都不许做时返回 false，调用方（DoBlockComment）据此不会再次回灌
        }

        // ND2:2366-2370 —— 这里搜的是裸符号（不像 doStreamComment 那样拼空格），
        // 挨着符号那一侧的空格靠下面两次单字符探测决定是否一起删
        string startComment = symbols.Start;
        string endComment = symbols.End;
        int startCommentLength = startComment.Length;

        bool retVal = false;                                      // 一次都没删成过就是 false
        _scintilla.SearchFlags = SearchFlags.WordStart;   // ND2:2396 用的就是这个 flag，不是 None

        // ND2 是 do…while(1)：只要选区里还包着完整的一对就继续拆，拆不到才返回
        while (true)
        {
            // 每轮都重新读选区：上一轮删完已经把选区改掉了，位置不能复用缓存
            int selectionStart = _scintilla.SelectionStart;
            int selectionEnd = _scintilla.SelectionEnd;
            bool moveCaret = _scintilla.CurrentPosition < selectionEnd;
            int docLength = _scintilla.TextLength;

            // 四个方向的探测：从选区起点向前 / 向后，分别找起符号和止符号（to 传 0 即反向搜）
            int startBeforeS = SearchInTargetRange(startComment, selectionStart, 0);   // 起点之前最近的一个 /*
            int endBeforeS = SearchInTargetRange(endComment, selectionStart, 0);       // 起点之前最近的一个 */
            int startAfterS = SearchInTargetRange(startComment, selectionStart, docLength);  // 起点之后最近的一个 /*
            int endAfterS = SearchInTargetRange(endComment, selectionStart, docLength);      // 起点之后最近的一个 */

            int posStartComment, posEndComment;   // 本轮要删的那一对的位置

            // 情形一：选区起点被一对注释包住（前面有 /*、后面有 */，且中间没插进另一对）
            if (IsSurrounded(startBeforeS, endAfterS, endBeforeS, startAfterS))
            {
                posStartComment = startBeforeS;
                posEndComment = endAfterS;
            }
            else
            {
                // 情形二：起点这边不成立，换成从选区终点再探一轮四个方向
                int startBeforeE = SearchInTargetRange(startComment, selectionEnd, 0);
                int endBeforeE = SearchInTargetRange(endComment, selectionEnd, 0);
                int startAfterE = SearchInTargetRange(startComment, selectionEnd, docLength);
                int endAfterE = SearchInTargetRange(endComment, selectionEnd, docLength);

                if (IsSurrounded(startBeforeE, endAfterE, endBeforeE, startAfterE))
                {
                    // 终点被一对注释包住：就拆这一对
                    posStartComment = startBeforeE;
                    posEndComment = endAfterE;
                }
                else if (startAfterS != -1 && startAfterS < selectionEnd && endBeforeE != -1 && endBeforeE > selectionStart)
                {
                    // 选区里包着完整的一对：取选区起点之后的第一对（ND2:2449 用的是起点那组结果）
                    posStartComment = startAfterS;
                    posEndComment = endAfterS;
                }
                else return retVal;   // 三种情形都不成立：已拆到尽头，退出循环
            }

            retVal = true;
            int endLength = endComment.Length;

            // 先删尾巴，免得起点那次的向前搜索位置被改掉（ND2:2463）
            if (SliceClamped(posEndComment - 1, posEndComment) == CommentSpace)
            {
                // */ 前面挨着一个空格 → 一并删，并把止符号的位置前挪一格
                endLength += 1;
                posEndComment -= 1;
            }

            _scintilla.BeginUndoAction();
            try
            {
                ReplaceRange(posEndComment, posEndComment + endLength, string.Empty);   // 删掉止符号（含探测到的空格）

                // 再处理起符号：后面挨着的一个空格同样顺手带走（和上面止符号侧的探测对称）
                int startLength = startCommentLength;
                if (SliceClamped(posStartComment + startLength, posStartComment + startLength + 1) == CommentSpace)
                    startLength += 1;

                ReplaceRange(posStartComment, posStartComment + startLength, string.Empty);

                // ND2:2489-2505 选回算：夹在中间被删掉的部分按实际删掉的长度折算
                int selectionStartMove = 0, selectionEndMove;
                if (selectionStart > posStartComment)
                {
                    // 起点在起符号之后：整段被删则左移 startLength；起点落在被删段内部则贴回起符号处
                    selectionStartMove = selectionStart >= posStartComment + startLength
                        ? -startLength
                        : -selectionStart - posStartComment;
                }
                if (selectionEnd >= posEndComment + endLength) selectionEndMove = -startLength + endLength;   // 终点在止符号之后
                else if (selectionEnd <= posEndComment) selectionEndMove = -startLength;                      // 终点还在正文里：只受起符号影响
                else selectionEndMove = -startLength + (selectionEnd - posEndComment);                        // 终点在被删的止符号内部

                RestoreSelection(moveCaret, selectionStart + selectionStartMove, selectionEnd + selectionEndMove);
            }
            finally
            {
                _scintilla.EndUndoAction();   // 每一对单独成一个撤销单元（ND2 也是逐对包 undo 组）
            }
        }
    }

    /// <summary>ND2:2416-2418 的判据：这个位置前面有起符号、后面有止符号，且中间没有插进另一对</summary>
    /// <param name="startBefore">锚点之前（反向搜索）最近的起符号位置，-1 表示没有</param>
    /// <param name="endAfter">锚点之后（正向搜索）最近的止符号位置，-1 表示没有</param>
    /// <param name="endBefore">锚点之前的最近止符号位置——它若晚于 <paramref name="startBefore"/>，说明那对已经闭合过了</param>
    /// <param name="startAfter">锚点之后的最近起符号位置——它若早于 <paramref name="endAfter"/>，说明中间又起了新一对</param>
    /// <returns>true = 锚点被完整的一对注释符包着，可以拆</returns>
    private static bool IsSurrounded(int startBefore, int endAfter, int endBefore, int startAfter)
        => startBefore != -1 && endAfter != -1
        && (endBefore == -1 || startBefore >= endBefore)
        && (startAfter == -1 || endAfter <= startAfter);

    // ==================== 逐行判定的纯函数（dev/casetest 里直接跑） ====================

    /// <summary>ND2:2136 <c>qstrncmp(linebuf, comment, comment_length - 1)</c>：命中给出要删的长度，没命中返回 -1</summary>
    /// <param name="linebuf">该行「缩进之后到行尾」的内容</param>
    /// <param name="comment">带尾空格的前缀，例如双斜杠再加一个空格</param>
    /// <returns>要删掉的字符数（前缀带空格则含空格，不带则只删符号本体）；-1 表示这行没有该前缀</returns>
    internal static int LineCommentRemoveLength(string linebuf, string comment)
    {
        int n = comment.Length;
        // 只比对前 n-1 个字符（ND2 的 qstrncmp 就是这个长度）：最后一格可能是空格，也可能根本不是空格
        if (!linebuf.StartsWith(comment[..(n - 1)], StringComparison.Ordinal)) return -1;
        // 第 n 格确实是空格就把它也算进要删的长度，否则少删一格
        return CharAt(linebuf, n - 1) == ' ' ? n : n - 1;
    }

    /// <summary>
    /// ND2:2172-2176：行首对上 <c>/*</c>（不带尾空格）、行尾对上 <c>*/</c>（不带前空格）才算命中，
    /// 挨着符号那一侧的空格一并删。没命中返回 null。
    /// </summary>
    /// <param name="linebuf">该行「缩进之后到行尾」的内容</param>
    /// <param name="advStart">带尾空格的起符号，例如 HTML 的注释开头再跟一个空格</param>
    /// <param name="advEnd">带前空格的止符号，例如一个空格再跟 HTML 的注释结尾</param>
    /// <returns>命中时返回头/尾两段各自要删的长度；没命中返回 null</returns>
    internal static (int StartLen, int EndLen)? StreamRemoveLengths(string linebuf, string advStart, string advEnd)
    {
        int s = advStart.Length, e = advEnd.Length;
        // 头部去掉最后一格空格再比、尾部去掉第一格空格再比——这样「挨着符号的空格」可有可无都算命中
        if (!linebuf.StartsWith(advStart[..(s - 1)], StringComparison.Ordinal)) return null;
        if (!linebuf.EndsWith(advEnd[1..], StringComparison.Ordinal)) return null;
        // 各自再探测那一格空格在不在：在就多删一格
        return (CharAt(linebuf, s - 1) == ' ' ? s : s - 1, CharAt(linebuf, linebuf.Length - e) == ' ' ? e : e - 1);
    }

    /// <summary>QByteArray::operator[] 越界读到的是 '\0'，这里同样补一个 '\0'</summary>
    /// <param name="s">被读的字符串</param>
    /// <param name="index">下标，越界时不抛异常</param>
    /// <returns>正常字符；越界返回 '\0'（永远不等于空格，于是判定自然落到「不删空格」那一支）</returns>
    private static char CharAt(string s, int index) => index >= 0 && index < s.Length ? s[index] : '\0';

    // ==================== 位置与选区辅助 ====================

    /// <summary>ND2 searchInTarget(text, from, to)：from 大于 to 即反向搜索</summary>
    /// <param name="text">要搜的注释符号（裸符号，不带补的空格）</param>
    /// <param name="from">搜索起点（文档位置）</param>
    /// <param name="to">搜索终点；传 0 表示从 from 往前找</param>
    /// <returns>命中位置的文档偏移；没命中返回 -1</returns>
    private int SearchInTargetRange(string text, int from, int to)
    {
        // Scintilla 的目标区间 [TargetStart, TargetEnd) 由这两个值决定，方向由大小关系体现
        _scintilla.SetTargetRange(from, to);
        return _scintilla.SearchInTarget(text);
    }

    /// <summary>
    /// 取文档里 [from, to) 这一小段的文本，两端都先夹到 [0, 文档长度]，越界不抛异常。
    /// 用来探测注释符挨着的那一格是不是空格（ND2 的 QByteArray mid 越界只会拿到空）。
    /// </summary>
    /// <param name="from">起点文档偏移</param>
    /// <param name="to">终点文档偏移（不含）</param>
    /// <returns>该段文本；区间为空或越界到左侧时返回空串</returns>
    private string SliceClamped(int from, int to)
    {
        int max = _scintilla.TextLength;
        from = Math.Clamp(from, 0, max);
        to = Math.Clamp(to, 0, max);
        return to <= from ? string.Empty : _scintilla.GetTextRange(from, to - from);
    }

    /// <summary>SCI_SETSEL + SCI_REPLACESEL（ND2 的 replaceSelWith 替换的就是当前选区）</summary>
    /// <param name="from">被替换区间起点（两端都会夹到文档范围内）</param>
    /// <param name="to">被替换区间终点（不含）；与 from 相等时相当于在该处插入</param>
    /// <param name="text">替换进去的文本，本文件里一律传空串即「删除这段」</param>
    private void ReplaceRange(int from, int to, string text)
    {
        // 先把两端夹进文档，防止逐行增删过程中算出的位置短暂越界（ND2 的 QString 操作不会崩，这里要靠夹）
        int max = _scintilla.TextLength;
        from = Math.Clamp(from, 0, max);
        to = Math.Clamp(to, 0, max);
        _scintilla.SetSelection(to, from);   // 锚点在 to、光标在 from：选中 [from, to)
        _scintilla.ReplaceSelection(text);   // 直接替换当前选区，顺带把选区收缩成插入点
    }

    /// <summary>
    /// ND2 收尾那两句：光标原本停在选区开头，就先把锚点放到选区尾、再把光标拉回选区头；
    /// 否则锚点在前、光标在后。
    /// </summary>
    /// <param name="moveCaret">true = 操作前光标在选区开头（锚点在尾），还原时要保持这个方向</param>
    /// <param name="selectionStart">回算后的选区起点</param>
    /// <param name="selectionEnd">回算后的选区终点</param>
    private void RestoreSelection(bool moveCaret, int selectionStart, int selectionEnd)
    {
        if (moveCaret)
        {
            // 先塌成「光标在选区尾」的空选区，再把光标设回起点，等价于锚点在尾、光标在头的选区
            _scintilla.SetEmptySelection(selectionEnd);
            _scintilla.CurrentPosition = selectionStart;
        }
        else
        {
            _scintilla.SetSelection(selectionEnd, selectionStart);   // 锚点在头、光标在尾
        }
    }
}
