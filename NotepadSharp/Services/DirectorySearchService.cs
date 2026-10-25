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

/// <summary>目录查找/替换的输入条件（ndd「目录查找」面板的字段集合）</summary>
public sealed class DirSearchOptions
{
    public string RootPath { get; set; } = string.Empty;
    public string FindText { get; set; } = string.Empty;
    public string FileFilter { get; set; } = "*.*";
    public bool Recursive { get; set; } = true;
    public bool MatchCase { get; set; }
    public bool WholeWord { get; set; }
    public bool Regex { get; set; }
    public bool SkipHidden { get; set; } = true;
    /// <summary>跳过相对路径中出现这些目录名（不区分大小写），如 bin:obj:node_modules</summary>
    public string SkipDirectories { get; set; } = "bin:obj:.git:node_modules";
    public long MaxFileBytes { get; set; } = 20L * 1024 * 1024;
    public int MaxFiles { get; set; } = 5000;
    public int MaxMatches { get; set; } = 5000;
}

/// <summary>扫描进度（同时用作替换计数上报）</summary>
public sealed record DirProgress(int FilesScanned, int MatchesFound, string CurrentFile, bool Done);

/// <summary>
/// 目录级查找/替换：后台线程枚举文件、按各自编码解码、逐行匹配。
/// 结果按文件分组返回；替换时按原编码写回，只读/二进制文件跳过。
/// </summary>
public static class DirectorySearchService
{
    public static async Task<List<DirFileResult>> SearchAsync(
        DirSearchOptions options, IProgress<DirProgress>? progress, CancellationToken token)
    {
        var results = new List<DirFileResult>();
        await Task.Run(() =>
        {
            int files = 0, matches = 0;
            foreach (var path in EnumerateFiles(options, token))
            {
                progress?.Report(new DirProgress(files, matches, Path.GetFileName(path), false));

                var file = SearchFile(path, options, ref matches, token);
                if (file != null)
                {
                    results.Add(file);
                    if (matches >= options.MaxMatches) break;
                }
                files++;
            }
            progress?.Report(new DirProgress(files, matches, string.Empty, true));
        }, token);
        return results;
    }

    /// <summary>批量替换：逐文件写回，返回改动文件数、替换次数、已改动路径与跳过原因</summary>
    public static async Task<(int Files, int Replacements, List<string> Changed, List<string> Skipped)> ReplaceAsync(
        DirSearchOptions options, string replaceText, IProgress<DirProgress>? progress, CancellationToken token)
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

                string? error = TryWriteReplaced(path, info, options, replaceText, out int done);
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

    /// <summary>读文件并按探测到的编码解码；二进制/读取失败返回 null</summary>
    private static FileText? ReadText(string path, DirSearchOptions options, out int codePage, out bool bom)
    {
        codePage = EncodingService.CpUtf8;
        bom = false;
        byte[] bytes;
        try { bytes = File.ReadAllBytes(path); }
        catch { return null; }
        if (EncodingService.LooksBinary(bytes)) return null;

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
    private static string? TryWriteReplaced(string path, FileText info, DirSearchOptions options, string replaceText, out int replacements)
    {
        replacements = 0;
        try
        {
            if ((new FileInfo(path).Attributes & FileAttributes.ReadOnly) != 0) return "文件只读";

            var sb = new StringBuilder(info.Text.Length);
            foreach (var line in EachLine(info.Text))
            {
                string body = line.Line;
                var positions = MatchPositions(body, options);
                if (positions.Count > 0)
                {
                    var rewritten = new StringBuilder();
                    int copied = 0;
                    foreach (var (column, length) in positions)
                    {
                        rewritten.Append(body, copied, column - copied).Append(replaceText);
                        copied = column + length;
                    }
                    rewritten.Append(body, copied, body.Length - copied);
                    body = rewritten.ToString();
                    replacements += positions.Count;
                }
                sb.Append(body).Append(line.Ending);
            }

            if (replacements == 0) return null;
            EncodingService.WriteFile(path, sb.ToString(), info.CodePage, info.Bom);
            return null;
        }
        catch (Exception ex) { return ex.Message; }
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

        if (options.Regex)
        {
            var flags = options.MatchCase ? RegexOptions.None : RegexOptions.IgnoreCase;
            try
            {
                foreach (Match m in Regex.Matches(line, options.FindText, flags))
                    hits.Add((m.Index, m.Length));
            }
            catch (ArgumentException) { }
            return hits;
        }

        string needle = options.FindText;
        if (needle.Length == 0 || line.Length < needle.Length) return hits;
        var comparison = options.MatchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

        int at = 0;
        while (at <= line.Length - needle.Length)
        {
            int found = line.IndexOf(needle, at, comparison);
            if (found < 0) break;
            bool wordOk = !options.WholeWord
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
        var skips = ParseFilter(options.SkipDirectories);
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
                    if (options.MaxFileBytes > 0 && info.Length > options.MaxFileBytes) continue;
                    if (!MatchesExtensions(file, extensions)) continue;
                }
                catch { continue; }
                yielded++;
                yield return file;
            }

            if (!options.Recursive) continue;
            IEnumerable<string> subDirs;
            try { subDirs = Directory.EnumerateDirectories(dir); }
            catch { continue; }
            foreach (var sub in subDirs)
            {
                try
                {
                    var info = new DirectoryInfo(sub);
                    if (options.SkipHidden && (info.Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0) continue;
                    if (IsSkippedDirectory(sub, options.RootPath, skips)) continue;
                }
                catch { continue; }
                queue.Enqueue(sub);
            }
        }
    }

    private static bool IsSkippedDirectory(string path, string root, List<string> skips)
    {
        if (skips.Count == 0) return false;
        var relative = Path.GetRelativePath(root, path)
            .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return relative.Any(part => skips.Contains(part));
    }

    /// <summary>ndd 用 ':' / ';' / ',' 分隔多个模式，如 *.c:*.cpp:*.h</summary>
    public static List<string> ParseFilter(string filter)
        => filter.Split(':', ';', ',')
                 .Select(p => p.Trim())
                 .Where(p => p.Length > 0)
                 .Select(p => p.StartsWith("*.") ? p[2..] : p)
                 .Distinct(StringComparer.OrdinalIgnoreCase)
                 .ToList();

    private static bool MatchesExtensions(string path, List<string> extensions)
    {
        if (extensions.Count == 0 || extensions.Contains("*")) return true;
        string ext = Path.GetExtension(path);
        if (ext.Length > 0) ext = ext[1..];
        return extensions.Contains(ext);
    }
}
