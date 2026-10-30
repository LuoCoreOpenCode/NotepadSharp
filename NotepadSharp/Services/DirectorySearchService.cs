using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace NotepadSharp.Services;

/// <summary>一条目录查找命中（Line 为 1 基，Column 为 0 基字符偏移）</summary>
/// <param name="FilePath">命中所在文件的完整路径</param>
/// <param name="Line">命中所在行号，1 基，与结果面板显示一致</param>
/// <param name="Column">命中在该行内的字符偏移，0 基（UTF-16 码元口径）</param>
/// <param name="Length">命中文本长度；正则的零宽匹配为 0</param>
/// <param name="LineText">所在行文本，超长已被 Shorten 截断</param>
public sealed record DirMatch(string FilePath, int Line, int Column, int Length, string LineText);

/// <summary>一个文件的命中集合，供结果面板按文件分组展示</summary>
public sealed class DirFileResult
{
    /// <summary>文件完整路径，也是分组的唯一键</summary>
    public required string FilePath { get; init; }
    /// <summary>该文件内全部命中，按行序与行内列序追加</summary>
    public List<DirMatch> Matches { get; } = new();

    /// <summary>纯文件名（不含目录），结果树叶子节点显示用</summary>
    public string DisplayName => Path.GetFileName(FilePath);
    /// <summary>所在目录路径；位于磁盘根等无父目录时为空串</summary>
    public string DirectoryName => Path.GetDirectoryName(FilePath) ?? string.Empty;
}

/// <summary>目录查找/替换的输入条件（字段与 ND2 查找窗「在目录查找」那一页一一对应）</summary>
public sealed class DirSearchOptions
{
    /// <summary>扫描根目录的完整路径，队列式遍历的种子；不存在或无权限时整轮为空结果</summary>
    public string RootPath { get; set; } = string.Empty;
    /// <summary>查找框原文，转义还原由 Condition/EffectiveText 统一处理</summary>
    public string FindText { get; set; } = string.Empty;
    /// <summary>替换框原文，仅 ReplaceAsync 用到</summary>
    public string ReplaceText { get; set; } = string.Empty;

    /// <summary>普通 / 扩展(\n,\r,\t,\0,\x...) / 正则，与 ND2 的三选一单选框一致</summary>
    public SearchMode Mode { get; set; } = SearchMode.Normal;
    /// <summary>是否区分大小写：普通/扩展走 Ordinal 系比较，正则经 ToRegexOptions 换算</summary>
    public bool MatchCase { get; set; }
    /// <summary>是否全词：普通/扩展由邻居字符判定，正则用 \b(?:...)\b 包裹实现</summary>
    public bool WholeWord { get; set; }

    /// <summary>跳过带 Hidden 或 System 属性的文件与子目录</summary>
    public bool SkipHidden { get; set; } = true;
    /// <summary>ND2 skipChildDirs：勾上就只扫根目录，不进子目录</summary>
    public bool SkipChildDirs { get; set; }
    /// <summary>ND2 skipBinary：按后缀属于二进制类型则跳过</summary>
    public bool SkipBinary { get; set; } = true;
    /// <summary>ND2 skipFileMaxSize + maxFileSizeSpinBox：不勾就等于不限大小</summary>
    public bool SkipBigFile { get; set; } = true;
    /// <summary>SkipBigFile 生效时的单文件大小上限（MB），超限整个文件不读也不查</summary>
    public int MaxFileMb { get; set; } = 20;

    /// <summary>ND2 dealFileType：不勾就是所有文件</summary>
    public bool FilterByFileType { get; set; } = true;
    /// <summary>文件类型模式串，如 *.c:*.cpp:*.h；由 ParseFilter 解析，*.* 或空串视为不过滤</summary>
    public string FileFilter { get; set; } = "*.*";

    /// <summary>ND2 skipDir + skipDirNames：按目录名精确（区分大小写）匹配后整棵跳过</summary>
    public bool SkipDirNames { get; set; } = true;
    /// <summary>要跳过的目录名清单，照 ND2 只用 ':' 分隔；命中相对路径中的任意一级即剪掉整棵子树</summary>
    public string SkipDirNameList { get; set; } = "debug:Debug:.vs:.git:.svn";

