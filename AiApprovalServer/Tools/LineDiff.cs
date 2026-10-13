using System.Text;

namespace AiApproval.Tools;

//行级差异对比
public static class LineDiff
{
    //逐行对比上限
    private const int MaxDpLines = 600;
    //输出行数上限
    private const int MaxOutputLines = 400;

    //算行级差异
    public static (string Text, int Added, int Removed) Compute(string? oldText, string? newText, int context = 3)
    {
        var oldLines = SplitLines(oldText);
        var newLines = SplitLines(newText);

        if (oldLines.Length > MaxDpLines || newLines.Length > MaxDpLines)
        {
            //太大就只给摘要
            return (
                $"文件过大，未做逐行对比。旧文件 {oldLines.Length} 行 / 新文件 {newLines.Length} 行，" +
                "已完整保存旧版本快照，可一键回退。",
                0, 0);
        }

        var n = oldLines.Length;
        var m = newLines.Length;
        var lcs = new int[n + 1, m + 1];

        for (var i = n - 1; i >= 0; i--)
        {
            for (var j = m - 1; j >= 0; j--)
            {
                lcs[i, j] = string.Equals(oldLines[i], newLines[j], StringComparison.Ordinal)
                    ? lcs[i + 1, j + 1] + 1
                    : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);
            }
        }

        //回溯操作序列
        var ops = new List<(char Kind, string Text)>();
        int x = 0, y = 0;
        while (x < n && y < m)
        {
            if (string.Equals(oldLines[x], newLines[y], StringComparison.Ordinal))
            {
                ops.Add((' ', oldLines[x]));
                x++; y++;
            }
            else if (lcs[x + 1, y] >= lcs[x, y + 1])
            {
                ops.Add(('-', oldLines[x]));
                x++;
            }
            else
            {
                ops.Add(('+', newLines[y]));
                y++;
            }
        }
        while (x < n) ops.Add(('-', oldLines[x++]));
        while (y < m) ops.Add(('+', newLines[y++]));

        //只留有变更的区块
        var keep = new bool[ops.Count];
        for (var i = 0; i < ops.Count; i++)
        {
            if (ops[i].Kind == ' ') continue;
            for (var k = Math.Max(0, i - context); k <= Math.Min(ops.Count - 1, i + context); k++)
                keep[k] = true;
        }

        var sb = new StringBuilder();
        var added = ops.Count(o => o.Kind == '+');
        var removed = ops.Count(o => o.Kind == '-');
        var emitted = 0;
        var lastKept = -2;

        for (var i = 0; i < ops.Count; i++)
        {
            if (!keep[i]) continue;

            if (i != lastKept + 1) sb.Append("@@\n");
            lastKept = i;

            sb.Append(ops[i].Kind).Append(ops[i].Text).Append('\n');

            if (++emitted >= MaxOutputLines)
            {
                sb.Append("... 变更过长，已截断展示（快照中保留完整内容）\n");
                break;
            }
        }

        if (sb.Length == 0)
            sb.Append("（内容无变化）\n");

        return (sb.ToString(), added, removed);
    }

    //按行拆分
    private static string[] SplitLines(string? text)
    {
        if (string.IsNullOrEmpty(text)) return Array.Empty<string>();
        return text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
    }
}
