// 语法表的数据源与口径说明：
// 参考实现 ND2 把语言定义放在外置配置里（langs.xml 的语言/后缀/词法器/注释符号表，
// 加上 stylers.xml 与 QsciLexer 的 style 编号表）；本工具没有引入 XML 加载器，
// 而是把这些表 1:1 硬编码成下面的 switch 与关键字常量，所以「表数据」就是本文件的唯一真相源。
using System;
using System.IO;

namespace NotepadSharp.Services;

/// <summary>
/// 语法高亮服务：将文件扩展名映射到 Lexilla lexer 名称
/// </summary>
/// <remarks>
/// 本类是「纯查表」服务，不持有控件也不改状态，四个职责分别对应参考实现语言表的四列：
/// 1）<see cref="GetLexerName"/>：后缀（含少数无后缀的特殊文件名）→ Lexilla 词法器名，
///    对应语言表里 ext → lexer 名 那一列；
/// 2）<see cref="GetLanguageName"/>：后缀 → 给用户看的语言显示名，对应「语言」菜单与状态栏的标题，
///    与词法器名不是一回事（多个显示名可以共用一个词法器）；
/// 3）<see cref="GetCommentSymbols"/>：词法器名 → 行注释符 + 成对区块注释符，
///    对应 ND2 挂在 QsciLexer 实例上的 m_commentSymbol / m_commentStart / m_commentEnd；
/// 4）<see cref="GetKeywords"/>：后缀 → 主/次关键字表，用来喂 SCI_SETKEYWORDS。
/// 匹配一律先 <c>ToLowerInvariant()</c>：Windows 文件系统大小写不敏感，
/// 而 switch 模式是大小写敏感的精确匹配，不做归一化就会漏掉 Foo.CS 这类文件。
/// </remarks>
public static class LexerService
{
    /// <summary>
    /// 根据文件路径获取对应的 Lexilla lexer 名称（大小写不敏感匹配在编辑器端处理）
    /// </summary>
    /// <param name="filePath">文件完整路径或文件名；只用它的扩展名与文件名部分，不访问磁盘</param>
    /// <returns>
    /// Lexilla 词法器名（如 "cpp"、"hypertext"、"props"），直接写进
    /// <c>Scintilla.LexerName</c>（SCI_SETILEXER）；识别不出来的返回 "null"，
    /// 也就是「纯文本词法器」——这是刻意选的安全默认值：给一个不存在的名字会被
    /// Scintilla 静默忽略，效果与纯文本相同但读回校验会失败，反而不如老实返回 "null"。
    /// </returns>
    /// <remarks>
    /// 表里有不少「借用」的映射（JS/TS/Go/Swift/Kotlin → cpp，Dockerfile → bash，TOML → props，
    /// PHP → hypertext），原因是随包的 Lexilla 构建里没有对应的独立词法器；
    /// 借来之后必须再注入该语言的关键字表（见 <see cref="GetKeywords"/>），否则关键字不会变色。
    /// 每条映射的具体后缀含义见下方 switch 各臂的行内注释。
    /// </remarks>
    public static string GetLexerName(string filePath)
    {
        // 扩展名（含点，如 ".cs"）小写化后做精确匹配；无扩展名时 Path.GetExtension 返回空串
        var ext = Path.GetExtension(filePath).ToLowerInvariant();
        // 文件名（含扩展名）留给下面「无扩展名的特殊文件名」判定用
        var fileName = Path.GetFileName(filePath);

        // 特殊文件名（无扩展名）
        // 这几个是业界惯例的「名字即语言」文件，只能按整名匹配；都不满足时按纯文本处理
        if (string.IsNullOrEmpty(ext))
        {
            // Dockerfile 语法自成一套，但 Lexilla 这份构建没有 dockerfile 词法器，
            // 借 bash 词法器至少能让指令名落在「词/标识符」槽上，观感最接近
            if (fileName.Equals("Dockerfile", StringComparison.OrdinalIgnoreCase))
                return "bash";
            // Makefile 有三种常见叫法（含 .mak 这种带点的整名，走不到下面按扩展名的分支）
            if (fileName.Equals("Makefile", StringComparison.OrdinalIgnoreCase) ||
                fileName.Equals("GNUmakefile", StringComparison.OrdinalIgnoreCase) ||
                fileName.Equals("makefile.mak", StringComparison.OrdinalIgnoreCase))
                return "makefile";
            if (fileName.Equals("cmakelists.txt", StringComparison.OrdinalIgnoreCase))
                return "cmake";
            return "null";
        }

        // 下面每个臂 = 参考语言表里的一条记录：左侧是后缀集合（ext 列），右侧是词法器名。
        // 顺序无关（switch 精确匹配），但按「语言族」分块排列，方便与参考实现逐条核对。
        return ext switch
        {
            // C/C++/C#/Java 都使用 cpp lexer
            // .ino 是 Arduino 草图（本质就是 C++）；.hxx/.hh 是 C++ 头文件的另一种写法
            ".c" or ".h" or ".cpp" or ".cc" or ".cxx" or ".hpp" or ".hxx" or ".hh" or ".ino" => "cpp",
            // C# 借 cpp 词法器：注释符号（// 与 /* */）和预处理风格完全兼容，
            // 关键字靠注入 CSharpKeywords 补齐
            ".cs" => "cpp",
            // Java 同理借 cpp：块/行注释一致，关键字注入 JavaKeywords
            ".java" => "cpp",
            // Python
            // .pyw = Windows 下无控制台窗口的脚本；.pyi = 类型存根文件（语法同 Python）
            ".py" or ".pyw" or ".pyi" => "python",
            // Web
            // .shtml = 服务端包含的 HTML；.xhtml = XML 语法的 HTML，两者都走 hypertext
            ".html" or ".htm" or ".shtml" or ".xhtml" => "hypertext",
            // 严格 XML 家族：.svg 是图形、.xaml 是 WPF 界面、.resx 是资源、
            // .csproj/.slnx 是工程文件——它们都是 XML，所以共用 xml 词法器
            ".xml" or ".svg" or ".xaml" or ".resx" or ".csproj" or ".slnx" => "xml",
            // .scss/.less 是 CSS 超集，词法层面差异（嵌套、变量）不影响上色
            ".css" or ".scss" or ".less" => "css",
            // Lexilla 这份构建里没有独立的 javascript / go / toml / dockerfile 词法器，
            // 名字给错会被静默忽略（整篇没颜色），所以借用近似词法器 + 注入各自关键字表
            // .mjs = ES Module、.cjs = CommonJS、.jsx = 带标签语法的 JS，统一按 cpp 上色
            ".js" or ".mjs" or ".cjs" or ".jsx" => "cpp",
            // JSON 有专用词法器：属性名/字面量/注释分档，比借 hypertext 干净得多
            ".json" => "json",
            // .ts = TypeScript、.tsx = 带 JSX 的 TypeScript（注意与 Video Transport Stream 撞扩展名，
            // 这里按源码处理，符合参考实现的取舍）
            ".ts" or ".tsx" => "cpp",
            // Shell
            // 注意 .bashrc 这一档实际很难命中：文件名就叫 ".bashrc" 时 Path.GetExtension 返回空串
            // （开头的点不算扩展名），会走不到这里；只有 foo.bashrc 这种写法才命中。
            // 真正打开 ~/.bashrc 时它是「无扩展名」分支，最终落到 "null" 纯文本。
            ".sh" or ".bash" or ".zsh" or ".bashrc" => "bash",
            // Ruby
            // .erb = 带模板标签的 Ruby，借 ruby 词法器
            ".rb" or ".erb" => "ruby",
            // PHP
            // PHP 文件主体是 HTML + 内嵌脚本，用 hypertext 词法器；
            // 注释符号在 GetCommentSymbols 里被专门改成 C 风格（对齐 ND2 的处理）
            ".php" => "hypertext",
            // Perl
            // .pm = Perl module
            ".pl" or ".pm" => "perl",
            // Lua
            ".lua" => "lua",
            // Go
            ".go" => "cpp",
            // Rust
            // 表里给的是 rust 专用词法器名；若随包构建不含它，控件读回校验会失败并退回 "null"，
            // 由 EditorControl 兜底（不会像早期版本那样留一个装了但没着色的中间态）
            ".rs" => "rust",
            // Swift
            ".swift" => "cpp",
            // Kotlin
            // .kts = Kotlin Script
            ".kt" or ".kts" => "cpp",
            // SQL
            ".sql" => "sql",
            // Markdown
            ".md" or ".markdown" => "markdown",
            // TOML（键值/段落/井号注释，与 props 词法器同构）
            ".toml" => "props",
            // YAML
            ".yaml" or ".yml" => "yaml",
            // Properties/INI
            // .conf/.env/.gitignore/.editorconfig 都是「键=值 + 节 + #注释」结构，共用 props 词法器
            ".properties" or ".ini" or ".cfg" or ".conf" or ".env" or ".gitignore" or ".editorconfig" => "props",
            // CMake
            ".cmake" => "cmake",
            // Diff/Patch
            ".diff" or ".patch" => "diff",
            // Verilog/VHDL
            // .vh 是 Verilog header；两者都有专用词法器（注释符号见 GetCommentSymbols）
            ".v" or ".vh" => "verilog",
            ".vhd" or ".vhdl" => "vhdl",
            // Assembly
            // .s = 通用汇编源文件、.nasm = NASM 方言，与 .asm 同表
            ".asm" or ".s" or ".nasm" => "asm",
            // Fortran
            // .f/.for = 固定格式，.f90/.f95 = 自由格式
            ".f" or ".f90" or ".f95" or ".for" => "fortran",
            // Makefile
            ".mak" or ".mk" => "makefile",
            // Batch
            ".bat" or ".cmd" => "batch",
            // PowerShell
            // .psm1 = 模块文件；词法器存在与否取决于 Lexilla 构建，装不上时退回纯文本
            ".ps1" or ".psm1" => "powershell",
            // Pascal
            ".pas" => "pascal",
            // Matlab
            // 注意 .m 与 OCaml 撞扩展名，这里按参考实现取 Matlab
            ".m" => "matlab",
            // R
            // .rmd = R Markdown（按 R 上色，Markdown 区块不做二次高亮）
            ".r" or ".rmd" => "r",
            // VB
            ".vb" => "vb",
            // Ada
            // .adb = 包体（body）、.ads = 规约（spec）
            ".ada" or ".adb" or ".ads" => "ada",
            // Lisp
            // .cl = Common Lisp、.el = Emacs Lisp
            ".lisp" or ".cl" or ".el" => "lisp",
            // Text
            ".txt" => "null",
            // 未知后缀：纯文本。刻意不抛异常也不返回空串——空串在 Scintilla 里是非法词法器名
            _ => "null",
        };
    }

