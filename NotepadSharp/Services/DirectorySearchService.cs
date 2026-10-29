using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace NotepadSharp.Services;

/// <summary>一条目录查找命中（Line 为 1 基，Column 为 0 基字符偏移）</summary>
public sealed record DirMatch(string FilePath, int Line, int Column, int Length, string LineText);

/// <summary>一个文件的命中集合，供结果面板按文件分组展示</summary>
public sealed class DirFileResult
{
    public required string FilePath { get; init; }
    public List<DirMatch> Matches { get; } = new();

    public string DisplayName => Path.GetFileName(FilePath);
    public string DirectoryName => Path.GetDirectoryName(FilePath) ?? string.Empty;
}

/// <summary>目录查找/替换的输入条件（字段与 ND2 查找窗「在目录查找」那一页一一对应）</summary>
public sealed class DirSearchOptions
{
    public string RootPath { get; set; } = string.Empty;
    public string FindText { get; set; } = string.Empty;
    public string ReplaceText { get; set; } = string.Empty;

    /// <summary>普通 / 扩展(\n,\r,\t,\0,\x...) / 正则，与 ND2 的三选一单选框一致</summary>
    public SearchMode Mode { get; set; } = SearchMode.Normal;
    public bool MatchCase { get; set; }
    public bool WholeWord { get; set; }

    public bool SkipHidden { get; set; } = true;
    /// <summary>ND2 skipChildDirs：勾上就只扫根目录，不进子目录</summary>
    public bool SkipChildDirs { get; set; }
    /// <summary>ND2 skipBinary：按后缀属于二进制类型则跳过</summary>
    public bool SkipBinary { get; set; } = true;
    /// <summary>ND2 skipFileMaxSize + maxFileSizeSpinBox：不勾就等于不限大小</summary>
    public bool SkipBigFile { get; set; } = true;
    public int MaxFileMb { get; set; } = 20;

    /// <summary>ND2 dealFileType：不勾就是所有文件</summary>
    public bool FilterByFileType { get; set; } = true;
    public string FileFilter { get; set; } = "*.*";

    /// <summary>ND2 skipDir + skipDirNames：按目录名精确（区分大小写）匹配后整棵跳过</summary>
    public bool SkipDirNames { get; set; } = true;
    public string SkipDirNameList { get; set; } = "debug:Debug:.vs:.git:.svn";

    /// <summary>结果护栏：超过就停止，对应 ND2 的「是否继续查找」询问</summary>
    public int MaxFiles { get; set; } = 20000;
    public int MaxMatches { get; set; } = 200000;

    /// <summary>把上面的查找条件打包成一个可直接用的描述符</summary>
    public SearchCondition Condition => new(FindText, Mode, MatchCase, WholeWord);
}

/// <summary>扫描进度（同时用作替换计数上报）</summary>
public sealed record DirProgress(int FilesScanned, int MatchesFound, string CurrentFile, bool Done);

/// <summary>
/// 目录级查找/替换：后台线程枚举文件、按各自编码解码、逐行匹配。
/// 结果按文件分组返回；替换时按原编码写回，只读/二进制文件跳过。
/// </summary>
public static class DirectorySearchService
{
    /// <param name="continueAsk">
    /// 命中面变大时询问用户是否继续（参数：有命中的文件数、命中总处数；返回 false 表示中止）。
    /// 阈值与 ND2 一致：命中文件&gt;100 或命中&gt;1000 问第一次，命中&gt;8000 再问第二次，每档只问一次。
    /// </param>
    public static async Task<DirSearchSummary> SearchAsync(
        DirSearchOptions options, IProgress<DirProgress>? progress, CancellationToken token,
        Func<int, int, bool>? continueAsk = null)
    {
        var results = new List<DirFileResult>();
        int files = 0, matches = 0, hitFiles = 0;
        bool askFirstEnabled = true, askSecondEnabled = true;
        await Task.Run(() =>
        {
            foreach (var path in EnumerateFiles(options, token))
            {
                progress?.Report(new DirProgress(files, matches, Path.GetFileName(path), false));

                var file = SearchFile(path, options, ref matches, token);
                files++;
                if (file == null) continue;

                results.Add(file);
                hitFiles++;
                if (matches >= options.MaxMatches) break;

                if (continueAsk == null) continue;
                if (askFirstEnabled && (hitFiles > 100 || matches > 1000))
                {
                    askFirstEnabled = false;
                    if (!continueAsk(hitFiles, matches)) break;
                }
                if (askSecondEnabled && matches > 8000)
                {
                    askSecondEnabled = false;
                    if (!continueAsk(hitFiles, matches)) break;
                }
            }
            progress?.Report(new DirProgress(files, matches, string.Empty, true));
        }, token);
        return new DirSearchSummary(results, files, matches, hitFiles);
    }

