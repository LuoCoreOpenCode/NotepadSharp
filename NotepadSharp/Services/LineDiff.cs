namespace NotepadSharp.Services;

/// <summary>一行在对齐结果里的状态：Equal 两侧相同、Change 同段内一删一增配对、Delete 只在左侧、Insert 只在右侧</summary>
public enum DiffKind { Equal, Change, Delete, Insert } // 四个成员依次为：相同 / 修改 / 删除 / 插入

/// <summary>对齐后的一行：AIndex/BIndex 为原文行号（0 基），-1 表示该侧需要占位空行</summary>
/// <param name="Kind">该行在对齐结果里的状态</param>
/// <param name="AIndex">左侧原文的 0 基行号；-1 表示左侧在此处是占位空行（纯插入行）</param>
/// <param name="BIndex">右侧原文的 0 基行号；-1 表示右侧在此处是占位空行（纯删除行）</param>
public sealed record DiffLine(DiffKind Kind, int AIndex, int BIndex);

/// <summary>
/// 行级差异计算：先掐掉公共前缀/后缀，再对中段做 LCS；
/// 中段过大时退化为「整段视为修改」，避免 O(n*m) 内存爆炸。
/// 输出已经是对齐顺序，两侧补上占位即可逐行并排显示。
/// </summary>
public static class LineDiff
{
    /// <summary>LCS 动态规划单元格上限（约 800 万个 int，粗算 32MB）：中段 n*m 超过它就整段退化，杜绝 O(n*m) 内存爆炸</summary>
    private const long MaxCells = 8_000_000;

    /// <summary>
    /// 计算两组行的对齐序列：先掐公共前缀/后缀，中段跑 LCS，规模超过 MaxCells 时整段按「修改+增删」退化。
    /// 行由调用方按换行切好后传入，本类不关心 \r\n 与 \n 的区别。
    /// </summary>
    /// <param name="a">左侧（原文）行序列</param>
    /// <param name="b">右侧（对比文）行序列</param>
    /// <param name="ignoreCase">比较时是否忽略大小写</param>
    /// <param name="ignoreTrivia">比较时是否忽略行尾空白</param>
    /// <returns>并排显示顺序的 DiffLine 序列；AIndex/BIndex 指回各自入参的行号</returns>
    public static List<DiffLine> Compute(
        IReadOnlyList<string> a, IReadOnlyList<string> b, bool ignoreCase, bool ignoreTrivia)
    {
        // 比较键与原文分离：归一化只影响「是否相同」的判定，输出仍然引用原始行号，改行内容不动
        var keyA = a.Select(k => Normalize(k, ignoreCase, ignoreTrivia)).ToArray();
        var keyB = b.Select(k => Normalize(k, ignoreCase, ignoreTrivia)).ToArray();

        // 掐公共前缀：head 之前的行两侧逐行相等，直接对齐、不进 DP
        int head = 0;
        while (head < keyA.Length && head < keyB.Length && keyA[head] == keyB[head]) head++;

        // 掐公共后缀：双指针从各自尾部一起前移；tailA > head 且 tailB > head
        // 保证后缀不会越过前缀、把中段吃穿（两边同时扣，后缀行数两侧恒等）
        int tailA = keyA.Length, tailB = keyB.Length;
        while (tailA > head && tailB > head && keyA[tailA - 1] == keyB[tailB - 1])
        {
            tailA--;
            tailB--;
        }

        // 前缀段全部记 Equal：掐前缀时两侧行号天然一一对应，i 同一个值两用
        var lines = new List<DiffLine>(a.Count + b.Count);
        for (int i = 0; i < head; i++) lines.Add(new DiffLine(DiffKind.Equal, i, i));

        // 中段规模护栏：n*m 是 DP 表面积，先换算成 long 再比，防两个 int 相乘本身溢出
        int n = tailA - head, m = tailB - head;
        if ((long)n * m > MaxCells)
        {
            // 中段太大：不逐行匹配，整段当作修改，多余部分按增删处理
            // 退化路径保持线性时间：前 min(n,m) 行按位置配对成 Change，多出的尾部一侧整段判删/判增
            int paired = Math.Min(n, m);
            for (int i = 0; i < paired; i++) lines.Add(new DiffLine(DiffKind.Change, head + i, head + i));
            for (int i = paired; i < n; i++) lines.Add(new DiffLine(DiffKind.Delete, head + i, -1));
            for (int i = paired; i < m; i++) lines.Add(new DiffLine(DiffKind.Insert, -1, head + i));
        }
        else
        {
            // 正常路径：中段 [head, tail) 区间交给 LCS，产出细粒度的等/删/增序列
            foreach (var op in Lcs(keyA, keyB, head, tailA, head, tailB))
                lines.Add(op);
        }

        // 后缀段补 Equal：掐后缀两侧各扣掉同样多行（keyA.Length-tailA == keyB.Length-tailB），
        // 所以按右侧剩余行数循环即可，A 侧从 tailA、B 侧从 tailB 起逐行对齐
        int tailStart = tailA;
        for (int i = 0; i < keyB.Length - tailB; i++)
            lines.Add(new DiffLine(DiffKind.Equal, tailStart + i, tailB + i));

        return lines;
    }