    /// <summary>
    /// 获取语言显示名称
    /// </summary>
    /// <param name="filePath">文件路径或文件名（只用扩展名/文件名判定，不读磁盘）</param>
    /// <returns>
    /// 给用户看的语言标题（"C++"、"C#"、"Markdown"…），用于状态栏「语法」栏与「语言」菜单项文本；
    /// 认不出来时返回 "Text"（而不是抛异常或空串），保证状态栏永远有内容可显示。
    /// </returns>
    /// <remarks>
    /// 与 <see cref="GetLexerName"/> 是两张独立的表：这里细分到「用户认知的语言」，
    /// 那里受限于 Lexilla 实际提供的词法器。所以 C/C++/C#/Java 显示名各不同、
    /// 词法器却都是 "cpp"，两者不能互相反推。
    /// 覆盖的后缀也比词法器表少（例如没有 .nasm/.f95/.erb），未列出的后缀统一落到 "Text"；
    /// 用户手动切换语法时，主窗口会直接改写 EditorControl.LanguageDisplay，不经过本方法。
    /// </remarks>
    public static string GetLanguageName(string filePath)
    {
        var ext = Path.GetExtension(filePath).ToLowerInvariant();
        var fileName = Path.GetFileName(filePath);

        // 无扩展名的「名字即语言」文件：与词法器表同样处理，但只认最常见的两种叫法
        if (string.IsNullOrEmpty(ext))
        {
            if (fileName.Equals("Dockerfile", StringComparison.OrdinalIgnoreCase))
                return "Docker";
            if (fileName.Equals("Makefile", StringComparison.OrdinalIgnoreCase))
                return "Makefile";
            return "Text";
        }

        // 每个臂 = 一种「显示名」，左侧是它接受的后缀集合（对应 ND2 语言菜单里的条目文本）
        return ext switch
        {
            // C 家族：词法器同为 cpp，显示名要分清
            ".c" or ".h" => "C",
            ".cpp" or ".cc" or ".cxx" or ".hpp" or ".hxx" or ".hh" => "C++",
            ".cs" => "C#",
            ".java" => "Java",
            ".py" or ".pyw" => "Python",
            ".html" or ".htm" => "HTML",
            ".xml" or ".svg" => "XML",
            ".css" => "CSS",
            // Web 脚本：JS/TS 的变体后缀各给各的显示名（.mjs/.cjs 未在表内，按 Text 兜底）
            ".js" => "JavaScript",
            ".json" => "JSON",
            ".ts" or ".tsx" => "TypeScript",
            ".jsx" => "JSX",
            ".sh" or ".bash" => "Shell",
            ".rb" => "Ruby",
            ".php" => "PHP",
            ".pl" or ".pm" => "Perl",
            ".lua" => "Lua",
            ".go" => "Go",
            ".rs" => "Rust",
            ".swift" => "Swift",
            ".kt" => "Kotlin",
            ".sql" => "SQL",
            ".md" => "Markdown",
            ".toml" => "TOML",
            ".yaml" or ".yml" => "YAML",
            // 配置类：.conf/.env/.gitignore 等归不进显示名，落到 Text
            ".properties" or ".ini" or ".cfg" => "Properties",
            ".cmake" => "CMake",
            ".diff" or ".patch" => "Diff",
            ".asm" => "Assembly",
            ".v" => "Verilog",
            ".vhd" => "VHDL",
            ".bat" or ".cmd" => "Batch",
            ".ps1" => "PowerShell",
            ".pas" => "Pascal",
            ".m" => "Matlab",
            ".r" => "R",
            ".vb" => "VB",
            ".f" or ".f90" => "Fortran",
            ".mak" => "Makefile",
            ".txt" => "Text",
            _ => "Text",
        };
    }