    /// <summary>结果护栏：超过就停止，对应 ND2 的「是否继续查找」询问</summary>
    public int MaxFiles { get; set; } = 20000;
    /// <summary>命中总处数护栏：SearchFile 累计到该值立即收摊，防止巨型结果拖垮面板</summary>
    public int MaxMatches { get; set; } = 200000;

    /// <summary>把上面的查找条件打包成一个可直接用的描述符</summary>
    /// <returns>与 FindText/Mode/MatchCase/WholeWord 对应的 SearchCondition；属性每次读取都新建实例，按行取用时留意开销</returns>
    public SearchCondition Condition => new(FindText, Mode, MatchCase, WholeWord);
}

/// <summary>扫描进度（同时用作替换计数上报）</summary>
/// <param name="FilesScanned">已遍历（或已替换）的文件数</param>
/// <param name="MatchesFound">累计命中处数；替换场景下是累计替换次数</param>
/// <param name="CurrentFile">正在处理的文件名；Done 时为空串</param>
/// <param name="Done">本轮枚举是否已收尾（正常结束或达护栏截断都会报到）</param>
public sealed record DirProgress(int FilesScanned, int MatchesFound, string CurrentFile, bool Done);

/// <summary>
/// 目录级查找/替换：后台线程枚举文件、按各自编码解码、逐行匹配。
/// 结果按文件分组返回；替换时按原编码写回，只读/二进制文件跳过。
/// </summary>
public static class DirectorySearchService
{
    /// <summary>
    /// 后台线程扫描整个目录树做查找：结果按文件分组返回。
    /// Task.Run 里没有共享可变状态跨线程读写，计数变量都是这段 lambda 的局部捕获；
    /// 取消通过 CancellationToken 传导（EnumerateFiles 处与 SearchFile 逐行处各检查一次）。
    /// </summary>
    /// <param name="options">本次扫描的全部条件</param>
    /// <param name="progress">进度回调，可空；IProgress 会把上报投递回创建它的同步上下文（UI 线程）</param>
    /// <param name="token">取消令牌：在文件枚举与行匹配两处生效，取消时抛 OperationCanceledException</param>
    /// <param name="continueAsk">
    /// 命中面变大时询问用户是否继续（参数：有命中的文件数、命中总处数；返回 false 表示中止）。
    /// 阈值与 ND2 一致：命中文件&gt;100 或命中&gt;1000 问第一次，命中&gt;8000 再问第二次，每档只问一次。
    /// </param>
    /// <returns>命中结果与各维度计数；用户点「停止」或触发护栏时返回的是截止当下的部分结果</returns>
    public static async Task<DirSearchSummary> SearchAsync(
        DirSearchOptions options, IProgress<DirProgress>? progress, CancellationToken token,
        Func<int, int, bool>? continueAsk = null)
    {
        var results = new List<DirFileResult>();
        // files=已遍历文件数，matches=累计命中，hitFiles=有命中的文件数；两个 ask*Enabled 保证每档只问一次
        int files = 0, matches = 0, hitFiles = 0;
        bool askFirstEnabled = true, askSecondEnabled = true;
        await Task.Run(() =>
        {
            // 枚举器每吐一个路径就处理一个；token 在 EnumerateFiles 内部逐文件检查，取消即抛
            foreach (var path in EnumerateFiles(options, token))
            {
                // 先报进度再干活：面板能显示「正在查哪个文件」
                progress?.Report(new DirProgress(files, matches, Path.GetFileName(path), false));

                var file = SearchFile(path, options, ref matches, token);
                files++;
                if (file == null) continue; // 读不了/被跳过/没命中的文件不进分组

                results.Add(file);
                hitFiles++;
                if (matches >= options.MaxMatches) break; // 命中护栏：到量收摊，结果保留已有的

                if (continueAsk == null) continue;
                // 第一档询问：命中文件数或命中处数越过 100/1000，问完置 false 不再重复
                if (askFirstEnabled && (hitFiles > 100 || matches > 1000))
                {
                    askFirstEnabled = false;
                    if (!continueAsk(hitFiles, matches)) break;
                }
                // 第二档询问：命中处数越过 8000 再问一次
                if (askSecondEnabled && matches > 8000)
                {
                    askSecondEnabled = false;
                    if (!continueAsk(hitFiles, matches)) break;
                }
            }
            // 收尾播报：无论正常走完、触护栏还是用户拒绝继续，都补一条 Done=true
            progress?.Report(new DirProgress(files, matches, string.Empty, true));
        }, token);
        return new DirSearchSummary(results, files, matches, hitFiles);
    }

