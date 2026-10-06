namespace Knowledge.Core;

public static class ConversationContext
{
    public static string Build(string question, Conversation conversation, List<Source> sources, HistoryOptions options)
    {
        var sourceNames = new List<object>();
        for (int i = 0; i < sources.Count; i++)
        {
            var name = Path.GetFileName(sources[i].Path);
            var item = new { sourceIndex = i + 1, name = name[..Math.Min(40, name.Length)] };
            if (AiJson.Serialize(sourceNames.Append(item)).Length + 18 > options.ContextCharacters / 2) break;
            sourceNames.Add(item);
        }
        var sourceText = sources.Count == 0 ? "" : "availableSources: " + AiJson.Serialize(sourceNames);
        var remaining = options.ContextCharacters - sourceText.Length - 1;
        var lines = new List<string>();
        var messages = conversation.Messages.AsEnumerable();
        if (conversation.Messages.LastOrDefault() is { Role: "user" } last && last.Text == question)
            messages = messages.Take(conversation.Messages.Count - 1);
        foreach (var message in messages.TakeLast(options.RecentMessages).Reverse())
        {
            var text = message.Text[..Math.Min(message.Text.Length, Math.Min(800, Math.Max(0, remaining - 16)))];
            if (text.Length == 0) break;
            var line = message.Role + ": " + text;
            if (line.Length + 1 > remaining) break;
            lines.Add(line); remaining -= line.Length + 1;
        }
        lines.Reverse();
        return string.Join("\n", lines) + "\n" + sourceText;
    }
}