    /// <summary>
    /// 一种语言的三个注释符号，对应 ND2 挂在词法器实例上的 m_commentSymbol / m_commentStart / m_commentEnd
    /// （ND2 <c>qscilexer*.cpp</c> 构造函数里的赋值）。空串 = 该语言没有这一种注释：
    /// ND2 的做法是直接不动作（<c>scintillaeditview.cpp:2048/2363/2558</c>），不会退化成 "//"。
    /// </summary>
    /// <param name="Line">行注释符（如 "//"、"#"、"--"、"REM"）；作用于「逐行加/去注释」，
    /// 会被插到该行第一个非空白字符之前。空串表示该语言没有行注释，命令对它是空操作。</param>
    /// <param name="Start">成对区块注释的开始符（如 "/*"、"&lt;!--"、"{")；用于「块注释」命令包裹选区。
    /// 空串表示没有区块注释。</param>
    /// <param name="End">成对区块注释的结束符（如 "*/"、"--&gt;"、"}"）；与 <paramref name="Start"/> 成对出现，
    /// 只有两者都非空时块注释才有意义。</param>
    /// <remarks>
    /// 用 record struct 而不是 class：值语义、可按字段比较、没有 null 烦恼，
    /// 默认值 <c>default</c> 的三个字段都是空串，正好等于「该语言没有注释」。
    /// 消费方是 <c>EditorControl.CommentOps</c>（注释三件套：块注释/行注释/取消注释）。
    /// </remarks>
    public readonly record struct CommentSymbols(string Line, string Start, string End)
    {
        /// <summary>
        /// 「此语言没有注释符号」的具名常量：三个字段都是空串。
        /// 调用方（EditorControl）在还没装词法器时用它当出厂默认，等价于注释命令暂时不可用。
        /// </summary>
        public static readonly CommentSymbols None = new(string.Empty, string.Empty, string.Empty);
    }