    /// <summary>一次目录查找的汇总：结果、遍历文件数、命中处数、有命中的文件数</summary>
    /// <param name="Results">按文件分组的全部命中，顺序与枚举顺序一致</param>
    /// <param name="FilesWalked">实际遍历过的文件数（含无命中的）</param>
    /// <param name="Matches">命中总处数</param>
    /// <param name="HitFiles">至少有一条命中的文件数</param>
    public sealed record DirSearchSummary(List<DirFileResult> Results, int FilesWalked, int Matches, int HitFiles);

    /// <summary>批量替换：逐文件写回，返回改动文件数、替换次数、已改动路径与跳过原因</summary>
    /// <param name="options">替换条件（FindText/ReplaceText/Mode 等与查找共用一套）</param>
    /// <param name="progress">进度回调，可空；此处 FilesScanned 计的是发生改动的文件数</param>
    /// <param name="token">取消令牌；注意已写回的文件不会回滚，取消只保证不再继续</param>
    /// <returns>四元组：改动文件数 / 替换总次数 / 已改动路径清单 / 被跳过文件与原因清单</returns>
    public static async Task<(int Files, int Replacements, List<string> Changed, List<string> Skipped)> ReplaceAsync(
        DirSearchOptions options, IProgress<DirProgress>? progress, CancellationToken token)
    {
        int files = 0, replacements = 0;
        var changed = new List<string>();
        var skipped = new List<string>();
        await Task.Run(() =>
        {
            // 与 SearchAsync 同一套枚举口径（隐藏/大小/类型/目录名过滤全都生效）
            foreach (var path in EnumerateFiles(options, token))
            {
                // 读文本并记下原编码与 BOM 有无——写回时要按原样还原，不能统一转成 UTF-8
                var info = ReadText(path, options, out int codePage, out bool bom);
                if (info == null) continue; // 读失败或按选项跳过（二进制/超限）
                if (MatchCount(info.Text, options) == 0) continue; // 无命中不碰文件，避免白白刷新修改时间

                // 先替换到内存再落盘；失败（只读/占用/无权限）只记入 skipped，不中断整轮
                string? error = TryWriteReplaced(path, info, options, out int done);
                if (error != null) skipped.Add($"{Path.GetFileName(path)}：{error}");
                else if (done > 0)
                {
                    files++;
                    replacements += done;
                    changed.Add(path);
                }
                progress?.Report(new DirProgress(files, replacements, Path.GetFileName(path), false));
            }
            progress?.Report(new DirProgress(files, replacements, string.Empty, true));
        }, token);
        return (files, replacements, changed, skipped);
    }

    /// <summary>一次读入的文件快照：解码后的文本连同编码口径，供匹配与等编码写回</summary>
    /// <param name="Path">文件完整路径</param>
    /// <param name="Text">解码并剥掉 BOM 后的全文文本</param>
    /// <param name="CodePage">探测到的代码页，写回时沿用</param>
    /// <param name="Bom">原文件是否带 BOM，写回时决定要不要补回</param>
    private record FileText(string Path, string Text, int CodePage, bool Bom);

