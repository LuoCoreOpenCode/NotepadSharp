using System;
using System.IO;

namespace NotepadSharp.Services;

/// <summary>
/// 语法高亮服务：将文件扩展名映射到 Lexilla lexer 名称
/// </summary>
public static class LexerService
{
    /// <summary>
    /// 根据文件路径获取对应的 Lexilla lexer 名称（大小写不敏感匹配在编辑器端处理）
    /// </summary>
    public static string GetLexerName(string filePath)
    {
        var ext = Path.GetExtension(filePath).ToLowerInvariant();
        var fileName = Path.GetFileName(filePath);

        // 特殊文件名（无扩展名）
        if (string.IsNullOrEmpty(ext))
        {
            if (fileName.Equals("Dockerfile", StringComparison.OrdinalIgnoreCase))
                return "bash";
            if (fileName.Equals("Makefile", StringComparison.OrdinalIgnoreCase) ||
                fileName.Equals("GNUmakefile", StringComparison.OrdinalIgnoreCase) ||
                fileName.Equals("makefile.mak", StringComparison.OrdinalIgnoreCase))
                return "makefile";
            if (fileName.Equals("cmakelists.txt", StringComparison.OrdinalIgnoreCase))
                return "cmake";
            return "null";
        }

        return ext switch
        {
            // C/C++/C#/Java 都使用 cpp lexer
            ".c" or ".h" or ".cpp" or ".cc" or ".cxx" or ".hpp" or ".hxx" or ".hh" or ".ino" => "cpp",
            ".cs" => "cpp",
            ".java" => "cpp",
            // Python
            ".py" or ".pyw" or ".pyi" => "python",
            // Web
            ".html" or ".htm" or ".shtml" or ".xhtml" => "hypertext",
            ".xml" or ".svg" or ".xaml" or ".resx" or ".csproj" or ".slnx" => "xml",
            ".css" or ".scss" or ".less" => "css",
            // Lexilla 这份构建里没有独立的 javascript / go / toml / dockerfile 词法器，
            // 名字给错会被静默忽略（整篇没颜色），所以借用近似词法器 + 注入各自关键字表
            ".js" or ".mjs" or ".cjs" or ".jsx" => "cpp",
            ".json" => "json",
            ".ts" or ".tsx" => "cpp",
            // Shell
            ".sh" or ".bash" or ".zsh" or ".bashrc" => "bash",
            // Ruby
            ".rb" or ".erb" => "ruby",
            // PHP
            ".php" => "hypertext",
            // Perl
            ".pl" or ".pm" => "perl",
            // Lua
            ".lua" => "lua",
            // Go
            ".go" => "cpp",
            // Rust
            ".rs" => "rust",
            // Swift
            ".swift" => "cpp",
            // Kotlin
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
            ".properties" or ".ini" or ".cfg" or ".conf" or ".env" or ".gitignore" or ".editorconfig" => "props",
            // CMake
            ".cmake" => "cmake",
            // Diff/Patch
            ".diff" or ".patch" => "diff",
            // Verilog/VHDL
            ".v" or ".vh" => "verilog",
            ".vhd" or ".vhdl" => "vhdl",
            // Assembly
            ".asm" or ".s" or ".nasm" => "asm",
            // Fortran
            ".f" or ".f90" or ".f95" or ".for" => "fortran",
            // Makefile
            ".mak" or ".mk" => "makefile",
            // Batch
            ".bat" or ".cmd" => "batch",
            // PowerShell
            ".ps1" or ".psm1" => "powershell",
            // Pascal
            ".pas" => "pascal",
            // Matlab
            ".m" => "matlab",
            // R
            ".r" or ".rmd" => "r",
            // VB
            ".vb" => "vb",
            // Ada
            ".ada" or ".adb" or ".ads" => "ada",
            // Lisp
            ".lisp" or ".cl" or ".el" => "lisp",
            // Text
            ".txt" => "null",
            _ => "null",
        };
    }