    /// <summary>
    /// ND2 的注释符号按「这个编辑器的语言」给，不是按扩展名的字符串猜。这里同一口径：
    /// 词法器名决定默认值，<paramref name="pathOrHint"/> 只用来还原 ND2 唯一逐实例改写过的 PHP
    /// （ND2 <c>scintillaeditview.cpp:672-676</c> 把 html 词法器的符号换成了 c 风格）。
    /// </summary>
    /// <param name="lexerName">当前生效的 Lexilla 词法器名（<see cref="GetLexerName"/> 的返回值，
    /// 或 EditorControl 读回校正后的 <c>CurrentLexerName</c>）；大小写敏感，务必用小写规范名</param>
    /// <param name="pathOrHint">文件路径或任何带扩展名的提示串；仅用于识别 .php，可为 null</param>
    /// <returns>该语言的注释三件套；认不出的词法器返回 <see cref="CommentSymbols.None"/>，
    /// 此时注释命令按 ND2 语义什么都不做（不会误插 "//"）。</returns>
    /// <remarks>
    /// 字段填写口径逐条对齐 ND2 的 <c>qscilexer*.cpp</c>，所以表里有三类「看着奇怪但必须照抄」的值：
    /// 只有区块注释没有行注释的语言（html/xml/css/pascal）把 <c>Line</c> 留空；
    /// 只有行注释的语言把 <c>Start</c>/<c>End</c> 留空；
    /// 字符串匹配是精确匹配，新增语言时要用 <see cref="GetLexerName"/> 里出现过的那个小写名，
    /// 写成 "html" 之类匹配不到的名字会静默落进默认分支（None），表现就是「注释菜单点了没反应」。
    /// </remarks>
    public static CommentSymbols GetCommentSymbols(string lexerName, string? pathOrHint)
    {
        // PHP 特例：它的词法器是 hypertext（默认给 <!-- -->），但 ND2 在建好词法器后逐实例改写成 C 风格，
        // 所以这里必须在查表之前先按扩展名截走，否则 // 注释就变成 <!-- --> 了
        if (Path.GetExtension(pathOrHint ?? string.Empty).Equals(".php", StringComparison.OrdinalIgnoreCase))
            return new CommentSymbols("//", "/*", "*/");

        return lexerName switch
        {
            // C 语法家族：// 行注释 + /* */ 区块注释（rust/verilog 也是这套）
            "cpp" or "rust" or "verilog" => new CommentSymbols("//", "/*", "*/"),
            // SQL 与 VHDL：-- 行注释，区块注释仍是 /* */
            "sql" or "vhdl" => new CommentSymbols("--", "/*", "*/"),
            // 汇编：; 行注释（各家方言都认它），区块注释沿用 C 风格
            "asm" => new CommentSymbols(";", "/*", "*/"),
            // 标记语言：没有行注释概念，只能整段用 <!-- --> 包住
            "hypertext" or "xml" => new CommentSymbols(string.Empty, "<!--", "-->"),
            // CSS：/* */ 区块注释；本工具不支持 SCSS 的 // 行注释（沿用参考实现的符号表）
            "css" => new CommentSymbols(string.Empty, "/*", "*/"),
            // Pascal：花括号一对 { } 作区块注释（Delphi 也认 (* *) 和 //，这里只按表给花括号）
            "pascal" => new CommentSymbols(string.Empty, "{", "}"),
            // R：# 行注释；区块注释按参考实现取成「引号对」，实际用途很窄
            "r" => new CommentSymbols("#", "\"", "\""),
            // 井号注释家族：脚本/配置类语言大多只有行注释，没有区块注释
            // （"null" 是纯文本词法器，这里也给它 # ——表现就是在 .txt 里按行插 '#'，
            //   与「配置文件多半也是 #」这一常见习惯一致）
            "python" or "bash" or "ruby" or "yaml" or "perl" or "props" or "makefile" or "cmake" or "null" =>
                new CommentSymbols("#", string.Empty, string.Empty),
            // 单一行注释家族：各自的注释符是语言里最有辨识度的那个符号
            "lua" => new CommentSymbols("--", string.Empty, string.Empty),
            "matlab" => new CommentSymbols("%", string.Empty, string.Empty),
            // 批处理用 REM 关键字当注释符（大小写按参考实现原样保留）
            "batch" => new CommentSymbols("REM", string.Empty, string.Empty),
            "fortran" => new CommentSymbols("!", string.Empty, string.Empty),
            // VB 用单引号，容易和字符串混淆但语言本身如此
            "vb" => new CommentSymbols("'", string.Empty, string.Empty),
            // 下面两条是 ND2 createLexer 里根本没建词法器（L_POWERSHELL/L_LISP 返回 nullptr，
            // 注释三件套在 ND2 里是空操作）但它自己写好的符号；这里按符号表给，见功能对照的偏差记录。
            "powershell" or "lisp" => new CommentSymbols(";", string.Empty, string.Empty),
            // ND2 有这些语言类但构造里没写符号（QsciLexerJSON/Markdown/Diff/Ada 等）→ 注释无动作
            _ => CommentSymbols.None,
        };
    }

