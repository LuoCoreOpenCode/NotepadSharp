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
    /// <summary>ND2 的 Comment_Mode（scintillaeditview.h:51）</summary>
    private enum CommentMode { Comment, Uncomment, Toggle }

    /// <summary>ND2 的 ADD_DEL_LINE_COM / ADD_BK_COM / DEL_BK_COM</summary>
    public enum CommentAction { AddDelLineComment, AddBlockComment, DelBlockComment }

    private const string CommentSpace = " ";

    /// <summary>
    /// ND2 <c>registerCommentShortKey</c>（ccnotepad.cpp:1635）注册的那三条快捷键最终都落到这个分发：
    /// Ctrl+Q 行注释开关、Ctrl+K 加区块注释、Ctrl+Shift+K 清区块注释。
    /// </summary>
    public void Comment(CommentAction action)
    {
        switch (action)
        {
            case CommentAction.AddDelLineComment:
                DoBlockComment(CommentMode.Toggle);
                break;
            case CommentAction.AddBlockComment:
                DoStreamComment();
                break;
            case CommentAction.DelBlockComment:
                UndoStreamComment(true);
                break;
        }
    }

    // ==================== 行注释 / 单行区块注释（doBlockComment） ====================

    private bool DoBlockComment(CommentMode mode)
    {
        var symbols = CommentSymbols;
        bool hasLine = symbols.Line.Length > 0;
        bool hasStream = symbols.Start.Length > 0 && symbols.End.Length > 0;

        // ND2:2023 —— 没有行注释符时，只要有成对符号就改走「整行包起来」的高级模式；两者都没有则不动作。
        // 这就是 HTML/XML/CSS/Pascal 的 Ctrl+Q 变成区块注释、而 C 系永远走行注释的原因。
        bool advanced = false;
        if (!hasLine)
        {
            if (!hasStream) return false;
            if (mode == CommentMode.Uncomment) return UndoStreamComment(false);
            advanced = true;
        }

        // ND2:2065-2085 前缀后面总带一个空格；BaanC 不带空格、且要注释空行、Fortran77 要贴在列 0，
        // 这三种语言本项目的语言表里都没有，相应特判照旧不搬。
        string comment = advanced ? string.Empty : symbols.Line + CommentSpace;
        string advStart = advanced ? symbols.Start + CommentSpace : string.Empty;
        string advEnd = advanced ? CommentSpace + symbols.End : string.Empty;
        int commentLength = comment.Length;
        int advStartLength = advStart.Length;
        int advEndLength = advEnd.Length;

        int selectionStart = _scintilla.SelectionStart;
        int selectionEnd = _scintilla.SelectionEnd;
        bool moveCaret = _scintilla.CurrentPosition < selectionEnd;   // 光标停在选区开头

        int selStartLine = _scintilla.LineFromPosition(selectionStart);
        int selEndLine = _scintilla.LineFromPosition(selectionEnd);
        // 选区尾正好落在下一行行首时，那一行不算进选区（ND2:2096）
        if (selEndLine > selStartLine && selectionEnd == LineStartPos(selEndLine)) selEndLine--;

        int nUncomments = 0;

        _scintilla.BeginUndoAction();
        try
        {
            for (int i = selStartLine; i <= selEndLine; i++)
            {
                int lineStart = LineStartPos(i);
                int lineIndent = _scintilla.Lines[i].IndentPosition;   // 前缀插在缩进之后，不插在列 0
                int lineEnd = LineContentEndPos(i);
                if (lineIndent == lineEnd) continue;                   // 空行不注释

                string linebuf = _scintilla.GetTextRange(lineIndent, lineEnd - lineIndent);

                if (mode != CommentMode.Comment)
                {
                    if (!advanced)
                    {
                        int len = LineCommentRemoveLength(linebuf, comment);
                        if (len >= 0)
                        {
                            ReplaceRange(lineIndent, lineIndent + len, string.Empty);

                            // ND2:2144-2164 选区回算
                            if (i == selStartLine)
                            {
                                if (selectionStart > lineIndent + len) selectionStart -= len;
                                else if (selectionStart > lineIndent) selectionStart = lineIndent;
                            }
                            if (i == selEndLine)
                            {
                                if (selectionEnd > lineIndent + len) selectionEnd -= len;
                                else if (selectionEnd > lineIndent)
                                {
                                    selectionEnd = lineIndent;
                                    if (lineIndent == lineStart && i != selStartLine) selectionEnd++;
                                }
                            }
                            else selectionEnd -= len;

                            nUncomments++;
                            continue;
                        }
                    }
                    else if (StreamRemoveLengths(linebuf, advStart, advEnd) is { } lens)
                    {
                        ReplaceRange(lineIndent, lineIndent + lens.StartLen, string.Empty);
                        ReplaceRange(lineEnd - lens.StartLen - lens.EndLen, lineEnd - lens.StartLen, string.Empty);

                        // ND2:2184-2210 选区回算（前后各删一段，分支比行注释多一层）
                        if (i == selStartLine)
                        {
                            if (selectionStart > lineEnd - lens.EndLen) selectionStart = lineEnd - lens.StartLen - lens.EndLen;
                            else if (selectionStart > lineIndent + lens.StartLen) selectionStart -= lens.StartLen;
                            else if (selectionStart > lineIndent) selectionStart = lineIndent;
                        }
                        if (i == selEndLine)
                        {
                            if (selectionEnd > lineEnd) selectionEnd -= lens.StartLen + lens.EndLen;
                            else if (selectionEnd > lineEnd - lens.EndLen) selectionEnd = lineEnd - lens.StartLen - lens.EndLen;
                            else if (selectionEnd > lineIndent + lens.StartLen) selectionEnd -= lens.StartLen;
                            else if (selectionEnd > lineIndent)
                            {
                                selectionEnd = lineIndent;
                                if (lineIndent == lineStart && i != selStartLine) selectionEnd++;
                            }
                        }
                        else selectionEnd -= lens.StartLen + lens.EndLen;

                        nUncomments++;
                        continue;
                    }
                }

                if (mode != CommentMode.Uncomment)
                {
                    if (!advanced)
                    {
                        _scintilla.InsertText(lineIndent, comment);

                        // ND2:2225-2237 选区回算
                        if (i == selStartLine && selectionStart >= lineIndent) selectionStart += commentLength;
                        if (i == selEndLine) { if (selectionEnd >= lineIndent) selectionEnd += commentLength; }
                        else selectionEnd += commentLength;
                    }
                    else
                    {
                        _scintilla.InsertText(lineIndent, advStart);
                        _scintilla.InsertText(lineEnd + advStartLength, advEnd);

                        // ND2:2245-2259 选区回算
                        if (i == selStartLine && selectionStart >= lineIndent) selectionStart += advStartLength;
                        if (i == selEndLine)
                        {
                            if (selectionEnd > lineEnd) selectionEnd += advStartLength + advEndLength;
                            else if (selectionEnd >= lineIndent) selectionEnd += advStartLength;
                        }
                        else selectionEnd += advStartLength + advEndLength;
                    }
                }
            }

            RestoreSelection(moveCaret, selectionStart, selectionEnd);
        }
        finally
        {
            _scintilla.EndUndoAction();
        }

        // ND2:2277 —— cm_uncomment 一行都没解开时，再试着拆一次成对注释
        if (mode == CommentMode.Uncomment && nUncomments == 0) return UndoStreamComment(false);
        return true;
    }

    // ==================== 区块注释（doStreamComment） ====================

    private bool DoStreamComment()
    {
        var symbols = CommentSymbols;

        // ND2:2552 —— 没有成对符号时，有行注释符就退回行注释，否则不动作
        if (symbols.Start.Length == 0 || symbols.End.Length == 0)
        {
            if (symbols.Line.Length > 0) return DoBlockComment(CommentMode.Comment);
            return false;
        }

        string startComment = symbols.Start + CommentSpace;
        string endComment = CommentSpace + symbols.End;
        int startCommentLength = startComment.Length;

        int selectionStart = _scintilla.SelectionStart;
        int selectionEnd = _scintilla.SelectionEnd;
        bool moveCaret = _scintilla.CurrentPosition < selectionEnd;

        // ND2:2577 —— 没有选区时取的是「当前行的缩进位置到行尾」，不是整个文档
        if (selectionEnd - selectionStart <= 0)
        {
            int selLine = _scintilla.LineFromPosition(selectionStart);
            selectionStart = _scintilla.Lines[selLine].IndentPosition;
            selectionEnd = LineContentEndPos(selLine);
        }

        _scintilla.BeginUndoAction();
        try
        {
            _scintilla.InsertText(selectionStart, startComment);
            selectionEnd += startCommentLength;
            selectionStart += startCommentLength;
            _scintilla.InsertText(selectionEnd, endComment);
            RestoreSelection(moveCaret, selectionStart, selectionEnd);
        }
        finally
        {
            _scintilla.EndUndoAction();
        }
        return true;
    }

    // ==================== 清除区块注释（undoStreamComment） ====================

    private bool UndoStreamComment(bool tryBlockComment)
    {
        var symbols = CommentSymbols;

        if (symbols.Start.Length == 0 || symbols.End.Length == 0)
        {
            if (symbols.Line.Length > 0 && tryBlockComment) return DoBlockComment(CommentMode.Uncomment);
            return false;
        }

        // ND2:2366-2370 —— 这里搜的是裸符号（不像 doStreamComment 那样拼空格），
        // 挨着符号那一侧的空格靠下面两次单字符探测决定是否一起删
        string startComment = symbols.Start;
        string endComment = symbols.End;
        int startCommentLength = startComment.Length;

        bool retVal = false;
        _scintilla.SearchFlags = SearchFlags.WordStart;   // ND2:2396 用的就是这个 flag，不是 None

        // ND2 是 do…while(1)：只要选区里还包着完整的一对就继续拆，拆不到才返回
        while (true)
        {
            int selectionStart = _scintilla.SelectionStart;
            int selectionEnd = _scintilla.SelectionEnd;
            bool moveCaret = _scintilla.CurrentPosition < selectionEnd;
            int docLength = _scintilla.TextLength;

            int startBeforeS = SearchInTargetRange(startComment, selectionStart, 0);
            int endBeforeS = SearchInTargetRange(endComment, selectionStart, 0);
            int startAfterS = SearchInTargetRange(startComment, selectionStart, docLength);
            int endAfterS = SearchInTargetRange(endComment, selectionStart, docLength);

            int posStartComment, posEndComment;

            if (IsSurrounded(startBeforeS, endAfterS, endBeforeS, startAfterS))
            {
                posStartComment = startBeforeS;
                posEndComment = endAfterS;
            }
            else
            {
                int startBeforeE = SearchInTargetRange(startComment, selectionEnd, 0);
                int endBeforeE = SearchInTargetRange(endComment, selectionEnd, 0);
                int startAfterE = SearchInTargetRange(startComment, selectionEnd, docLength);
                int endAfterE = SearchInTargetRange(endComment, selectionEnd, docLength);

                if (IsSurrounded(startBeforeE, endAfterE, endBeforeE, startAfterE))
                {
                    posStartComment = startBeforeE;
                    posEndComment = endAfterE;
                }
                else if (startAfterS != -1 && startAfterS < selectionEnd && endBeforeE != -1 && endBeforeE > selectionStart)
                {
                    // 选区里包着完整的一对：取选区起点之后的第一对（ND2:2449 用的是起点那组结果）
                    posStartComment = startAfterS;
                    posEndComment = endAfterS;
                }
                else return retVal;
            }

            retVal = true;
            int endLength = endComment.Length;

            // 先删尾巴，免得起点那次的向前搜索位置被改掉（ND2:2463）
            if (SliceClamped(posEndComment - 1, posEndComment) == CommentSpace)
            {
                endLength += 1;
                posEndComment -= 1;
            }

            _scintilla.BeginUndoAction();
            try
            {
                ReplaceRange(posEndComment, posEndComment + endLength, string.Empty);

                int startLength = startCommentLength;
                if (SliceClamped(posStartComment + startLength, posStartComment + startLength + 1) == CommentSpace)
                    startLength += 1;

                ReplaceRange(posStartComment, posStartComment + startLength, string.Empty);

                // ND2:2489-2505 选回算：夹在中间被删掉的部分按实际删掉的长度折算
                int selectionStartMove = 0, selectionEndMove;
                if (selectionStart > posStartComment)
                {
                    selectionStartMove = selectionStart >= posStartComment + startLength
                        ? -startLength
                        : -selectionStart - posStartComment;
                }
                if (selectionEnd >= posEndComment + endLength) selectionEndMove = -startLength + endLength;
                else if (selectionEnd <= posEndComment) selectionEndMove = -startLength;
                else selectionEndMove = -startLength + (selectionEnd - posEndComment);

                RestoreSelection(moveCaret, selectionStart + selectionStartMove, selectionEnd + selectionEndMove);
            }
            finally
            {
                _scintilla.EndUndoAction();
            }
        }
    }

    /// <summary>ND2:2416-2418 的判据：这个位置前面有起符号、后面有止符号，且中间没有插进另一对</summary>
    private static bool IsSurrounded(int startBefore, int endAfter, int endBefore, int startAfter)
        => startBefore != -1 && endAfter != -1
        && (endBefore == -1 || startBefore >= endBefore)
        && (startAfter == -1 || endAfter <= startAfter);

    // ==================== 逐行判定的纯函数（dev/casetest 里直接跑） ====================

    /// <summary>ND2:2136 <c>qstrncmp(linebuf, comment, comment_length - 1)</c>：命中给出要删的长度，没命中返回 -1</summary>
    internal static int LineCommentRemoveLength(string linebuf, string comment)
    {
        int n = comment.Length;
        if (!linebuf.StartsWith(comment[..(n - 1)], StringComparison.Ordinal)) return -1;
        return CharAt(linebuf, n - 1) == ' ' ? n : n - 1;
    }

    /// <summary>
    /// ND2:2172-2176：行首对上 <c>/*</c>（不带尾空格）、行尾对上 <c>*/</c>（不带前空格）才算命中，
    /// 挨着符号那一侧的空格一并删。没命中返回 null。
    /// </summary>
    internal static (int StartLen, int EndLen)? StreamRemoveLengths(string linebuf, string advStart, string advEnd)
    {
        int s = advStart.Length, e = advEnd.Length;
        if (!linebuf.StartsWith(advStart[..(s - 1)], StringComparison.Ordinal)) return null;
        if (!linebuf.EndsWith(advEnd[1..], StringComparison.Ordinal)) return null;
        return (CharAt(linebuf, s - 1) == ' ' ? s : s - 1, CharAt(linebuf, linebuf.Length - e) == ' ' ? e : e - 1);
    }

    /// <summary>QByteArray::operator[] 越界读到的是 '\0'，这里同样补一个 '\0'</summary>
    private static char CharAt(string s, int index) => index >= 0 && index < s.Length ? s[index] : '\0';

    // ==================== 位置与选区辅助 ====================

    /// <summary>ND2 searchInTarget(text, from, to)：from 大于 to 即反向搜索</summary>
    private int SearchInTargetRange(string text, int from, int to)
    {
        _scintilla.SetTargetRange(from, to);
        return _scintilla.SearchInTarget(text);
    }

    private string SliceClamped(int from, int to)
    {
        int max = _scintilla.TextLength;
        from = Math.Clamp(from, 0, max);
        to = Math.Clamp(to, 0, max);
        return to <= from ? string.Empty : _scintilla.GetTextRange(from, to - from);
    }

    /// <summary>SCI_SETSEL + SCI_REPLACESEL（ND2 的 replaceSelWith 替换的就是当前选区）</summary>
    private void ReplaceRange(int from, int to, string text)
    {
        int max = _scintilla.TextLength;
        from = Math.Clamp(from, 0, max);
        to = Math.Clamp(to, 0, max);
        _scintilla.SetSelection(to, from);
        _scintilla.ReplaceSelection(text);
    }

    /// <summary>
    /// ND2 收尾那两句：光标原本停在选区开头，就先把锚点放到选区尾、再把光标拉回选区头；
    /// 否则锚点在前、光标在后。
    /// </summary>
    private void RestoreSelection(bool moveCaret, int selectionStart, int selectionEnd)
    {
        if (moveCaret)
        {
            _scintilla.SetEmptySelection(selectionEnd);
            _scintilla.CurrentPosition = selectionStart;
        }
        else
        {
            _scintilla.SetSelection(selectionEnd, selectionStart);
        }
    }
}
