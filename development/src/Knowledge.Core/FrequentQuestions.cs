using System.Text.RegularExpressions;
namespace Knowledge.Core;

public static class FrequentQuestions
{
    public static List<string> Select(IEnumerable<Conversation> conversations, int limit = 3) => conversations
        .SelectMany(c => c.Messages).Where(m => m.Role == "user")
        .Select(m => new { Text = Regex.Replace(m.Text.Trim(), @"\s+", " "), m.Time })
        .Where(m => m.Text.Length is >= 10 and <= 160 && RequestPlanner.LocalPlan(m.Text, 40) == null)
        .GroupBy(m => Regex.Replace(m.Text.ToLowerInvariant(), @"[\p{P}\p{S}]", "").Trim())
        .Where(g => g.Count() >= 2)
        .OrderByDescending(g => g.Count()).ThenByDescending(g => g.Max(m => m.Time))
        .Take(limit).Select(g => g.OrderByDescending(m => m.Time).First().Text).ToList();
}