    // ==================== 关键字表 ====================
    // cpp / python 等 lexer 本身不带这些语言的关键字，需要显式注入才能高亮
    // （参考实现靠 langs.xml 里的 keyword 段 + QsciLexer::setKeywordSets；我们直接内联成字符串常量）

    /// <summary>
    /// 一个语言的两张关键字表，对应 SCI_SETKEYWORDS 的 0 号与 1 号槽位。
    /// </summary>
    /// <param name="Primary">主关键字表（空格分隔）：命中后走「关键字」样式，即主题表里的 5 号槽，加粗高亮</param>
    /// <param name="Secondary">次关键字表（空格分隔）：命中后走「二级关键字/类型名」样式（16 号槽，青色），
    /// 本工具用它承载类型名与内置对象，让 Console/String 这类名字与普通标识符区分开</param>
    /// <remarks>
    /// 词法器比对关键字是大小写敏感的，所以表内单词必须写语言里的原生拼写（如 Int32、memcpy），
    /// 不要为了「多匹配几种写法」而重复登记大小写变体——那只会让同一份表越来越长。
    /// 表内允许空格分隔的重复项（Scintilla 会去重），但重复本身没有语义，见各常量注释里的说明。
    /// </remarks>
    public sealed record KeywordSets(string Primary, string Secondary);

    /// <summary>
    /// 取该文件应注入的关键字表（借 cpp 词法器的语言全靠它，否则关键字不上色）。
    /// </summary>
    /// <param name="filePath">文件路径或文件名；只按扩展名查表，不读磁盘</param>
    /// <returns>
    /// 命中时返回主/次两张表；未收录的语言返回 null，表示「不注入、用词法器自带的关键字表」。
    /// 返回 null 是有意为之的兜底：SQL/YAML/Bash 这些词法器自带关键字表，再注入一遍会覆盖掉它们的原生配色。
    /// </returns>
    /// <remarks>
    /// 这里按扩展名而不是按词法器名查：同一个 "cpp" 词法器要同时服务 C/C++/C#/Java/JS/TS/Go/Swift/Kotlin，
    /// 只有后缀能分辨「该注入哪一份关键字」。调用侧（EditorControl.SetLexerByNameInternal）
    /// 打开文件时把文件路径当 keywordPath 传进来，所以正常打开都会命中本表；
    /// 传 null 的场景是用户在「语言」菜单手动切语法，此时沿用词法器自带的关键字表。
    /// </remarks>
    public static KeywordSets? GetKeywords(string filePath)
    {
        var ext = Path.GetExtension(filePath).ToLowerInvariant();

        return ext switch
        {
            // C#：主表是语言关键字，次表是 BCL/WPF 常用类型名
            ".cs" => new KeywordSets(CSharpKeywords, CSharpTypes),
            // C/C++ 家族（含 Arduino .ino）
            ".c" or ".h" or ".cpp" or ".cc" or ".cxx" or ".hpp" or ".hxx" or ".hh" or ".ino"
                => new KeywordSets(CppKeywords, CppTypes),
            ".java" => new KeywordSets(JavaKeywords, JavaTypes),
            // Python：次表放内置函数与内置异常
            ".py" or ".pyw" or ".pyi" => new KeywordSets(PythonKeywords, PythonBuiltins),
            // JS/TS 系：TS 有独立关键字表（在 JS 之上加类型语法），内置表与 JS 共用
            ".js" or ".mjs" or ".cjs" or ".jsx" => new KeywordSets(JsKeywords, JsBuiltins),
            ".ts" or ".tsx" => new KeywordSets(TsKeywords, JsBuiltins),
            ".go" => new KeywordSets(GoKeywords, GoBuiltins),
            // Kotlin / Swift 只提供主表，次表留空串（不是 null：SCI_SETKEYWORDS 收空串表示清空该表）
            ".kt" or ".kts" => new KeywordSets(KotlinKeywords, ""),
            ".swift" => new KeywordSets(SwiftKeywords, ""),
            // 其它后缀：词法器自带表，或（如 rust/lua/r）本来就认全自己的关键字
            _ => null,
        };
    }