    /// <summary>读文件并按探测到的编码解码；读取失败或按选项跳过二进制时返回 null</summary>
    /// <param name="path">文件完整路径</param>
    /// <param name="options">扫描选项（本函数只用到 SkipBinary）</param>
    /// <param name="codePage">出参：探测到的代码页；默认 UTF-8，解码失败也不回滚</param>
    /// <param name="bom">出参：原文件是否带 BOM</param>
    /// <returns>文件快照；读不了、解码炸了或被选项剪掉时返回 null</returns>
    private static FileText? ReadText(string path, DirSearchOptions options, out int codePage, out bool bom)
    {
        // 先给默认值再干活：下面任何一条 return null 路径，out 参数都保持已初始化状态
        codePage = EncodingService.CpUtf8;
        bom = false;
        byte[] bytes;
        // 整读字节：占用中/无权限/路径失效等异常统一按「跳过此文件」处理
        try { bytes = File.ReadAllBytes(path); }
        catch { return null; }
        // ND2 只按已知二进制后缀跳过（findwin.cpp:2744-2746），不看内容
        if (options.SkipBinary && BinaryFileTypes.IsHexExtension(path)) return null;

        // 探测编码（BOM/启发式）后解码；stripBom 让正文不含 U+FEFF，写回时由 Bom 标志补回
        (codePage, bom) = EncodingService.Detect(bytes);
        // 非法字节序列可能让解码抛异常，同样按跳过处理
        try { return new FileText(path, EncodingService.Decode(bytes, codePage, stripBom: true), codePage, bom); }
        catch { return null; }
    }

    /// <summary>
    /// 单文件查找：逐行取命中位置攒成分组结果。totalMatches 跨文件共享（ref 传入），
    /// 每匹配一行检查一次取消；达到 MaxMatches 时带着已攒的命中提前收口。
    /// </summary>
    /// <param name="path">待查文件路径</param>
    /// <param name="options">查找条件</param>
    /// <param name="totalMatches">全局命中计数，本文件的增量直接累加进去</param>
    /// <param name="token">取消令牌：逐行检查，取消时抛 OperationCanceledException 由外层 Task.Run 传导</param>
    /// <returns>该文件的命中分组；无命中或读文件被跳过时返回 null（调用方以此区分「有结果」）</returns>
    private static DirFileResult? SearchFile(string path, DirSearchOptions options, ref int totalMatches, CancellationToken token)
    {
        var info = ReadText(path, options, out _, out _);
        if (info == null) return null;

        // 分组对象懒创建：第一条命中出现时才 new，无命中的文件不留下空壳
        DirFileResult? result = null;
        foreach (var line in EachLine(info.Text))
        {
            // 行级取消检查点：大文件里点「停止」能在一行内生效
            token.ThrowIfCancellationRequested();
            foreach (var (column, length) in MatchPositions(line.Line, options))
            {
                // Line 转成 1 基行号（Index 是 0 基），行文本截短后进面板
                (result ??= new DirFileResult { FilePath = path }).Matches.Add(
                    new DirMatch(path, line.Index + 1, column, length, Shorten(line.Line)));
                totalMatches++;
                if (totalMatches >= options.MaxMatches) return result; // 全局护栏：截断返回部分结果
            }
        }
        return result;
    }

    /// <summary>统计整份文本的命中总处数（逐行调用 MatchPositions 求和），替换前用来跳过无命中文件</summary>
    /// <param name="text">解码后的全文</param>
    /// <param name="options">查找条件</param>
    /// <returns>命中处数；正则零宽匹配也计入</returns>
    private static int MatchCount(string text, DirSearchOptions options)
    {
        int count = 0;
        foreach (var line in EachLine(text))
            count += MatchPositions(line.Line, options).Count;
        return count;
    }

