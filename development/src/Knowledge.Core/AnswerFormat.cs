using System.Text.RegularExpressions;

namespace Knowledge.Core;
public static class AnswerFormat
{
    /// <summary>"…הבאים: 1. שער אורן 2. אבטחה 3. …" becomes an intro line and one line per point. Only a run that counts
    /// 1, 2, 3… within one line is split, so decimals and single numbers stay as they are; code blocks are untouched.</summary>
    public static string SplitInlineLists(string text)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        var inCode = false;
        for (int i = 0; i < lines.Length; i++)
        {
            if (lines[i].TrimStart().StartsWith("```")) { inCode = !inCode; continue; }
            if (inCode) continue;
            var markers = Regex.Matches(lines[i], @"(?<=^|\s)(\d{1,2})\.\s").Cast<Match>().ToList();
            var run = new List<Match>();
            foreach (var m in markers) if (int.Parse(m.Groups[1].Value) == run.Count + 1) run.Add(m);
            if (run.Count < 2) continue;
            var line = lines[i];
            for (int k = run.Count - 1; k >= 0; k--)
                if (run[k].Index > 0) line = line[..run[k].Index].TrimEnd() + "\n" + line[run[k].Index..];
            lines[i] = line;
        }
        return string.Join("\n", lines);
    }
}