    /// <summary>一次目录查找的汇总：结果、遍历文件数、命中处数、有命中的文件数</summary>
    public sealed record DirSearchSummary(List<DirFileResult> Results, int FilesWalked, int Matches, int HitFiles);

    /// <summary>批量替换：逐文件写回，返回改动文件数、替换次数、已改动路径与跳过原因</summary>
    public static async Task<(int Files, int Replacements, List<string> Changed, List<string> Skipped)> ReplaceAsync(
        DirSearchOptions options, IProgress<DirProgress>? progress, CancellationToken token)
    {
        int files = 0, replacements = 0;
        var changed = new List<string>();
        var skipped = new List<string>();
        await Task.Run(() =>
        {
            foreach (var path in EnumerateFiles(options, token))
            {
                var info = ReadText(path, options, out int codePage, out bool bom);
                if (info == null) continue;
                if (MatchCount(info.Text, options) == 0) continue;

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

    private record FileText(string Path, string Text, int CodePage, bool Bom);

    /// <summary>读文件并按探测到的编码解码；读取失败或按选项跳过二进制时返回 null</summary>
    private static FileText? ReadText(string path, DirSearchOptions options, out int codePage, out bool bom)
    {
        codePage = EncodingService.CpUtf8;
        bom = false;
        byte[] bytes;
        try { bytes = File.ReadAllBytes(path); }
        catch { return null; }
        // ND2 用后缀表判断二进制，这里用内容嗅探做等价判断
        if (options.SkipBinary && EncodingService.LooksBinary(bytes)) return null;

        (codePage, bom) = EncodingService.Detect(bytes);
        try { return new FileText(path, EncodingService.Decode(bytes, codePage, stripBom: true), codePage, bom); }
        catch { return null; }
    }

    private static DirFileResult? SearchFile(string path, DirSearchOptions options, ref int totalMatches, CancellationToken token)
    {
        var info = ReadText(path, options, out _, out _);
        if (info == null) return null;

        DirFileResult? result = null;
        foreach (var line in EachLine(info.Text))
        {
            token.ThrowIfCancellationRequested();
            foreach (var (column, length) in MatchPositions(line.Line, options))
            {
                (result ??= new DirFileResult { FilePath = path }).Matches.Add(
                    new DirMatch(path, line.Index + 1, column, length, Shorten(line.Line)));
                totalMatches++;
                if (totalMatches >= options.MaxMatches) return result;
            }
        }
        return result;
    }

    private static int MatchCount(string text, DirSearchOptions options)
    {
        int count = 0;
        foreach (var line in EachLine(text))
            count += MatchPositions(line.Line, options).Count;
        return count;
    }

    /// <summary>把文件内所有行的命中替换掉并按原编码写回；返回 null 表示成功</summary>
    private static string? TryWriteReplaced(string path, FileText info, DirSearchOptions options, out int replacements)
    {
        replacements = 0;
        try
        {
            if ((new FileInfo(path).Attributes & FileAttributes.ReadOnly) != 0) return "文件只读";

            var sb = new StringBuilder(info.Text.Length);
            foreach (var line in EachLine(info.Text))
            {
                string body = ReplaceInLine(line.Line, options, ref replacements);
                sb.Append(body).Append(line.Ending);
            }

            if (replacements == 0) return null;
            EncodingService.WriteFile(path, sb.ToString(), info.CodePage, info.Bom);
            return null;
        }
        catch (Exception ex) { return ex.Message; }
    }

    /// <summary>
    /// 行内替换。正则走 Regex.Replace，替换串里的 $1/\1 反向引用照 ND2 的
    /// replaceTargetRegExMode 支持；普通/扩展模式按字面替换。
    /// </summary>
    private static string ReplaceInLine(string line, DirSearchOptions options, ref int replacements)
    {
        var condition = options.Condition;
        string needle = condition.EffectiveText;
        if (needle.Length == 0) return line;

        if (condition.Mode == SearchMode.Regex)
        {
            string pattern = condition.WholeWord ? $@"\b(?:{needle})\b" : needle;
            string replacement = condition.PrepareReplacement(options.ReplaceText);
            try
            {
                int count = 0;
                string result = Regex.Replace(line, pattern,
                    m => { count++; return m.Result(replacement); },
                    condition.ToRegexOptions());
                replacements += count;
                return result;
            }
            catch (ArgumentException)
            {
                return line;
            }
        }

        var comparison = condition.MatchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        if (line.Length < needle.Length || line.IndexOf(needle, comparison) < 0) return line;

        var sb = new StringBuilder(line.Length);
        int at = 0;
        while (at <= line.Length - needle.Length)
        {
            int found = line.IndexOf(needle, at, comparison);
            if (found < 0) break;
            bool wordOk = !condition.WholeWord
                || (!IsWordChar(CharBefore(line, found)) && !IsWordChar(CharAfter(line, found + needle.Length)));
            if (!wordOk)
            {
                sb.Append(line, at, found + needle.Length - at);
                at = found + needle.Length;
                continue;
            }
            sb.Append(line, at, found - at).Append(condition.PrepareReplacement(options.ReplaceText));
            replacements++;
            at = found + needle.Length;
        }
        sb.Append(line, at, line.Length - at);
        return sb.ToString();
    }

    private static string Shorten(string line)
        => line.Length <= 240 ? line : line[..240] + "…";

    private readonly record struct LineInfo(string Line, string Ending, int Index);

    /// <summary>逐行切分；行尾符单独保留，替换写回时不改变原文件的换行风格</summary>
    private static IEnumerable<LineInfo> EachLine(string text)
    {
        int start = 0, index = 0;
        while (start <= text.Length)
        {
            int next = text.IndexOf('\n', start);
            string body = next < 0 ? text[start..] : text[start..next];
            string ending;
            if (body.EndsWith("\r")) { ending = "\r\n"; body = body[..^1]; }
            else ending = next < 0 ? string.Empty : "\n";

            yield return new LineInfo(body, ending, index);
            if (next < 0) break;
            start = next + 1;
            index++;
        }
    }

    /// <summary>一行内所有命中的 (列, 长度)；正则允许零宽匹配，普通查找不会重叠</summary>
    private static List<(int Column, int Length)> MatchPositions(string line, DirSearchOptions options)
    {
        var hits = new List<(int, int)>();
        var condition = options.Condition;
        string needle = condition.EffectiveText;
        if (needle.Length == 0) return hits;

        if (condition.Mode == SearchMode.Regex)
        {
            string pattern = condition.WholeWord ? $@"\b(?:{needle})\b" : needle;
            try
            {
                foreach (Match m in Regex.Matches(line, pattern, condition.ToRegexOptions()))
                    hits.Add((m.Index, m.Length));
            }
            catch (ArgumentException) { }
            return hits;
        }

        if (line.Length < needle.Length) return hits;
        var comparison = condition.MatchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

        int at = 0;
        while (at <= line.Length - needle.Length)
        {
            int found = line.IndexOf(needle, at, comparison);
            if (found < 0) break;
            bool wordOk = !condition.WholeWord
                || (!IsWordChar(CharBefore(line, found)) && !IsWordChar(CharAfter(line, found + needle.Length)));
            if (wordOk) hits.Add((found, needle.Length));
            at = found + needle.Length;
        }
        return hits;
    }

    private static char CharBefore(string s, int index) => index > 0 ? s[index - 1] : '\0';
    private static char CharAfter(string s, int index) => index < s.Length ? s[index] : '\0';
    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_';

    private static IEnumerable<string> EnumerateFiles(DirSearchOptions options, CancellationToken token)
    {
        foreach (var path in EnumerateFilesCore(options))
        {
            token.ThrowIfCancellationRequested();
            yield return path;
        }
    }

    private static IEnumerable<string> EnumerateFilesCore(DirSearchOptions options)
    {
        int yielded = 0;
        var extensions = ParseFilter(options.FileFilter);
        bool filterByType = options.FilterByFileType
            && options.FileFilter.Trim().Length > 0
            && options.FileFilter.Trim() != "*.*"
            && extensions.Count > 0;
        var skipNames = options.SkipDirNames ? ParseDirNames(options.SkipDirNameList) : new List<string>();
        long maxBytes = options.SkipBigFile ? (long)options.MaxFileMb * 1024 * 1024 : 0;
        var queue = new Queue<string>();
        queue.Enqueue(options.RootPath);

        while (queue.Count > 0 && yielded < options.MaxFiles)
        {
            string dir = queue.Dequeue();
            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(dir); }
            catch { continue; }

            foreach (var file in files)
            {
                if (yielded >= options.MaxFiles) yield break;
                try
                {
                    var info = new FileInfo(file);
                    if (options.SkipHidden && (info.Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0) continue;
                    if (maxBytes > 0 && info.Length > maxBytes) continue;
                    // ND2 fileTypeFilter：开了类型过滤时，没有后缀的文件一律跳过
                    if (filterByType && !MatchesExtensions(file, extensions)) continue;
                }
                catch { continue; }
                yielded++;
                yield return file;
            }

            if (options.SkipChildDirs) continue;
            IEnumerable<string> subDirs;
            try { subDirs = Directory.EnumerateDirectories(dir); }
            catch { continue; }
            foreach (var sub in subDirs)
            {
                try
                {
                    var info = new DirectoryInfo(sub);
                    if (options.SkipHidden && (info.Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0) continue;
                    if (IsSkippedDirectory(sub, options.RootPath, skipNames)) continue;
                }
                catch { continue; }
                queue.Enqueue(sub);
            }
        }
    }

    /// <summary>ND2 用 QStringList::indexOf 精确匹配目录名，所以这里是区分大小写的</summary>
    private static bool IsSkippedDirectory(string path, string root, List<string> skips)
    {
        if (skips.Count == 0) return false;
        var relative = Path.GetRelativePath(root, path)
            .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return relative.Any(part => skips.Contains(part, StringComparer.Ordinal));
    }

    /// <summary>支持用 ':' / ';' / ',' 分隔多个模式，如 *.c:*.cpp:*.h</summary>
    public static List<string> ParseFilter(string filter)
        => filter.Split(':', ';', ',')
                 .Select(p => p.Trim())
                 .Where(p => p.Length > 0)
                 .Select(p => p.StartsWith("*.") ? p[2..] : p)
                 .Distinct(StringComparer.OrdinalIgnoreCase)
                 .ToList();

    /// <summary>跳过目录名列表：ND2 只按 ':' 分隔</summary>
    private static List<string> ParseDirNames(string names)
        => names.Split(':')
               .Select(p => p.Trim())
               .Where(p => p.Length > 0)
               .ToList();

    private static bool MatchesExtensions(string path, List<string> extensions)
    {
        if (extensions.Count == 0 || extensions.Contains("*")) return true;
        string ext = Path.GetExtension(path);
        if (ext.Length > 0) ext = ext[1..];
        return extensions.Contains(ext);
    }
}