    /// <summary>把文件内所有行的命中替换掉并按原编码写回；返回 null 表示成功</summary>
    /// <param name="path">目标文件路径</param>
    /// <param name="info">此前 ReadText 得到的快照（含编码与 BOM 口径）</param>
    /// <param name="options">查找与替换条件</param>
    /// <param name="replacements">出参：实际发生的替换处数；0 表示没动文件</param>
    /// <returns>错误消息字符串；null 代表成功（或无命中未写盘）</returns>
    private static string? TryWriteReplaced(string path, FileText info, DirSearchOptions options, out int replacements)
    {
        replacements = 0;
        try
        {
            // 只读文件先挡掉，别等到写盘才炸
            if ((new FileInfo(path).Attributes & FileAttributes.ReadOnly) != 0) return "文件只读";

            // 逐行替换后原样拼回行尾符：\r\n 的行还是 \r\n、\n 的行还是 \n，混用也保持混用
            var sb = new StringBuilder(info.Text.Length);
            foreach (var line in EachLine(info.Text))
            {
                string body = ReplaceInLine(line.Line, options, ref replacements);
                sb.Append(body).Append(line.Ending);
            }

            // 一行都没换到就不碰磁盘（免得刷新文件时间戳）
            if (replacements == 0) return null;
            // 按原代码页 + 原 BOM 有无写回，编码保持与 ND2 一致
            EncodingService.WriteFile(path, sb.ToString(), info.CodePage, info.Bom);
            return null;
        }
        catch (Exception ex) { return ex.Message; } // 写盘失败（占用/无权限）把原因带回给 skipped 列表
    }

    /// <summary>
    /// 行内替换。正则走 Regex.Replace，替换串里的 $1/\1 反向引用照 ND2 的
    /// replaceTargetRegExMode 支持；普通/扩展模式按字面替换。
    /// </summary>
    /// <param name="line">行文本（已剥去行尾符）</param>
    /// <param name="options">查找与替换条件</param>
    /// <param name="replacements">全局替换计数，本行的替换次数累加进去</param>
    /// <returns>替换后的行文本；无可替换或正则非法时原样返回</returns>
    private static string ReplaceInLine(string line, DirSearchOptions options, ref int replacements)
    {
        var condition = options.Condition;
        string needle = condition.EffectiveText; // 扩展模式在这里吃转义，普通模式是原文
        if (needle.Length == 0) return line;

        if (condition.Mode == SearchMode.Regex)
        {
            // 全词用 \b(?:...)\b 包裹：(?:) 非捕获组保证 a|b 这类选择不会被 \b 只咬住首尾分支
            string pattern = condition.WholeWord ? $@"\b(?:{needle})\b" : needle;
            // 替换串按同一模式还原转义，交给 Match.Result 解释 $1/$0 等反向引用
            string replacement = condition.PrepareReplacement(options.ReplaceText);
            try
            {
                // 用 MatchEvaluator 重载顺路计数；零宽匹配由 Regex 引擎自动前进，不会死循环
                int count = 0;
                string result = Regex.Replace(line, pattern,
                    m => { count++; return m.Result(replacement); },
                    condition.ToRegexOptions());
                replacements += count;
                return result;
            }
            catch (ArgumentException)
            {
                // 正则语法或替换串反向引用非法：整行原样退回，不中断批量任务
                return line;
            }
        }

        // 普通/扩展：字面匹配。Ordinal 系比较保证行为不随区域设置漂移
        var comparison = condition.MatchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        // 快速否掉整行无命中的情况，省掉 StringBuilder 分配
        if (line.Length < needle.Length || line.IndexOf(needle, comparison) < 0) return line;

        var sb = new StringBuilder(line.Length);
        int at = 0; // 已抄写到 sb 的游标
        while (at <= line.Length - needle.Length)
        {
            int found = line.IndexOf(needle, at, comparison);
            if (found < 0) break;
            // 全词判定：命中前后紧邻的字符都得不是「词字符」（字母数字下划线）
            bool wordOk = !condition.WholeWord
                || (!IsWordChar(CharBefore(line, found)) && !IsWordChar(CharAfter(line, found + needle.Length)));
            if (!wordOk)
            {
                // 全词不成立：这段也按普通文本抄掉，游标越过它继续找下一处
                sb.Append(line, at, found + needle.Length - at);
                at = found + needle.Length;
                continue;
            }
            // 抄写命中前的残留文本，再拼上替换文本（扩展模式已还原转义）
            sb.Append(line, at, found - at).Append(condition.PrepareReplacement(options.ReplaceText));
            replacements++;
            // 整个 needle 一次跨过，命中文本不再参与后续匹配
            at = found + needle.Length;
        }
        // 收尾：最后一段命中之后的行尾残留
        sb.Append(line, at, line.Length - at);
        return sb.ToString();
    }