    /// <summary>
    /// C# 主关键字表（注入 SCI_SETKEYWORDS 的 0 号表，命中即「关键字蓝 + 加粗」）。
    /// 内容口径：前一段是 C# 规范里的保留字（abstract..while，其中 bool/byte/decimal 这些
    /// 「类型字面量」也属于关键字，必须留在主表而不是次表）；后一段是「上下文关键字」
    /// （add/alias/async/by/dynamic/get/init/record/required/scoped 等，只在特定语法位置才是关键字）。
    /// 上下文关键字混在主表里会让 <c>add</c> 这类普通变量名也变色——这是参考实现同款取舍，
    /// 因为词法器无法区分语法位置，两害相权取其轻。
    /// </summary>
    private const string CSharpKeywords =
        "abstract as base bool break byte case catch char checked class const continue decimal default " +
        "delegate do double else enum event explicit extern false finally fixed float for foreach goto if " +
        "implicit in int interface internal is lock long namespace new null object operator out override " +
        "params private protected public readonly ref return sbyte sealed short sizeof stackalloc static " +
        "string struct switch this throw true try typeof uint ulong unchecked unsafe ushort using virtual " +
        "void volatile while " +
        "add alias and ascending async await by descending dynamic equals from get global group init into " +
        "join let managed nameof not notnull on or orderby partial remove select set unmanaged value var " +
        "when where with yield record required file scoped";

    /// <summary>
    /// C# 次关键字表（1 号表，命中即「类型名青」）：BCL 常用类型 + WPF 控件/画刷 + 序列化/IO/反射等。
    /// </summary>
    /// <remarks>
    /// 这张表是「实用导向」的白名单，不是完整 API 列表：只收日常手写代码里会直接出现的名字，
    /// 表越长命中越随机、也越容易把变量名涂色，所以刻意保持人工筛选。
    /// 尾部残留少量重复项（如 StringBuilder/IEnumerable）与若干未采用的名字，
    /// 对 Scintilla 来说重复只是浪费一次查表，不影响上色结果。
    /// </remarks>
    private const string CSharpTypes =
        "Console String Object Int32 Int64 Int16 UInt32 UInt64 Double Single Boolean Char Byte SByte " +
        "Decimal Exception ArgumentException ArgumentNullException InvalidOperationException " +
        "NotImplementedException NotSupportedException List Dictionary HashSet Queue Stack " +
        "IEnumerable ICollection IList IDictionary IReadOnlyList Func Action Predicate Task ValueTask " +
        "Nullable Tuple ValueTuple Type Math Convert DateTime TimeSpan DateTimeOffset Guid Uri Regex " +
        "Encoding StringBuilder Stream File FileInfo Directory DirectoryInfo Path Environment Process " +
        "Thread Task Dispatcher DependencyObject DependencyProperty Application Window Page UserControl " +
        "Control TextBox Button Label StackPanel DockPanel Grid Border Image TextBlock ListBox ComboBox " +
        "TreeView TabControl Menu MenuItem Brush SolidColorBrush Color ColorConverter FontFamily " +
        "MessageBox OpenFileDialog SaveFileDialog Registry RegistryKey CultureInfo EventArgs " +
        "CancelEventArgs RoutedEventArgs PropertyChangedEventArgs JsonSerializer JsonSerializerOptions " +
        "HttpClient StreamReader StreamWriter MemoryStream StringBuilder IEnumerable Span";

    /// <summary>
    /// C/C++ 主关键字表（0 号表）：覆盖到 C++20（concept/requires/co_await 这一批），
    /// 另外把运算符替代记号（and/or/not/bitand/xor_eq…）也一并登记，让写成 <c>and</c> 的代码
    /// 和写成 <c>&amp;&amp;</c> 的代码有一样的观感。
    /// </summary>
    /// <remarks>末尾 override/final/delete/default 是重复登记（前文已有 default），
    /// 属于历史冗余，Scintilla 查表时自动去重，保留是为了不改动已验证过的表数据。</remarks>
    private const string CppKeywords =
        "alignas alignof and and_eq asm auto bitand bitor bool break case catch char char8_t char16_t " +
        "char32_t class compl concept const consteval constexpr constinit const_cast continue co_await " +
        "co_return co_yield decltype default delete do double dynamic_cast else enum explicit export extern " +
        "false float for friend goto if inline int long mutable namespace new noexcept not not_eq nullptr " +
        "operator or or_eq private protected public register reinterpret_cast requires return short signed " +
        "sizeof static static_assert static_cast struct switch template this thread_local throw true try " +
        "typedef typeid typename union unsigned using virtual void volatile wchar_t while xor xor_eq " +
        "override final delete default";

