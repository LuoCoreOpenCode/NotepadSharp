namespace NotepadSharp.Services;

public enum DiffKind { Equal, Change, Delete, Insert }

/// <summary>对齐后的一行：AIndex/BIndex 为原文行号（0 基），-1 表示该侧需要占位空行</summary>
public sealed record DiffLine(DiffKind Kind, int AIndex, int BIndex);

/// <summary>
/// 行级差异计算：先掐掉公共前缀/后缀，再对中段做 LCS；
/// 中段过大时退化为「整段视为修改」，避免 O(n*m) 内存爆炸。
/// 输出已经是对齐顺序，两侧补上占位即可逐行并排显示。
/// </summary>
public static class LineDiff
{
    private const long MaxCells = 8_000_000;

    public static List<DiffLine> Compute(
        IReadOnlyList<string> a, IReadOnlyList<string> b, bool ignoreCase, bool ignoreTrivia)
    {
        var keyA = a.Select(k => Normalize(k, ignoreCase, ignoreTrivia)).ToArray();
        var keyB = b.Select(k => Normalize(k, ignoreCase, ignoreTrivia)).ToArray();

        int head = 0;
        while (head < keyA.Length && head < keyB.Length && keyA[head] == keyB[head]) head++;

        int tailA = keyA.Length, tailB = keyB.Length;
        while (tailA > head && tailB > head && keyA[tailA - 1] == keyB[tailB - 1])
        {
            tailA--;
            tailB--;
        }

        var lines = new List<DiffLine>(a.Count + b.Count);
        for (int i = 0; i < head; i++) lines.Add(new DiffLine(DiffKind.Equal, i, i));

        int n = tailA - head, m = tailB - head;
        if ((long)n * m > MaxCells)
        {
            // 中段太大：不逐行匹配，整段当作修改，多余部分按增删处理
            int paired = Math.Min(n, m);
            for (int i = 0; i < paired; i++) lines.Add(new DiffLine(DiffKind.Change, head + i, head + i));
            for (int i = paired; i < n; i++) lines.Add(new DiffLine(DiffKind.Delete, head + i, -1));
            for (int i = paired; i < m; i++) lines.Add(new DiffLine(DiffKind.Insert, -1, head + i));
        }
        else
        {
            foreach (var op in Lcs(keyA, keyB, head, tailA, head, tailB))
                lines.Add(op);
        }

        int tailStart = tailA;
        for (int i = 0; i < keyB.Length - tailB; i++)
            lines.Add(new DiffLine(DiffKind.Equal, tailStart + i, tailB + i));

        return lines;
    }

    /// <summary>把差异段折叠成「修改 + 纯增/纯删」，并让同一段的增删成对显示</summary>
    private static IEnumerable<DiffLine> Lcs(
        string[] keyA, string[] keyB, int aFrom, int aTo, int bFrom, int bTo)
    {
        int n = aTo - aFrom, m = bTo - bFrom;
        // dp[i,j] = 从 (i,j) 到末尾的 LCS 长度
        var dp = new int[(n + 1) * (m + 1)];
        int stride = m + 1;
        for (int i = n - 1; i >= 0; i--)
            for (int j = m - 1; j >= 0; j--)
                dp[i * stride + j] = keyA[aFrom + i] == keyB[bFrom + j]
                    ? dp[(i + 1) * stride + j + 1] + 1
                    : Math.Max(dp[(i + 1) * stride + j], dp[i * stride + j + 1]);

        var raw = new List<DiffLine>(n + m);
        int x = 0, y = 0;
        while (x < n && y < m)
        {
            if (keyA[aFrom + x] == keyB[bFrom + y])
            {
                raw.Add(new DiffLine(DiffKind.Equal, aFrom + x, bFrom + y));
                x++; y++;
            }
            else if (dp[(x + 1) * stride + y] >= dp[x * stride + y + 1])
            {
                raw.Add(new DiffLine(DiffKind.Delete, aFrom + x, -1));
                x++;
            }
            else
            {
                raw.Add(new DiffLine(DiffKind.Insert, -1, bFrom + y));
                y++;
            }
        }
        while (x < n) { raw.Add(new DiffLine(DiffKind.Delete, aFrom + x, -1)); x++; }
        while (y < m) { raw.Add(new DiffLine(DiffKind.Insert, -1, bFrom + y)); y++; }

        // 连续的非 Equal 片段内，把一删一增配对成「修改」，读起来更贴近并排 diff
        var paired = new List<DiffLine>(raw.Count);
        int i0 = 0;
        while (i0 < raw.Count)
        {
            if (raw[i0].Kind == DiffKind.Equal)
            {
                paired.Add(raw[i0]);
                i0++;
                continue;
            }
            int i1 = i0;
            while (i1 < raw.Count && raw[i1].Kind != DiffKind.Equal) i1++;
            var dels = raw.GetRange(i0, i1 - i0).Where(r => r.Kind == DiffKind.Delete).ToList();
            var inss = raw.GetRange(i0, i1 - i0).Where(r => r.Kind == DiffKind.Insert).ToList();
            int both = Math.Min(dels.Count, inss.Count);
            for (int k = 0; k < both; k++)
                paired.Add(new DiffLine(DiffKind.Change, dels[k].AIndex, inss[k].BIndex));
            for (int k = both; k < dels.Count; k++) paired.Add(dels[k]);
            for (int k = both; k < inss.Count; k++) paired.Add(inss[k]);
            i0 = i1;
        }
        return paired;
    }

    /// <summary>差异段起点（对齐后的行号），用于「上一个 / 下一个差异」跳转</summary>
    public static List<int> HunkStarts(IReadOnlyList<DiffLine> lines)
    {
        var starts = new List<int>();
        bool inHunk = false;
        for (int i = 0; i < lines.Count; i++)
        {
            if (lines[i].Kind == DiffKind.Equal) inHunk = false;
            else if (!inHunk)
            {
                starts.Add(i);
                inHunk = true;
            }
        }
        return starts;
    }

    private static string Normalize(string line, bool ignoreCase, bool ignoreTrivia)
    {
        string s = line;
        if (ignoreTrivia) s = s.TrimEnd();
        if (ignoreCase) s = s.ToLowerInvariant();
        return s;
    }
}