    /// <summary>行文本进面板前的截短：超过 240 字符掐头去尾加省略号，防止超长行拖垮列表</summary>
    /// <param name="line">原始行文本</param>
    /// <returns>原文或前 240 字符加 "…" 的展示文本</returns>
    private static string Shorten(string line)
        => line.Length <= 240 ? line : line[..240] + "…";

    /// <summary>逐行切分后的单行信息，行尾符与正文分开存</summary>
    /// <param name="Line">行正文（不含行尾符）</param>
    /// <param name="Ending">该行原有的行尾符：\r\n、\n 或空（末行）</param>
    /// <param name="Index">0 基行号</param>
    private readonly record struct LineInfo(string Line, string Ending, int Index);

    /// <summary>逐行切分；行尾符单独保留，替换写回时不改变原文件的换行风格</summary>
    /// <param name="text">全文文本</param>
    /// <returns>各行 (正文, 行尾符, 0 基行号) 的惰性序列；\r\n 与 \n 都认，纯 \r 的老式换行不认</returns>
    private static IEnumerable<LineInfo> EachLine(string text)
    {
        int start = 0, index = 0;
        // 条件带等号：文本恰以 \n 结尾时多吐一行空行，与编辑器「末尾还有一行」的行数口径一致
        while (start <= text.Length)
        {
            // 以 \n 为切分点；\r\n 的 \r 挂在 body 尾巴上，下面单独摘出来
            int next = text.IndexOf('\n', start);
            string body = next < 0 ? text[start..] : text[start..next];
            string ending;
            // 还原行尾符：正文以 \r 收尾就是 \r\n；否则是 \n；整行没匹配到 \n（末行）就没有行尾
            if (body.EndsWith("\r")) { ending = "\r\n"; body = body[..^1]; }
            else ending = next < 0 ? string.Empty : "\n";

            yield return new LineInfo(body, ending, index);
            if (next < 0) break; // 已经没有下一个换行，这行就是最后一行
            start = next + 1;
            index++;
        }
    }

    /// <summary>一行内所有命中的 (列, 长度)；正则允许零宽匹配，普通查找不会重叠</summary>
    /// <param name="line">行文本（不含行尾符）</param>
    /// <param name="options">查找条件</param>
    /// <returns>按列升序的命中列表；空文本/无命中返回空表</returns>
    private static List<(int Column, int Length)> MatchPositions(string line, DirSearchOptions options)
    {
        var hits = new List<(int, int)>();
        var condition = options.Condition;
        string needle = condition.EffectiveText;
        if (needle.Length == 0) return hits; // 空查找串不算命中，防死循环

        if (condition.Mode == SearchMode.Regex)
        {
            // 全词包裹与 ReplaceInLine 同款：\b(?:...)\b，(?:) 防选择分支被拆
            string pattern = condition.WholeWord ? $@"\b(?:{needle})\b" : needle;
            try
            {
                // 逐行喂给正则所以不需要多行模式；^ $ 天然只对本行生效
                // 零宽匹配（如 a*）会被收进 hits，(列, 0) 交由面板显示为插入点标记
                foreach (Match m in Regex.Matches(line, pattern, condition.ToRegexOptions()))
                    hits.Add((m.Index, m.Length));
            }
            catch (ArgumentException) { } // 正则非法：静默当无命中，不让单个坏 pattern 炸掉整轮扫描
            return hits;
        }

        if (line.Length < needle.Length) return hits; // 行比needle还短，不可能命中
        // 大小写口径与 ReplaceInLine 保持一致：Ordinal 系，不吃区域设置
        var comparison = condition.MatchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

        // 逐处推进查找：每次从上一处命中末尾继续，天然不重叠
        int at = 0;
        while (at <= line.Length - needle.Length)
        {
            int found = line.IndexOf(needle, at, comparison);
            if (found < 0) break;
            // 全词：命中前后紧邻字符都不是词字符才算数（行首/行尾经 CharBefore/CharAfter 视作 '\0'，放行）
            bool wordOk = !condition.WholeWord
                || (!IsWordChar(CharBefore(line, found)) && !IsWordChar(CharAfter(line, found + needle.Length)));
            if (wordOk) hits.Add((found, needle.Length));
            at = found + needle.Length;
        }
        return hits;
    }