    /// <summary>
    /// C/C++ 次关键字表（1 号表，类型青）：std 命名空间下的容器/智能指针/流对象与固定宽度整型。
    /// </summary>
    /// <remarks>词法器不会自己识别 "std::vector" 的 vector 部分，只有整词命中表里的名字才上色；
    /// 因此这张表收录的是「单独出现也有意义」的名字（string/vector/size_t 等）。</remarks>
    private const string CppTypes =
        "std string wstring vector map set unordered_map unordered_set list pair size_t ptrdiff_t int8_t " +
        "int16_t int32_t int64_t uint8_t uint16_t uint32_t uint64_t ostream istream iostream " +
        "stringstream fstream ofstream ifstream shared_ptr unique_ptr weak_ptr make_shared make_unique " +
        "cout cin endl nullptr_t FILE";

    /// <summary>
    /// Java 主关键字表（0 号表）：JLS 保留字再加上 10/14/17 引入的
    /// var/record/sealed/permits/yield/non-sealed（true/false/null 是字面量，按参考实现留在关键字表里）。
    /// </summary>
    private const string JavaKeywords =
        "abstract assert boolean break byte case catch char class const continue default do double else " +
        "enum extends final finally float for goto if implements import instanceof int interface long " +
        "native new package private protected public return short static strictfp super switch synchronized " +
        "this throw throws transient try void volatile while true false null var record sealed permits " +
        "yield non-sealed";

    /// <summary>
    /// Java 次关键字表（1 号表，类型青）：java.lang / java.util 里最常用的类型与注解名。
    /// </summary>
    /// <remarks>Integer/Long/Double 等在末尾被重复登记了一次（表数据的既有冗余，不影响效果）；
    /// Override/Deprecated/SuppressWarnings 是注解名，Java 词法器不区分注解与普通标识符，
    /// 放进这张表是「让它显眼一点」的实用做法。</remarks>
    private const string JavaTypes =
        "String Integer Long Double Float Boolean Character Byte Short Object List ArrayList LinkedList " +
        "Map HashMap LinkedHashMap Set HashSet TreeSet Exception RuntimeException System Math Thread " +
        "Runnable StringBuilder Class Override Deprecated SuppressWarnings Optional Stream Collectors " +
        "Arrays Collections Objects Integer Long Double";

    /// <summary>
    /// Python 主关键字表（0 号表）：语言保留字 + True/False/None 字面量 + match/case 软关键字。
    /// </summary>
    /// <remarks>self/cls 严格说不是关键字而是约定名，这里按参考实现一起登记，
    /// 好处是方法签名里一眼能看出「这是实例/类方法的隐式参数」。</remarks>
    private const string PythonKeywords =
        "and as assert async await break class continue def del elif else except finally for from global " +
        "if import in is lambda None nonlocal not or pass raise return True try while with yield False " +
        "match case self cls";

    /// <summary>
    /// Python 次关键字表（1 号表，类型青）：内建函数（abs/len/print…）与内建异常/容器类型。
    /// </summary>
    /// <remarks>print/len 在表尾重复出现（既有冗余）；不收录标准库模块名，
    /// 因为模块名千变万化，写死白名单只会让人误以为「没上色=不是模块」。</remarks>
    private const string PythonBuiltins =
        "abs all any bin bool bytearray bytes callable chr dict dir divmod enumerate eval exec filter " +
        "float format frozenset getattr globals hasattr hash help hex id input int isinstance issubclass " +
        "iter len list locals map max memoryview min next object oct open ord pow print property range " +
        "repr reversed round set setattr slice sorted staticmethod str sum super tuple type vars zip " +
        "Exception ValueError TypeError KeyError IndexError RuntimeError print len";

    /// <summary>
    /// JavaScript 主关键字表（0 号表）：ES 保留字 + 常见上下文关键字
    /// （of/as/from/get/set 只在 for-of、import、getter 等位置才是关键字，这里一律按关键字涂色）。
    /// </summary>
    /// <remarks>由于 JS 走的是 cpp 词法器，本表实际填进 SCE_C_WORD(5) 槽；
    /// 关键字表在 Scintilla 里是「每个控件实例一份」的状态，所以换文件/换语言时必须重新注入，
    /// 否则会留着上一个语言的表，出现「打开 JS 却按 C# 关键字上色」这类错位。</remarks>
    private const string JsKeywords =
        "break case catch class const continue debugger default delete do else export extends false " +
        "finally for function if import in instanceof new null return super switch this throw true try " +
        "typeof var void while with yield let static async await of as from get set";

    /// <summary>
    /// JavaScript 次关键字表（1 号表）：全局内建对象/函数与浏览器、Node 两个环境的常用入口。
    /// </summary>
    /// <remarks>console/document/window 属浏览器，require/module/exports 属 Node，
    /// 混在一张表里是为了省掉「判断当前脚本跑在哪」的复杂度——反正只是上色。</remarks>
    private const string JsBuiltins =
        "Array Boolean Date Error Function JSON Math Number Object Promise Proxy Reflect RegExp String " +
        "Symbol Map Set WeakMap WeakSet parseInt parseFloat isNaN isFinite undefined NaN Infinity " +
        "console document window globalThis require module exports";