    /// <summary>把差异段折叠成「修改 + 纯增/纯删」，并让同一段的增删成对显示</summary>
    /// <param name="keyA">左侧归一化比较键数组（全文，不限中段）</param>
    /// <param name="keyB">右侧归一化比较键数组（全文，不限中段）</param>
    /// <param name="aFrom">左侧中段起点（含）</param>
    /// <param name="aTo">左侧中段终点（不含）</param>
    /// <param name="bFrom">右侧中段起点（含）</param>
    /// <param name="bTo">右侧中段终点（不含）</param>
    /// <returns>中段区间的对齐序列，行号是全文 0 基下标而非段内相对值</returns>
    private static IEnumerable<DiffLine> Lcs(
        string[] keyA, string[] keyB, int aFrom, int aTo, int bFrom, int bTo)
    {
        int n = aTo - aFrom, m = bTo - bFrom;
        // dp[i,j] = 从 (i,j) 到末尾的 LCS 长度
        // 二维表摊平成一维 int[] 省掉锯齿数组开销；第 n 行与第 m 列是天然边界（数组零值即 LCS=0，
        // 对应「一侧剩余为空」），所以只需从 (n-1, m-1) 反向填到 (0, 0)
        var dp = new int[(n + 1) * (m + 1)];
        int stride = m + 1; // 每行宽度，dp[i,j] 折算成 dp[i * stride + j]
        for (int i = n - 1; i >= 0; i--)
            for (int j = m - 1; j >= 0; j--)
                // 两侧当前行相等：走对角线加一；不等：取「跳过左行」与「跳过右行」两个方向的较大值
                dp[i * stride + j] = keyA[aFrom + i] == keyB[bFrom + j]
                    ? dp[(i + 1) * stride + j + 1] + 1
                    : Math.Max(dp[(i + 1) * stride + j], dp[i * stride + j + 1]);

        // 回溯：从 (0,0) 沿 dp 表贪心走出具体对齐序列，行号加回 aFrom/bFrom 还原为全文下标
        var raw = new List<DiffLine>(n + m);
        int x = 0, y = 0;
        while (x < n && y < m)
        {
            if (keyA[aFrom + x] == keyB[bFrom + y])
            {
                // 当前行相同：记 Equal，两侧游标一起前进（对角线移动）
                raw.Add(new DiffLine(DiffKind.Equal, aFrom + x, bFrom + y));
                x++; y++;
            }
            else if (dp[(x + 1) * stride + y] >= dp[x * stride + y + 1])
            {
                // 「跳过左行」不劣于「跳过右行」：把左侧当前行判为删除；平局固定偏向删除，结果可预期
                raw.Add(new DiffLine(DiffKind.Delete, aFrom + x, -1));
                x++;
            }
            else
            {
                // 「跳过右行」更优：右侧当前行是新增出来的
                raw.Add(new DiffLine(DiffKind.Insert, -1, bFrom + y));
                y++;
            }
        }
        // 一侧先走到头：另一侧剩余行没有可配对象，全部记纯删除/纯插入
        while (x < n) { raw.Add(new DiffLine(DiffKind.Delete, aFrom + x, -1)); x++; }
        while (y < m) { raw.Add(new DiffLine(DiffKind.Insert, -1, bFrom + y)); y++; }

        // 连续的非 Equal 片段内，把一删一增配对成「修改」，读起来更贴近并排 diff
        // 扫描策略：Equal 原样吐出；遇到非 Equal 就圈出整个差异段再折叠
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
            // i1 探到本段之后第一个 Equal（或表尾），[i0, i1) 即一段连续增删混合区
            int i1 = i0;
            while (i1 < raw.Count && raw[i1].Kind != DiffKind.Equal) i1++;
            // 段内按出现顺序分别收集删行与增行两个队列
            var dels = raw.GetRange(i0, i1 - i0).Where(r => r.Kind == DiffKind.Delete).ToList();
            var inss = raw.GetRange(i0, i1 - i0).Where(r => r.Kind == DiffKind.Insert).ToList();
            // 前 min(删,增) 对按顺序 zip 成 Change：AIndex/BIndex 各自沿用原行号，并排显示正好对上
            int both = Math.Min(dels.Count, inss.Count);
            for (int k = 0; k < both; k++)
                paired.Add(new DiffLine(DiffKind.Change, dels[k].AIndex, inss[k].BIndex));
            // 配不上对的保留原样：多出的删行/增行在另一侧显示为占位空行
            for (int k = both; k < dels.Count; k++) paired.Add(dels[k]);
            for (int k = both; k < inss.Count; k++) paired.Add(inss[k]);
            i0 = i1;
        }
        return paired;
    }

    /// <summary>差异段起点（对齐后的行号），用于「上一个 / 下一个差异」跳转</summary>
    /// <param name="lines">Compute 输出的对齐序列</param>
    /// <returns>每个差异段首行在 lines 中的下标列表，按出现顺序，可直接二分/线性找邻居</returns>
    public static List<int> HunkStarts(IReadOnlyList<DiffLine> lines)
    {
        var starts = new List<int>();
        // inHunk 标记当前是否已站在差异段内部：Equal 把状态清零，
        // 于是每段只在「Equal → 非 Equal」的翻转处记录一次起点，连续差异行不会重复记
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

    /// <summary>生成比较键：先忽略行尾空白、再忽略大小写；只影响相等性判定，不改原文</summary>
    /// <param name="line">原始行文本</param>
    /// <param name="ignoreCase">是否统一转小写比较</param>
    /// <param name="ignoreTrivia">是否忽略行尾空白</param>
    /// <returns>归一化后的比较字符串</returns>
    private static string Normalize(string line, bool ignoreCase, bool ignoreTrivia)
    {
        string s = line;
        // 只 TrimEnd：行尾空白算「无关差异」，行首缩进的变化是真实改动，不能吞
        if (ignoreTrivia) s = s.TrimEnd();
        // ToLowerInvariant 而非 ToLower：结果不受当前区域设置影响（避免土耳其语 i 之类的问题）
        if (ignoreCase) s = s.ToLowerInvariant();
        return s;
    }
}
