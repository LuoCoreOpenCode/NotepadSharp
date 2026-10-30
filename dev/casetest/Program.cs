using System;
using System.Linq;
using System.Text;

/// <summary>
/// dev/casetest 的入口：把 <see cref="Harness"/> 里的纯函数副本和 NotepadSharp.Services 下的被测静态方法
/// 喂给一批固定输入，逐条断言期望值，作为「改动主工程后行为没跑偏」的回归基线。
/// 期望值全部从 ND2（Qt/C++ 原版）逐行推导，连它的历史怪癖（大小写敏感、越界读 '\0' 等）也照抄——
/// 断言通过只代表复刻忠实，不代表行为合理；想「顺手修好」怪癖，必须先确认 ND2 侧结论，再动这里的期望值。
/// </summary>
internal static class Program
{
    /// <summary>失败断言的累计条数；0 = 全部通过（进程退出码 0），非 0 = 有回归（退出码 1，供脚本/CI 判定）</summary>
    private static int _fail;

    /// <summary>
    /// 唯一的断言原语：期望与实际都收敛成 string 比较。
    /// 通过时打印 PASS+用例名；失败时打印用例名和转义过的期望/实际，并累加 <see cref="_fail"/>。
    /// 数字、布尔等类型的用例在调用点先 ToString()，避免这里做泛型比较。
    /// </summary>
    /// <param name="name">用例名，同时是回归基线的一部分，请勿随意改写</param>
    /// <param name="expected">从 ND2 行为推导出的期望值</param>
    /// <param name="actual">当前实现的实测值</param>
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

    /// <summary>把 CR/LF 显示成字面量 \r \n，失败输出里能一眼看出差异是不是换行造成的</summary>
    /// <param name="s">要展示的原始字符串</param>
    /// <returns>换行符被转义后的展示文本</returns>
    private static string Show(string s) => s.Replace("\r", "\\r").Replace("\n", "\\n");

    /// <summary>跑一遍大小写转换的简写，省掉每个用例里重复写 Harness. 前缀</summary>
    /// <param name="text">原文</param>
    /// <param name="mode">ND2 的 TextCaseType 的等价枚举</param>
    /// <returns>转换结果，直接喂给 <see cref="Eq"/> 比对</returns>
    private static string C(string text, Harness.CaseMode mode) => Harness.ConvertCase(text, mode);

