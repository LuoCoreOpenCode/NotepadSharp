using System;
using System.Linq;

internal static class Program
{
    private static int _fail;

    private static void Eq(string name, string expected, string actual)
    {
        if (expected == actual)
        {
            Console.WriteLine("PASS " + name);
            return;
        }

        _fail++;
        Console.WriteLine($"FAIL {name}\n  期望 <{Show(expected)}>\n  实际 <{Show(actual)}>");
    }

    private static string Show(string s) => s.Replace("\r", "\\r").Replace("\n", "\\n");

    private static string C(string text, Harness.CaseMode mode) => Harness.ConvertCase(text, mode);

    private static int Main()
    {
        Eq("转成大写", "HELLO WORLD", C("hello World", Harness.CaseMode.Upper));
        Eq("转成小写", "hello world", C("hello World", Harness.CaseMode.Lower));

        // 词首=前一个字符既不是字母也不是数字；FORCE 把非词首字母压小写，BLEND 原样保留
        Eq("每词 FORCE", "Hello World Foo2bar Baz'qux", C("hello WORLD foo2bar baz'qux", Harness.CaseMode.Proper));
        Eq("每词 BLEND", "Hello WORLD Foo2bar Baz'qux", C("hello WORLD foo2bar baz'qux", Harness.CaseMode.ProperBlend));
        Eq("撇号例外", "Rock'n'roll", C("rock'n'roll", Harness.CaseMode.Proper));
        // BLEND 只把词首字母转大写，非词首字母原样保留；撇号例外会把词首那个字母再压回小写
        Eq("撇号例外(BLEND 保留其余)", "Rock'n'rOLL", C("rock'n'ROLL", Harness.CaseMode.ProperBlend));

        // 句末只认半角 . ! ?；紧跟字母数字不算新句（防 3.14）；连续两个 \r 或 \n 也算开新句
        Eq("句首 FORCE", "Hello world. It's 3.14 pi\r\n\r\nI am alone",
            C("hello world. it's 3.14 pi\r\n\r\ni am alone", Harness.CaseMode.Sentence));
        Eq("句首 BLEND", "Hello WORLD. Foo BAR", C("hello WORLD. foo BAR", Harness.CaseMode.SentenceBlend));
        Eq("全角句号不算句末", "Hello 世界。foo BAR", C("hello 世界。foo BAR", Harness.CaseMode.SentenceBlend));
        Eq("孤立的 i 变 I", "Give I a book, (i) and I'm", C("give i a book, (i) and i'm", Harness.CaseMode.SentenceBlend));

        // 互换：小写→大写，其余一律转小写
        Eq("大小写互换", "hELLO, wORLD! 123", C("Hello, World! 123", Harness.CaseMode.Invert));

        string random = C("abcdefghij", Harness.CaseMode.Random);
        bool sameLetters = random.Length == 10 && random.All(char.IsLetter)
            && random.Select(char.ToLowerInvariant).SequenceEqual("abcdefghij");
        Eq("随机大小写只动字母", "true", sameLetters ? "true" : "false");

        // ---- spaceTabConvert：期望值全部按 ND2 ccnotepad.cpp:9270-9477 的 C++ 逐行推出来 ----
        static string ST(string src, Harness.SpaceTabConversion type, int tabWidth = 4, int caret = -1)
            => Harness.SpaceTabConvert(src, tabWidth, caret, type).Text ?? "<无操作>";

        const Harness.SpaceTabConversion T2S = Harness.SpaceTabConversion.TabToSpace;
        const Harness.SpaceTabConversion S2A = Harness.SpaceTabConversion.SpaceToTabAll;
        const Harness.SpaceTabConversion S2L = Harness.SpaceTabConversion.SpaceToTabLeading;

        Eq("TAB→空格 行首", "    x", ST("\tx", T2S));
        Eq("TAB→空格 列感知", "a   b", ST("a\tb", T2S));
        Eq("TAB→空格 满列也补满一档", "        x", ST("    \tx", T2S));
        Eq("TAB→空格 按行清零列", "    a\n    b", ST("\ta\n\tb", T2S));
        Eq("TAB→空格 中文只计一列", "中   x", ST("中\tx", T2S));
        Eq("TAB→空格 没 TAB 就无操作", "<无操作>", ST("ab", T2S));
        Eq("TAB→空格 空文档无操作", "<无操作>", ST("", T2S));

        Eq("空格→TAB 满 4 个并一根", "\tx", ST("    x", S2A));
        Eq("空格→TAB 3 个不并", "   x", ST("   x", S2A));
        Eq("空格→TAB 被右边的 TAB 吸收", "\t", ST("  \t", S2A));
        Eq("空格→TAB 单空格后面还是空格也并", "abc\t ", ST("abc  ", S2A));
        // ND2 的原样行为：列 3 上那一个空格被「后面还是空格」规则提前并成 TAB，剩下三个补不回来，整体右移一列
        Eq("空格→TAB 4 空格变 TAB+3 空格", "abc\t   x", ST("abc    x", S2A));
        Eq("空格→TAB 原文里的 TAB 只是推列", "\t\tx", ST("\t\tx", S2A));
        Eq("空格→TAB 跨行重新起算", "abc\n\tx", ST("abc\n    x", S2A));
        Eq("空格→TAB (全部) 两处都并", "\tx\t y", ST("    x    y", S2A));
        Eq("空格→TAB (行首) 第一个非空白之后不动", "\tx    y", ST("    x    y", S2L));

        // 光标点跟着搬：ND2 用 `i <= currentPos` 数，等号意味着当前字符本身也被算进去
        Eq("光标搬运( TAB→空格)", "4",
            Harness.SpaceTabConvert("\tx", 4, 0, T2S).Caret.ToString());
        Eq("光标搬运(空格→TAB)", "2",
            Harness.SpaceTabConvert("    x", 4, 4, S2A).Caret.ToString());

        // ---- 注释三件套的两条逐行判定纯函数：期望值按 ND2 scintillaeditview.cpp:2136/2172 的 C++ 推出 ----
        static string LCR(string linebuf, string comment) => Harness.LineCommentRemoveLength(linebuf, comment).ToString();
        static string SRL(string linebuf, string advStart, string advEnd)
            => Harness.StreamRemoveLengths(linebuf, advStart, advEnd) is { } v ? $"{v.StartLen}+{v.EndLen}" : "<无>";

        // 前缀去掉自己那一格空格后再比，命中时挨着符号的空格一起删（2138）
        Eq("行注释// 带空格删 3", "3", LCR("// foo", "// "));
        Eq("行注释// 不带空格删 2", "2", LCR("//foo", "// "));
        Eq("行注释# 带空格删 2", "2", LCR("# x", "# "));
        Eq("行注释# 不带空格删 1", "1", LCR("#x", "# "));
        Eq("行注释REM 带空格删 4", "4", LCR("REM dir", "REM "));
        Eq("行注释REM 不带空格删 3", "3", LCR("REMdir", "REM "));
        Eq("行注释前缀不在行首不算", "-1", LCR("x// y", "// "));
        // 整行就只有符号本身：QByteArray 越界读到 '\0'，走「不带空格」那一档
        Eq("行注释整行只有//", "2", LCR("//", "// "));
        Eq("行注释整行比符号还短", "-1", LCR("/", "// "));

        // 高级模式（HTML/CSS/Pascal 这类没有行注释符的语言）：行首比前 s-1 个、行尾比后 e-1 个
        Eq("区块<!-- 带空格", "5+4", SRL("<!-- foo -->", "<!-- ", " -->"));
        Eq("区块<!-- 不带空格", "4+3", SRL("<!--foo-->", "<!-- ", " -->"));
        Eq("区块/* */ 只带一侧空格", "2+3", SRL("/*foo */", "/* ", " */"));
        Eq("区块头对不上不算", "<无>", SRL("<div> -->", "<!-- ", " -->"));
        Eq("区块尾对不上不算", "<无>", SRL("<!-- foo", "<!-- ", " -->"));
        Eq("区块R 的双引号", "1+1", SRL("\"x\"", "\" ", " \""));

        // ---- URL 状态机：期望值按 ND2 scintillaeditview.cpp:3344-3600 的 C++ 逐行推出来 ----
        // 段序列里 U 前缀 = 判成 URL，- 前缀 = 不是（这些段会被 CLEARRANGE）
        static string Scan(string text)
            => string.Join("|", NotepadSharp.Services.UrlScanner.ScanSegments(text)
                .Select(s => (s.IsUrl ? "U" : "-") + text.Substring(s.Start, s.Length)));
        static string OneUrl(string text)
            => NotepadSharp.Services.UrlScanner.IsUrl(text, 0, out int len) ? text.Substring(0, len) : "<不是>";

        Eq("URL 基本", "Uhttps://a.com|- x", Scan("https://a.com x"));
        Eq("URL 前有中文", "-中文 |Uhttps://a.com", Scan("中文 https://a.com"));
        Eq("URL 只认 http/https", "<不是>", OneUrl("mailto:foo@bar.com"));
        Eq("URL scheme 大小写敏感（ND2 原样）", "-HTTP|-://A.COM", Scan("HTTP://A.COM"));
        Eq("URL 剥掉尾点", "Uhttps://a.com|-.", Scan("https://a.com."));
        // 有效性在剥尾字符「之前」判（ND2 :3583 在 :3586 之前），所以分号得落在路径里才轮得到剥
        Eq("URL 尾分号在路径里才剥", "Uhttps://a.com/x|-;;", Scan("https://a.com/x;;"));
        // 只有一个右括号时整段里没有左括号 → 按 ND2 的配平逻辑照样剥掉
        Eq("URL 尾右括号（无左括号）剥掉", "Uhttps://a.com/x|-)", Scan("https://a.com/x)"));
        // 有左括号就保留，wiki 那种 Function_(mathematics) 不会被吃掉
        Eq("URL 成对括号保留", "Uhttps://a.com/x(y)", Scan("https://a.com/x(y)"));
        Eq("URL 问号里的 & = ; 不断开（ND2 的 digitValue 分支恒不命中）",
            "Uhttps://a.com/?q=1&b=2;c=3", Scan("https://a.com/?q=1&b=2;c=3"));
        Eq("URL 井号后一直算到空白", "Uhttps://a.com/a#b?c|- d e", Scan("https://a.com/a#b?c d e"));
        Eq("URL 只有 scheme 不算", "<不是>", OneUrl("https://"));
        // isUrlTextChar 的「排除 # < > ? 」全是死分支，所以中文、尖括号、问号都能进 URL
        Eq("URL 里的中文不截断（ND2 的 digitValue 分支恒不命中）", "Uhttps://a.com/中", Scan("https://a.com/中"));
        // ND2 的 scanToUrlStart 判的是绝对下标 0：段首（相对 1）的 scheme 认不出来
        Eq("URL 段首非绝对 0 不认 scheme（ND2 原样）", "False/13",
            NotepadSharp.Services.UrlScanner.IsUrl("xhttps://a.com", 1, out int quirkLen)
                ? "True/" + quirkLen : "False/" + quirkLen);
        // 字节偏移：中文 3 个字符 = 7 个 UTF-8 字节（ND2 addHotSpot 按段累加 toUtf8().size()）
        Eq("URL 中文前缀的字节长度", "7",
            NotepadSharp.Services.UrlScanner.Utf8Bytes("中文 https://a.com", 0, 3).ToString());

        // QUrl::isValid 的等价判定：http/https 且必须有主机名
        Eq("QUrl 等价 http://a.com", "True", NotepadSharp.Services.UrlScanner.IsValidUrl("http://a.com").ToString());
        Eq("QUrl 等价 http:// 无主机", "False", NotepadSharp.Services.UrlScanner.IsValidUrl("http://").ToString());
        Eq("QUrl 等价 空主机带端口", "False", NotepadSharp.Services.UrlScanner.IsValidUrl("http://:8080").ToString());

        Console.WriteLine(_fail == 0 ? "全部通过" : _fail + " 条失败");
        return _fail == 0 ? 0 : 1;
    }
}