    /// <summary>
    /// TypeScript 主关键字表（0 号表）：前段与 <see cref="JsKeywords"/> 完全一致，
    /// 后段追加类型系统专用记号（type/interface/enum/namespace/declare/implements/readonly、
    /// any/unknown/never/keyof/infer/satisfies 等）。
    /// </summary>
    /// <remarks>TS 的内置表复用 JS 那份（见 <see cref="GetKeywords"/>）：两者的全局对象基本同名，
    /// 单开一份只会让两张表越改越不一致。</remarks>
    private const string TsKeywords =
        "break case catch class const continue debugger default delete do else export extends false " +
        "finally for function if import in instanceof new null return super switch this throw true try " +
        "typeof var void while with yield let static async await of as from get set " +
        "type interface enum namespace declare abstract implements private protected public readonly " +
        "any unknown never string number boolean symbol object bigint keyof infer is asserts satisfies";

    /// <summary>
    /// Go 主关键字表（0 号表）：语言保留字（break..type/var）与预声明标识符
    /// （nil/true/iota/append/len/make/panic…）、内置类型名（int/uint/float64/string/error…）混编。
    /// </summary>
    /// <remarks>
    /// Go 走 cpp 词法器，而 cpp 词法器只有 0/1 两张表可用，所以这里把「关键字 + 内置函数 + 类型名」
    /// 全塞进主表（这也是本文件里唯一一张三段混编的表）。
    /// 另外注意表数据本身：break..type var 这一段连续出现了两次（重复部分从第二行中段开始，
    /// 即紧接在第一次的 "type var" 之后又抄了一遍），属于已知的冗余，Scintilla 查表会去重，
    /// 所以只影响可读性、不影响上色效果。
    /// </remarks>
    private const string GoKeywords =
        "break case chan const continue default defer else fallthrough for func go goto if import interface " +
        "map package range return select struct switch type var break case chan const continue default defer " +
        "else fallthrough for func go goto if import interface map package range return select struct switch " +
        "type var nil true iota append bool byte cap close complex copy delete len make new panic print println " +
        "recover any int int8 int16 int32 int64 uint uint8 uint16 uint32 uint64 uintptr float32 float64 string " +
        "bool error comparable";

    /// <summary>
    /// Go 次关键字表（1 号表，类型青）：常用标准库包名与结构/接口里会出现的类型词。
    /// </summary>
    /// <remarks>包名（fmt/strings/errors/os…）在 Go 源码里出现频率极高，给它们一档单独颜色
    /// 比让它们在标识符海里灰着更好读；Error/String/Reader/Writer 这类首字母名同时是接口约定名。</remarks>
    private const string GoBuiltins =
        "fmt strings errors os io net http context sync atomic time strconv bytes bufio math sort json xml " +
        "Error String Reader Writer Context WaitGroup Mutex RWMutex Channel Slice Map Array Struct Interface " +
        "Package Import Main Init Type Kind Value";

    /// <summary>
    /// Kotlin 主关键字表（0 号表）：硬关键字（as/break/class/fun/val/var/when…）与软关键字
    /// （data/sealed/inline/lateinit/operator/companion…）合并在一格里——Kotlin 的次表被留空，
    /// 所以没有「类型名」这一档，全部按关键字涂色。
    /// </summary>
    /// <remarks>Kotlin 走 cpp 词法器；表里还混着 constructor/delegate/get/set/param/property/receiver
    /// 这类只在特定语法位置出现的设计词，一起登记后访问器与构造函数声明能立刻跳出来。</remarks>
    private const string KotlinKeywords =
        "as break class continue do else false for fun if in interface is null object package return " +
        "super this throw true try typealias typeof val var when while by catch constructor delegate " +
        "dynamic field file finally get import init param property receiver set setparam where actual " +
        "abstract annotation companion const crossinline data enum expect external final infix inline " +
        "inner internal lateinit noinline open operator out override private protected public reified " +
        "sealed suspend tailrec vararg";

    /// <summary>
    /// Swift 主关键字表（0 号表）：声明关键字（let/var/func/struct/protocol…）、
    /// 控制流（guard/defer/repeat/fallthrough…）与现代特性（async/await/actor/some）都在这一格里。
    /// </summary>
    /// <remarks>Swift 的次表留空（见 <see cref="GetKeywords"/>），所以类型名不做特殊上色；
    /// Self/self/Any 这三个「隐式接收者」反而在表里，因为它们出现频率最高、最需要辨识度。
    /// 同一段代码走 cpp 词法器时，注释符号与 C 系一致（// 与 /* */），无需额外配置。</remarks>
    private const string SwiftKeywords =
        "associatedtype class deinit enum extension fileprivate func import init inout internal let open " +
        "operator private protocol public static struct subscript typealias var break case continue " +
        "default defer do else fallthrough for guard if in repeat return switch where while as Any catch " +
        "false is nil rethrows super self Self throw throws true try async await actor some any";
}