    /// <summary>取 index 前一位字符，越到行首以外返回 '\0'（全词判定的边界哨兵）</summary>
    /// <param name="s">行文本</param>
    /// <param name="index">命中起点</param>
    /// <returns>前一个字符或 '\0'</returns>
    private static char CharBefore(string s, int index) => index > 0 ? s[index - 1] : '\0';
    /// <summary>取 index 处字符，越过行尾返回 '\0'（全词判定的边界哨兵）</summary>
    /// <param name="s">行文本</param>
    /// <param name="index">命中终点（needle 长度处）</param>
    /// <returns>后一个字符或 '\0'</returns>
    private static char CharAfter(string s, int index) => index < s.Length ? s[index] : '\0';
    /// <summary>词字符判定：字母、数字、下划线，与正则 \w 的常见子集口径一致</summary>
    /// <param name="c">待判定字符</param>
    /// <returns>是全词匹配中不允许紧贴命中的字符则 true</returns>
    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_';

    /// <summary>
    /// 对外层的薄封装：每吐一个路径检查一次取消令牌。
    /// 惰性枚举 + ThrowIfCancellationRequested 让「停止」能在深目录里及时生效，不必等整轮跑完。
    /// </summary>
    /// <param name="options">过滤口径与护栏</param>
    /// <param name="token">取消令牌</param>
    /// <returns>通过全部过滤条件的文件路径序列</returns>
    private static IEnumerable<string> EnumerateFiles(DirSearchOptions options, CancellationToken token)
    {
        foreach (var path in EnumerateFilesCore(options))
        {
            token.ThrowIfCancellationRequested();
            yield return path;
        }
    }