    /// <summary>
    /// 获取语言显示名称
    /// </summary>
    public static string GetLanguageName(string filePath)
    {
        var ext = Path.GetExtension(filePath).ToLowerInvariant();
        var fileName = Path.GetFileName(filePath);

        if (string.IsNullOrEmpty(ext))
        {
            if (fileName.Equals("Dockerfile", StringComparison.OrdinalIgnoreCase))
                return "Docker";
            if (fileName.Equals("Makefile", StringComparison.OrdinalIgnoreCase))
                return "Makefile";
            return "Text";
        }

        return ext switch
        {
            ".c" or ".h" => "C",
            ".cpp" or ".cc" or ".cxx" or ".hpp" or ".hxx" or ".hh" => "C++",
            ".cs" => "C#",
            ".java" => "Java",
            ".py" or ".pyw" => "Python",
            ".html" or ".htm" => "HTML",
            ".xml" or ".svg" => "XML",
            ".css" => "CSS",
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
    public readonly record struct CommentSymbols(string Line, string Start, string End)
    {
        public static readonly CommentSymbols None = new(string.Empty, string.Empty, string.Empty);
    }

    /// <summary>
    /// ND2 的注释符号按「这个编辑器的语言」给，不是按扩展名的字符串猜。这里同一口径：
    /// 词法器名决定默认值，<paramref name="pathOrHint"/> 只用来还原 ND2 唯一逐实例改写过的 PHP
    /// （ND2 <c>scintillaeditview.cpp:672-676</c> 把 html 词法器的符号换成了 c 风格）。
    /// </summary>
    public static CommentSymbols GetCommentSymbols(string lexerName, string? pathOrHint)
    {
        if (Path.GetExtension(pathOrHint ?? string.Empty).Equals(".php", StringComparison.OrdinalIgnoreCase))
            return new CommentSymbols("//", "/*", "*/");

        return lexerName switch
        {
            "cpp" or "rust" or "verilog" => new CommentSymbols("//", "/*", "*/"),
            "sql" or "vhdl" => new CommentSymbols("--", "/*", "*/"),
            "asm" => new CommentSymbols(";", "/*", "*/"),
            "hypertext" or "xml" => new CommentSymbols(string.Empty, "<!--", "-->"),
            "css" => new CommentSymbols(string.Empty, "/*", "*/"),
            "pascal" => new CommentSymbols(string.Empty, "{", "}"),
            "r" => new CommentSymbols("#", "\"", "\""),
            "python" or "bash" or "ruby" or "yaml" or "perl" or "props" or "makefile" or "cmake" or "null" =>
                new CommentSymbols("#", string.Empty, string.Empty),
            "lua" => new CommentSymbols("--", string.Empty, string.Empty),
            "matlab" => new CommentSymbols("%", string.Empty, string.Empty),
            "batch" => new CommentSymbols("REM", string.Empty, string.Empty),
            "fortran" => new CommentSymbols("!", string.Empty, string.Empty),
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

    public sealed record KeywordSets(string Primary, string Secondary);

    public static KeywordSets? GetKeywords(string filePath)
    {
        var ext = Path.GetExtension(filePath).ToLowerInvariant();

        return ext switch
        {
            ".cs" => new KeywordSets(CSharpKeywords, CSharpTypes),
            ".c" or ".h" or ".cpp" or ".cc" or ".cxx" or ".hpp" or ".hxx" or ".hh" or ".ino"
                => new KeywordSets(CppKeywords, CppTypes),
            ".java" => new KeywordSets(JavaKeywords, JavaTypes),
            ".py" or ".pyw" or ".pyi" => new KeywordSets(PythonKeywords, PythonBuiltins),
            ".js" or ".mjs" or ".cjs" or ".jsx" => new KeywordSets(JsKeywords, JsBuiltins),
            ".ts" or ".tsx" => new KeywordSets(TsKeywords, JsBuiltins),
            ".go" => new KeywordSets(GoKeywords, GoBuiltins),
            ".kt" or ".kts" => new KeywordSets(KotlinKeywords, ""),
            ".swift" => new KeywordSets(SwiftKeywords, ""),
            _ => null,
        };
    }

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

    private const string CppKeywords =
        "alignas alignof and and_eq asm auto bitand bitor bool break case catch char char8_t char16_t " +
        "char32_t class compl concept const consteval constexpr constinit const_cast continue co_await " +
        "co_return co_yield decltype default delete do double dynamic_cast else enum explicit export extern " +
        "false float for friend goto if inline int long mutable namespace new noexcept not not_eq nullptr " +
        "operator or or_eq private protected public register reinterpret_cast requires return short signed " +
        "sizeof static static_assert static_cast struct switch template this thread_local throw true try " +
        "typedef typeid typename union unsigned using virtual void volatile wchar_t while xor xor_eq " +
        "override final delete default";

    private const string CppTypes =
        "std string wstring vector map set unordered_map unordered_set list pair size_t ptrdiff_t int8_t " +
        "int16_t int32_t int64_t uint8_t uint16_t uint32_t uint64_t ostream istream iostream " +
        "stringstream fstream ofstream ifstream shared_ptr unique_ptr weak_ptr make_shared make_unique " +
        "cout cin endl nullptr_t FILE";

    private const string JavaKeywords =
        "abstract assert boolean break byte case catch char class const continue default do double else " +
        "enum extends final finally float for goto if implements import instanceof int interface long " +
        "native new package private protected public return short static strictfp super switch synchronized " +
        "this throw throws transient try void volatile while true false null var record sealed permits " +
        "yield non-sealed";

    private const string JavaTypes =
        "String Integer Long Double Float Boolean Character Byte Short Object List ArrayList LinkedList " +
        "Map HashMap LinkedHashMap Set HashSet TreeSet Exception RuntimeException System Math Thread " +
        "Runnable StringBuilder Class Override Deprecated SuppressWarnings Optional Stream Collectors " +
        "Arrays Collections Objects Integer Long Double";

    private const string PythonKeywords =
        "and as assert async await break class continue def del elif else except finally for from global " +
        "if import in is lambda None nonlocal not or pass raise return True try while with yield False " +
        "match case self cls";

    private const string PythonBuiltins =
        "abs all any bin bool bytearray bytes callable chr dict dir divmod enumerate eval exec filter " +
        "float format frozenset getattr globals hasattr hash help hex id input int isinstance issubclass " +
        "iter len list locals map max memoryview min next object oct open ord pow print property range " +
        "repr reversed round set setattr slice sorted staticmethod str sum super tuple type vars zip " +
        "Exception ValueError TypeError KeyError IndexError RuntimeError print len";

    private const string JsKeywords =
        "break case catch class const continue debugger default delete do else export extends false " +
        "finally for function if import in instanceof new null return super switch this throw true try " +
        "typeof var void while with yield let static async await of as from get set";

    private const string JsBuiltins =
        "Array Boolean Date Error Function JSON Math Number Object Promise Proxy Reflect RegExp String " +
        "Symbol Map Set WeakMap WeakSet parseInt parseFloat isNaN isFinite undefined NaN Infinity " +
        "console document window globalThis require module exports";

    private const string TsKeywords =
        "break case catch class const continue debugger default delete do else export extends false " +
        "finally for function if import in instanceof new null return super switch this throw true try " +
        "typeof var void while with yield let static async await of as from get set " +
        "type interface enum namespace declare abstract implements private protected public readonly " +
        "any unknown never string number boolean symbol object bigint keyof infer is asserts satisfies";

    private const string GoKeywords =
        "break case chan const continue default defer else fallthrough for func go goto if import interface " +
        "map package range return select struct switch type var break case chan const continue default defer " +
        "else fallthrough for func go goto if import interface map package range return select struct switch " +
        "type var nil true iota append bool byte cap close complex copy delete len make new panic print println " +
        "recover any int int8 int16 int32 int64 uint uint8 uint16 uint32 uint64 uintptr float32 float64 string " +
        "bool error comparable";

    private const string GoBuiltins =
        "fmt strings errors os io net http context sync atomic time strconv bytes bufio math sort json xml " +
        "Error String Reader Writer Context WaitGroup Mutex RWMutex Channel Slice Map Array Struct Interface " +
        "Package Import Main Init Type Kind Value";

    private const string KotlinKeywords =
        "as break class continue do else false for fun if in interface is null object package return " +
        "super this throw true try typealias typeof val var when while by catch constructor delegate " +
        "dynamic field file finally get import init param property receiver set setparam where actual " +
        "abstract annotation companion const crossinline data enum expect external final infix inline " +
        "inner internal lateinit noinline open operator out override private protected public reified " +
        "sealed suspend tailrec vararg";

    private const string SwiftKeywords =
        "associatedtype class deinit enum extension fileprivate func import init inout internal let open " +
        "operator private protocol public static struct subscript typealias var break case continue " +
        "default defer do else fallthrough for guard if in repeat return switch where while as Any catch " +
        "false is nil rethrows super self Self throw throws true try async await actor some any";
}