    /// <summary>
    /// 顺序跑完全部回归用例：大小写 → TAB/空格 → 注释 → URL → 编码。
    /// 用例顺序本身也是基线的一部分（失败输出按执行顺序读），新增用例追加在对应小节末尾。
    /// </summary>
    /// <returns>0 = 全部通过；1 = 至少一条失败（失败条数已打印）</returns>
    private static int Main()
    {
        // 最基础的两条：守住 Upper/Lower 直接映射到 ToUpper/ToLowerInvariant 的接线没被改坏
        Eq("转成大写", "HELLO WORLD", C("hello World", Harness.CaseMode.Upper));
        Eq("转成小写", "hello world", C("hello World", Harness.CaseMode.Lower));

        // 词首=前一个字符既不是字母也不是数字；FORCE 把非词首字母压小写，BLEND 原样保留
        // 这两条输入完全相同、只差 mode 一档：哪天 BLEND 的输出追上 FORCE，说明 force 标志或分发接反了
        Eq("每词 FORCE", "Hello World Foo2bar Baz'qux", C("hello WORLD foo2bar baz'qux", Harness.CaseMode.Proper));
        Eq("每词 BLEND", "Hello WORLD Foo2bar Baz'qux", C("hello WORLD foo2bar baz'qux", Harness.CaseMode.ProperBlend));
        // rock'n'roll：n 前是词首（该大写）、又被撇号例外压回小写，两步抵消才得到全小写的 n/r——规则改坏这里最先碎
        Eq("撇号例外", "Rock'n'roll", C("rock'n'roll", Harness.CaseMode.Proper));
        // BLEND 只把词首字母转大写，非词首字母原样保留；撇号例外会把词首那个字母再压回小写
        // 期望值 rOLL 里的 O 保持大写，守住「撇号例外只压紧跟撇号的那一个字母、不清扫其余」
        Eq("撇号例外(BLEND 保留其余)", "Rock'n'rOLL", C("rock'n'ROLL", Harness.CaseMode.ProperBlend));

        // 句末只认半角 . ! ?；紧跟字母数字不算新句（防 3.14）；连续两个 \r 或 \n 也算开新句
        // 这一条同时压住三个边界：句首 H 大写、3.14 后的 pi 不被误判成新句、空行后的 I 大写；
        // FORCE 还要求句中 it's 被压回小写
        Eq("句首 FORCE", "Hello world. It's 3.14 pi\r\n\r\nI am alone",
            C("hello world. it's 3.14 pi\r\n\r\ni am alone", Harness.CaseMode.Sentence));
        // BLEND 版：只抬句首字母，WORLD/BAR 这类原有大写不许被抹掉
        Eq("句首 BLEND", "Hello WORLD. Foo BAR", C("hello WORLD. foo BAR", Harness.CaseMode.SentenceBlend));
        // 中文全角「。」不是 ND2 认可的句末标记：foo 不能变 Foo。防止「顺手支持全角标点」偏离基线
        Eq("全角句号不算句末", "Hello 世界。foo BAR", C("hello 世界。foo BAR", Harness.CaseMode.SentenceBlend));
        // 只有孤立的 i 才升格 I（前邻空白/左括号/双引号、后邻空白/撇号）；I'm 的大写 I 是原文自带的
        Eq("孤立的 i 变 I", "Give I a book, (i) and I'm", C("give i a book, (i) and i'm", Harness.CaseMode.SentenceBlend));

        // 互换：小写→大写，其余一律转小写
        // 判定只问 IsLower：原大写、123、标点统统落到「转小写」分支（对数字标点即恒等操作），所以 ! 123 原样输出
        Eq("大小写互换", "hELLO, wORLD! 123", C("Hello, World! 123", Harness.CaseMode.Invert));

        // Random 模式的结果串不可复现（RandomBit 不固定种子），所以断言的是不变量：
        // 长度不变、全是字母、忽略大小写后与原文逐位相等——谁把 Random 改成「动了非字母字符」立刻会碎在这里
        string random = C("abcdefghij", Harness.CaseMode.Random);
        bool sameLetters = random.Length == 10 && random.All(char.IsLetter)
            && random.Select(char.ToLowerInvariant).SequenceEqual("abcdefghij");
        Eq("随机大小写只动字母", "true", sameLetters ? "true" : "false");

        // ---- spaceTabConvert：期望值全部按 ND2 ccnotepad.cpp:9270-9477 的 C++ 逐行推出来 ----
        // 局部函数：跑一遍 Harness.SpaceTabConvert 只取 Text 部分；
        // null（对应 ND2 的提前 return「无操作」）统一转成字面量 <无操作>，好走字符串断言；
        // caret 默认 -1，让不关心光标的用例不必重复传
        static string ST(string src, Harness.SpaceTabConversion type, int tabWidth = 4, int caret = -1)
            => Harness.SpaceTabConvert(src, tabWidth, caret, type).Text ?? "<无操作>";

        const Harness.SpaceTabConversion T2S = Harness.SpaceTabConversion.TabToSpace;         // 展开方向：TAB→空格
        const Harness.SpaceTabConversion S2A = Harness.SpaceTabConversion.SpaceToTabAll;      // 压缩方向：空格→TAB，整行所有空格串都参与
        const Harness.SpaceTabConversion S2L = Harness.SpaceTabConversion.SpaceToTabLeading;  // 压缩方向：空格→TAB，只处理行首空格串

        // 列 0 上的 TAB 补满一整档（tabWidth=4）：最普通的情形
        Eq("TAB→空格 行首", "    x", ST("\tx", T2S));
        // TAB 的补位量按当前列算：列 1 上的 TAB 只补 3 个空格，不是固定 4 个
        Eq("TAB→空格 列感知", "a   b", ST("a\tb", T2S));
        // 恰好落在制表位边界的 TAB 不省略，照样补满一整档（ND2 原样行为）
        Eq("TAB→空格 满列也补满一档", "        x", ST("    \tx", T2S));
        // 列计数在 \n 处归零：两行行首的 TAB 都从列 0 补 4，证明没有跨行累计列
        Eq("TAB→空格 按行清零列", "    a\n    b", ST("\ta\n\tb", T2S));
        // 中文只按 1 列计（ND2 数的是码元不是显示宽度），所以「中」后的 TAB 补 3 个空格
        Eq("TAB→空格 中文只计一列", "中   x", ST("中\tx", T2S));
        // 全文没有 TAB 时 ND2 连 undo 都不开、直接返回：守住这个 null 短路，别让实现改成「返回原文」
        Eq("TAB→空格 没 TAB 就无操作", "<无操作>", ST("ab", T2S));
        // 空文档同样走提前 return（ND2 条件 docLength<2）
        Eq("TAB→空格 空文档无操作", "<无操作>", ST("", T2S));

        // 行首连续 4 个空格恰好凑满一个制表位 → 并成一根 TAB（counter>=1 的主合并分支）
        Eq("空格→TAB 满 4 个并一根", "\tx", ST("    x", S2A));
        // 差一个凑不满就不并：3 空格原样保留，守「没到制表位绝不合并」
        Eq("空格→TAB 3 个不并", "   x", ST("   x", S2A));
        // 行首空格后面紧跟原文 TAB 时被「吸收」，合并成一根 TAB（输入 2 空格+TAB 输出仅 1 个 TAB）
        Eq("空格→TAB 被右边的 TAB 吸收", "\t", ST("  \t", S2A));
        // counter<1 的提前合并：列 3 上只要「后面还是空格」，一个空格也并成 TAB（abc 后第 1 个空格即被吃掉）
        Eq("空格→TAB 单空格后面还是空格也并", "abc\t ", ST("abc  ", S2A));
        // ND2 的原样行为：列 3 上那一个空格被「后面还是空格」规则提前并成 TAB，剩下三个补不回来，整体右移一列
        // （就是上一条怪癖在 4 空格场景的连锁后果——忠实复刻，不许「顺手修好」）
        Eq("空格→TAB 4 空格变 TAB+3 空格", "abc\t   x", ST("abc    x", S2A));
        // 原文里的 TAB 不参与合并，只负责推进列和 tabStop
        Eq("空格→TAB 原文里的 TAB 只是推列", "\t\tx", ST("\t\tx", S2A));
        // 换行把 column/tabStop/nonSpaceFound 全部复位：第二行的 4 空格重新攒够并成 TAB
        Eq("空格→TAB 跨行重新起算", "abc\n\tx", ST("abc\n    x", S2A));
        // ALL 版：x 后面的第二处空格串也照样并（TAB+空格+空格+TAB 混排），证明「全部」模式不停在第一个非空白
        Eq("空格→TAB (全部) 两处都并", "\tx\t y", ST("    x    y", S2A));
        // LEADING 版：本行第一个非空白字符之后不再转换，x 后面的 4 空格原样保留
        Eq("空格→TAB (行首) 第一个非空白之后不动", "\tx    y", ST("    x    y", S2L));

        // 光标点跟着搬：ND2 用 `i <= currentPos` 数，等号意味着当前字符本身也被算进去
        // TAB 恰好在光标下（caret=0）展开成 4 空格：4 个空格逐个计数，光标被推到展开段末尾的列 4
        Eq("光标搬运( TAB→空格)", "4",
            Harness.SpaceTabConvert("\tx", 4, 0, T2S).Caret.ToString());
        // 光标在列 4（x 上）：行首 4 空格并成 1 个 TAB 计一次，x 本身再计一次 → 光标落到结果的列 2（仍是 x）
        Eq("光标搬运(空格→TAB)", "2",
            Harness.SpaceTabConvert("    x", 4, 4, S2A).Caret.ToString());

        // ---- 注释三件套的两条逐行判定纯函数：期望值按 ND2 scintillaeditview.cpp:2136/2172 的 C++ 推出 ----
        // 局部函数：行注释命中时返回要删除的前缀长度、没命中返回 -1；转成字符串好走 Eq
        static string LCR(string linebuf, string comment) => Harness.LineCommentRemoveLength(linebuf, comment).ToString();
        // 局部函数：区块注释命中时输出 "起删长度+尾删长度"，任一侧没对上输出 <无>
        static string SRL(string linebuf, string advStart, string advEnd)
            => Harness.StreamRemoveLengths(linebuf, advStart, advEnd) is { } v ? $"{v.StartLen}+{v.EndLen}" : "<无>";

        // 前缀去掉自己那一格空格后再比，命中时挨着符号的空格一起删（2138）
        // "// foo" 配前缀 "// "：比对 "//" 命中且紧跟空格 → 连空格删 3 个
        Eq("行注释// 带空格删 3", "3", LCR("// foo", "// "));
        // "//foo" 紧跟的不是空格 → 只删符号本身 2 个
        Eq("行注释// 不带空格删 2", "2", LCR("//foo", "// "));
        // # 系同理（带空格删 2、不带删 1），见下两条
        Eq("行注释# 带空格删 2", "2", LCR("# x", "# "));
        Eq("行注释# 不带空格删 1", "1", LCR("#x", "# "));
        // REM 系同理：证明这套判定适用于任意长度前缀，而不是只硬编码了 "//"
        Eq("行注释REM 带空格删 4", "4", LCR("REM dir", "REM "));
        Eq("行注释REM 不带空格删 3", "3", LCR("REMdir", "REM "));
        // 匹配是从行首起的（StartsWith）：符号出现在行中间不算注释，返回 -1
        Eq("行注释前缀不在行首不算", "-1", LCR("x// y", "// "));
        // 整行就只有符号本身：QByteArray 越界读到 '\0'，走「不带空格」那一档
        Eq("行注释整行只有//", "2", LCR("//", "// "));
        // 整行比前缀还短：直接不算注释。防有人改成「短行前缀对上几个算几个」而误删正文
        Eq("行注释整行比符号还短", "-1", LCR("/", "// "));

        // 高级模式（HTML/CSS/Pascal 这类没有行注释符的语言）：行首比前 s-1 个、行尾比后 e-1 个
        // 首尾两侧都挨着空格：各多删一格，5+4
        Eq("区块<!-- 带空格", "5+4", SRL("<!-- foo -->", "<!-- ", " -->"));
        // 首尾都没有空格：只删符号本身，4+3
        Eq("区块<!-- 不带空格", "4+3", SRL("<!--foo-->", "<!-- ", " -->"));
        // 两侧独立判定：/*foo */ 头部不带空格删 2、尾部带空格删 3，互不影响
        Eq("区块/* */ 只带一侧空格", "2+3", SRL("/*foo */", "/* ", " */"));
        // 行首对不上（<div> 不是 <!--）即使行尾对得上也整行不动
        Eq("区块头对不上不算", "<无>", SRL("<div> -->", "<!-- ", " -->"));
        // 行尾对不上同样整行不动：宁可留着半个注释符，也不能删错正文
        Eq("区块尾对不上不算", "<无>", SRL("<!-- foo", "<!-- ", " -->"));
        // R 语言的双引号区块："x" 首尾各删 1（前缀 "\" " 去掉尾空格只剩一个引号）
        Eq("区块R 的双引号", "1+1", SRL("\"x\"", "\" ", " \""));

        // ---- URL 状态机：期望值按 ND2 scintillaeditview.cpp:3344-3600 的 C++ 逐行推出来 ----
        // 段序列里 U 前缀 = 判成 URL，- 前缀 = 不是（这些段会被 CLEARRANGE）
        // 局部函数：把整段文本切成 URL/非 URL 段，用 | 拼成一行方便肉眼逐段比对
        static string Scan(string text)
            => string.Join("|", NotepadSharp.Services.UrlScanner.ScanSegments(text)
                .Select(s => (s.IsUrl ? "U" : "-") + text.Substring(s.Start, s.Length)));
        // 局部函数：对整串从下标 0 走一遍 IsUrl，命中输出截出的 URL 本身，否则输出 <不是>
        static string OneUrl(string text)
            => NotepadSharp.Services.UrlScanner.IsUrl(text, 0, out int len) ? text.Substring(0, len) : "<不是>";

        // 最小正例：合法 URL + 空格，空格起必须是非 URL 段（热区不能吃到正文）
        Eq("URL 基本", "Uhttps://a.com|- x", Scan("https://a.com x"));
        // URL 识别只看 scheme 起点，前面的中文和空格不构成障碍
        Eq("URL 前有中文", "-中文 |Uhttps://a.com", Scan("中文 https://a.com"));
        // 只放行 http/https：mailto: 若被高亮，Ctrl+点击会打不开还误导用户
        Eq("URL 只认 http/https", "<不是>", OneUrl("mailto:foo@bar.com"));
        // scheme 比较大小写敏感：HTTP:// 整段都不认。ND2 原样怪癖，别「顺手」改成忽略大小写
        Eq("URL scheme 大小写敏感（ND2 原样）", "-HTTP|-://A.COM", Scan("HTTP://A.COM"));
        // 句点是英文句末最常见符号，尾巴的点不许进 URL（不然点开 a.com. 直接 404）
        Eq("URL 剥掉尾点", "Uhttps://a.com|-.", Scan("https://a.com."));
        // 有效性在剥尾字符「之前」判（ND2 :3583 在 :3586 之前），所以分号得落在路径里才轮得到剥
        Eq("URL 尾分号在路径里才剥", "Uhttps://a.com/x|-;;", Scan("https://a.com/x;;"));
        // 只有一个右括号时整段里没有左括号 → 按 ND2 的配平逻辑照样剥掉
        Eq("URL 尾右括号（无左括号）剥掉", "Uhttps://a.com/x|-)", Scan("https://a.com/x)"));
        // 有左括号就保留，wiki 那种 Function_(mathematics) 不会被吃掉
        Eq("URL 成对括号保留", "Uhttps://a.com/x(y)", Scan("https://a.com/x(y)"));
        // 进入 query（? 之后）就一路吃到空白：= & ; 都是合法 query 内容，不许把 URL 切断
        // 括号里点名的 digitValue 分支是 ND2 的死代码（恒不命中），这里守住没人把它「修活」
        Eq("URL 问号里的 & = ; 不断开（ND2 的 digitValue 分支恒不命中）",
            "Uhttps://a.com/?q=1&b=2;c=3", Scan("https://a.com/?q=1&b=2;c=3"));
        // # 之后的 fragment 同样一路吃到下一个空白，中间的空格才是唯一终点
        Eq("URL 井号后一直算到空白", "Uhttps://a.com/a#b?c|- d e", Scan("https://a.com/a#b?c d e"));
        // 光有 scheme、后面空无一物不算 URL（等价 QUrl::isValid 要求非空主机名）
        Eq("URL 只有 scheme 不算", "<不是>", OneUrl("https://"));
        // isUrlTextChar 的「排除 # < > ? 」全是死分支，所以中文、尖括号、问号都能进 URL
        Eq("URL 里的中文不截断（ND2 的 digitValue 分支恒不命中）", "Uhttps://a.com/中", Scan("https://a.com/中"));
        // ND2 的 scanToUrlStart 判的是绝对下标 0：段首（相对 1）的 scheme 认不出来
        // 期望 False/13：整段 13 个字符全按非 URL 处理。守住这个越界复刻，别让「修复」它改变热区切分
        Eq("URL 段首非绝对 0 不认 scheme（ND2 原样）", "False/13",
            NotepadSharp.Services.UrlScanner.IsUrl("xhttps://a.com", 1, out int quirkLen)
                ? "True/" + quirkLen : "False/" + quirkLen);
        // 字节偏移：中文 3 个字符 = 7 个 UTF-8 字节（ND2 addHotSpot 按段累加 toUtf8().size()）
        // 守的是「码元坐标 → UTF-8 字节坐标」的换算：错一个字节，热区/点击定位就整体偏移
        Eq("URL 中文前缀的字节长度", "7",
            NotepadSharp.Services.UrlScanner.Utf8Bytes("中文 https://a.com", 0, 3).ToString());

        // QUrl::isValid 的等价判定：http/https 且必须有主机名
        Eq("QUrl 等价 http://a.com", "True", NotepadSharp.Services.UrlScanner.IsValidUrl("http://a.com").ToString());  // 合法最小样例：scheme+主机齐活
        Eq("QUrl 等价 http:// 无主机", "False", NotepadSharp.Services.UrlScanner.IsValidUrl("http://").ToString());  // 只有 scheme 没有 host，不合法
        Eq("QUrl 等价 空主机带端口", "False", NotepadSharp.Services.UrlScanner.IsValidUrl("http://:8080").ToString());  // 带端口也没用：host 为空一票否决

        // ---- EncodingService：BOM 先行（CmpareMode.cpp:101-117） ----
        // 用户报的 bug：UTF-16LE BOM 的 汉化说明.txt 被当成二进制。现在判定只看 BOM + 整篇能否解码，
        // 不再数 NUL（ND2 没有内容嗅探这一步）。下面三条 u16 用例就是这个 bug 的锁死桩，一条都不能松。
        // 局部函数：造一段「UTF-16LE BOM + 正文」的真字节；U+000D/000A 等在高字节位全是 0x00，
        // 正是当年被「数 NUL 判二进制」误杀的形态
        static byte[] U16Le(string s) => new byte[] { 0xFF, 0xFE }.Concat(Encoding.Unicode.GetBytes(s)).ToArray();

        byte[] u16 = U16Le("一、更新历史\r\n1. 修复若干问题\r\n");                              // 用户真实文件的浓缩版
        var (u16Cp, u16Bom) = NotepadSharp.Services.EncodingService.Detect(u16);               // 第一步：只根据 BOM 探测
        Eq("UTF-16LE BOM 探测", "1200/True", u16Cp + "/" + u16Bom);                             // 必须给出 1200 + 带 BOM，落错分支就会被当二进制
        var open16 = NotepadSharp.Services.EncodingService.DecodeForOpen(u16, u16Cp, u16Bom);  // 第二步：按探测结果解码
        Eq("UTF-16LE BOM 解码", "一、更新历史\r\n1. 修复若干问题\r\n", open16.Text);            // BOM 要被剥掉、正文逐字符还原、\r\n 不许被改写
        Eq("UTF-16LE BOM 不乱码", "False", open16.Garbled.ToString());                          // 合法 UTF-16 绝不许标乱码，否则又弹「文本还是二进制」

        // UTF-16BE：BOM 是 FE FF（和 LE 的 FF FE 只差字节序），顺序判反就会把 LE 文件按 BE 解出满屏乱码
        var (beCp, beBom) = NotepadSharp.Services.EncodingService.Detect(new byte[] { 0xFE, 0xFF, 0x00, 0x41 });
        Eq("UTF-16BE BOM 探测", "1201/True", beCp + "/" + beBom);
        // UTF-8 BOM：EF BB BF 三字节 → 65001 且 Bom=true（写回时靠这个标志补回 BOM）
        var (u8Cp, u8Bom) = NotepadSharp.Services.EncodingService.Detect(new byte[] { 0xEF, 0xBB, 0xBF, 0x41 });
        Eq("UTF-8 BOM 探测", "65001/True", u8Cp + "/" + u8Bom);

        // 已知二进制后缀表（doctypelistview.cpp:21-22），ND2 按原样比较，大小写敏感
        // .exe 在表里：命中就直开十六进制视图/跳过文本解码，不再浪费时间解字节
        Eq("exe 后缀进 hex", "True", NotepadSharp.Services.BinaryFileTypes.IsHexExtension(@"C:\x\a.exe").ToString());
        // .bin 不在 ND2 表里：名单不许自作主张扩大，改了这里目录搜索行为就和 ND2 对不上
        Eq("bin 不在 ND2 hex 表", "False", NotepadSharp.Services.BinaryFileTypes.IsHexExtension(@"C:\x\a.bin").ToString());
        // 无后缀的 README 不算二进制（取不到扩展名就返回 false）
        Eq("无后缀不进 hex", "False", NotepadSharp.Services.BinaryFileTypes.IsHexExtension(@"C:\x\README").ToString());
        // 大写 PNG 不匹配：ND2 比较是大小写敏感的 Ordinal——守住「别顺手 ToLower」
        Eq("大写 PNG 不匹配（ND2 大小写敏感）", "False", NotepadSharp.Services.BinaryFileTypes.IsHexExtension(@"C:\x\a.PNG").ToString());

        // ND2 scanFileOutPut：当前编码解不开就无条件回落 GBK 再试，仍失败才置乱码
        // （注意 .NET 的 936 把 0xFF 映射成 U+F8F5，不会报错；0x81 0x20 才是真非法序列）
        // 用真非法序列 0x81 0x20 喂 GBK：严格解码必须失败并标 Garbled（ND2 据此弹「打开为文本或二进制」）
        var gbkFail = NotepadSharp.Services.EncodingService.DecodeForOpen(
            new byte[] { 0x41, 0x81, 0x20, 0x42 }, NotepadSharp.Services.EncodingService.CpGbk, false);
        Eq("GBK 解不出的字节判乱码", "True", gbkFail.Garbled.ToString());
        // 往返验证：Encode("中文",936) 出来的合法 GBK 字节必须原样解开且不标乱码
        var gbkOk = NotepadSharp.Services.EncodingService.DecodeForOpen(
            NotepadSharp.Services.EncodingService.Encode("中文", NotepadSharp.Services.EncodingService.CpGbk, false),
            NotepadSharp.Services.EncodingService.CpGbk, false);
        Eq("GBK 正常文本不乱码", "中文/False", gbkOk.Text + "/" + gbkOk.Garbled);
        // 0x81 0x20 0x41 按 UTF-16LE 解不开 → 回落 936；返回的 CodePage 必须一并改成 936，
        // 否则调用方状态栏显示的编码和正文字节对不上（True=回落后仍判乱码）
        var u16Fail = NotepadSharp.Services.EncodingService.DecodeForOpen(
            new byte[] { 0x81, 0x20, 0x41 }, NotepadSharp.Services.EncodingService.CpUtf16Le, false);
        Eq("UTF-16 解不开时回落 GBK（编码一并改掉）", "936/True", u16Fail.CodePage + "/" + u16Fail.Garbled);

        // 冒烟用例的字节：0x41 0x81 0x20 0x42 0x43，全流程走 Detect → DecodeForOpen
        // 曾因候选码页里的 20878（CP950）在部分环境抛 NotSupportedException，直接炸成「打开失败」弹窗；
        // 现在无论探测落到哪个候选，最终都必须安静地给出 Garbled=true，而不是抛异常
        var smokeBytes = new byte[] { 0x41, 0x81, 0x20, 0x42, 0x43 };
        var (smokeCp, smokeBom) = NotepadSharp.Services.EncodingService.Detect(smokeBytes);
        var smokeOpen = NotepadSharp.Services.EncodingService.DecodeForOpen(smokeBytes, smokeCp, smokeBom);
        Eq("冒烟样例判乱码（曾因 20878 代码页抛 NotSupported 而报打开失败）", "True", smokeOpen.Garbled.ToString());

        Console.WriteLine(_fail == 0 ? "全部通过" : _fail + " 条失败");   // 汇总行：给脚本/人眼看的最终结论
        return _fail == 0 ? 0 : 1;                                        // 退出码非 0 即有回归，供 CI 判定
    }
}
