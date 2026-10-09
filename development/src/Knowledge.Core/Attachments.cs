using System.Text;

namespace Knowledge.Core;

/// <summary>A document the user attached in general chat. Its text stays with the conversation (DPAPI-protected like the rest of
/// it), so follow-up questions about the same document still have it.</summary>
public record Attachment(string Name, string Text);

public static class Attachments
{
    public static Attachment Read(string path, int maxMegabytes, CancellationToken cancellation)
    {
        if (!Access.SafeDocument(path)) throw new InvalidOperationException("סוג הקובץ לא נתמך. אפשר לצרף מסמך Word, מצגת PowerPoint, קובץ PDF, גיליון Excel או קובץ טקסט (גם CSV, HTML ו־JSON).");
        if (new FileInfo(path).Length > maxMegabytes * 1024L * 1024) throw new InvalidOperationException($"הקובץ גדול מדי (עד {maxMegabytes} MB).");
        var text = new StringBuilder();
        string? location = null;
        foreach (var passage in DocumentIndex.Extract(path, 4000, cancellation))
        {
            if (passage.Location != location && passage.Location != "מסמך") text.Append(text.Length > 0 ? "\n\n" : "").Append('[').Append(passage.Location).Append("]\n");
            else if (text.Length > 0) text.Append('\n');
            location = passage.Location;
            text.Append(passage.Text);
        }
        if (string.IsNullOrWhiteSpace(text.ToString())) throw new InvalidOperationException("לא נמצא טקסט בקובץ. ייתכן שזה PDF סרוק (תמונה).");
        return new(Path.GetFileName(path), text.ToString());
    }
    /// <summary>How many characters of attached text one request may carry: what is left of the model's input window after
    /// room for the answer and the instructions, at a conservative 2 characters per token (Hebrew tokenizes densely).</summary>
    public static int Budget(AiOptions ai) => ai.MaxInputTokens > 0 ? (int)Math.Clamp((ai.MaxInputTokens - (long)ai.MaxOutputTokens - 3000) * 2, 4000, 2_000_000) : 24000;
    /// <summary>The conversation's attachments for the prompt, newest first, cut to the budget.</summary>
    public static List<object> ForPrompt(Conversation conversation, int budget)
    {
        var result = new List<object>();
        foreach (var file in conversation.Messages.Select(m => m.File).OfType<Attachment>().Reverse())
        {
            if (budget <= 0) break;
            var text = file.Text.Length > budget ? file.Text[..budget] : file.Text;
            result.Add(new { name = file.Name, text, truncated = text.Length < file.Text.Length });
            budget -= text.Length;
        }
        return result;
    }
}