    /// <summary>
    /// BFS 队列式目录遍历（不是递归，栈深不构成风险）：文件按护栏逐个产出，
    /// 子目录满足隐藏/跳过名条件才入队；无权限或路径炸了的目录直接 continue 跳过整棵。
    /// 注意：遇到目录链接环没有visited去重，靠 MaxFiles 护栏兜底截断。
    /// </summary>
    /// <param name="options">过滤口径与护栏</param>
    /// <returns>可枚举到的文件路径，最多 MaxFiles 个</returns>
    private static IEnumerable<string> EnumerateFilesCore(DirSearchOptions options)
    {
        int yielded = 0; // 已产出文件数，同时充当 MaxFiles 护栏的计数
        var extensions = ParseFilter(options.FileFilter);
        // 与 ND2 一致：没勾类型过滤、模式串为空或就是 *.*，都等于不按类型筛
        bool filterByType = options.FilterByFileType
            && options.FileFilter.Trim().Length > 0
            && options.FileFilter.Trim() != "*.*"
            && extensions.Count > 0;
        var skipNames = options.SkipDirNames ? ParseDirNames(options.SkipDirNameList) : new List<string>();
        // 大小上限换算成字节；不勾 SkipBigFile 时置 0，下面的判断用 >0 短路掉
        long maxBytes = options.SkipBigFile ? (long)options.MaxFileMb * 1024 * 1024 : 0;
        var queue = new Queue<string>();
        queue.Enqueue(options.RootPath);

        // 队列消费循环：目录出队 → 吐它的文件 → 子目录入队，天然逐层展开
        while (queue.Count > 0 && yielded < options.MaxFiles)
        {
            string dir = queue.Dequeue();
            IEnumerable<string> files;
            // 无访问权限/目录已消失：整棵放弃，不影响兄弟目录
            try { files = Directory.EnumerateFiles(dir); }
            catch { continue; }

            foreach (var file in files)
            {
                if (yielded >= options.MaxFiles) yield break; // 枚举中途撞护栏：立刻收摊
                try
                {
                    var info = new FileInfo(file);
                    // 隐藏/系统文件按属性剔除
                    if (options.SkipHidden && (info.Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0) continue;
                    // 超过大小上限的整个不读（二进制探测、解码都在 ReadText，这里先省一步）
                    if (maxBytes > 0 && info.Length > maxBytes) continue;
                    // ND2 fileTypeFilter：开了类型过滤时，没有后缀的文件一律跳过
                    if (filterByType && !MatchesExtensions(file, extensions)) continue;
                }
                catch { continue; } // 单文件属性都读不到（断链等）：跳过这个文件
                yielded++;
                yield return file;
            }

            // 勾了「不进入子目录」就只看根目录这一层
            if (options.SkipChildDirs) continue;
            IEnumerable<string> subDirs;
            try { subDirs = Directory.EnumerateDirectories(dir); }
            catch { continue; } // 子目录枚举炸了（权限等）：放弃这层展开
            foreach (var sub in subDirs)
            {
                try
                {
                    var info = new DirectoryInfo(sub);
                    if (options.SkipHidden && (info.Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0) continue;
                    // 相对路径任意一级命中跳过名单（.vs/.git 之类）就剪掉整棵子树
                    if (IsSkippedDirectory(sub, options.RootPath, skipNames)) continue;
                }
                catch { continue; }
                queue.Enqueue(sub);
            }
        }
    }

    /// <summary>ND2 用 QStringList::indexOf 精确匹配目录名，所以这里是区分大小写的</summary>
    /// <param name="path">待检查的子目录完整路径</param>
    /// <param name="root">扫描根目录，用于求相对路径</param>
    /// <param name="skips">跳过目录名清单（已按 ':' 拆好）</param>
    /// <returns>相对路径中任意一级命中名单则 true，整棵子树不进入</returns>
    private static bool IsSkippedDirectory(string path, string root, List<string> skips)
    {
        if (skips.Count == 0) return false;
        // 取「相对根」的路径再按分隔符拆级：只有子树内的目录名参与匹配，根路径自身不受名单影响
        var relative = Path.GetRelativePath(root, path)
            .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        // Ordinal 比较：Debug 与 debug 是两个名字，照 ND2 的大小写敏感口径
        return relative.Any(part => skips.Contains(part, StringComparer.Ordinal));
    }

    /// <summary>支持用 ':' / ';' / ',' 分隔多个模式，如 *.c:*.cpp:*.h</summary>
    /// <param name="filter">原始模式串</param>
    /// <returns>去点、去空白、忽略大小写去重后的后缀表（不含 *，单独的 * 表示全匹配）</returns>
    public static List<string> ParseFilter(string filter)
        => filter.Split(':', ';', ',')
                 .Select(p => p.Trim())
                 .Where(p => p.Length > 0)
                 // *.c 拆成 c；不写成 *. 前缀的项（如裸 c 或 *）原样保留
                 .Select(p => p.StartsWith("*.") ? p[2..] : p)
                 .Distinct(StringComparer.OrdinalIgnoreCase)
                 .ToList();

    /// <summary>跳过目录名列表：ND2 只按 ':' 分隔</summary>
    /// <param name="names">原始清单串，如 debug:Debug:.vs:.git:.svn</param>
    /// <returns>去空白后的目录名列表，大小写原样保留（匹配时区分大小写）</returns>
    private static List<string> ParseDirNames(string names)
        => names.Split(':')
               .Select(p => p.Trim())
               .Where(p => p.Length > 0)
               .ToList();

    /// <summary>文件后缀是否落在过滤表内；表里有 * 则全放行，无后缀文件一律拒绝</summary>
    /// <param name="path">文件完整路径</param>
    /// <param name="extensions">ParseFilter 产出的后缀表</param>
    /// <returns>允许该文件参与扫描则 true</returns>
    private static bool MatchesExtensions(string path, List<string> extensions)
    {
        if (extensions.Count == 0 || extensions.Contains("*")) return true;
        // GetExtension 带着点（.cs），去掉点后查表；List.Contains 默认按 Ordinal 比较，
        // 所以表虽由 IgnoreCase 去重产出，实际匹配仍是大小写敏感的
        string ext = Path.GetExtension(path);
        if (ext.Length > 0) ext = ext[1..];
        return extensions.Contains(ext);
    }
}
