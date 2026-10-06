namespace Knowledge.Core;
public static class TextChunks
{
    public static IEnumerable<string> Split(string text, int size)
    {
        if (size < 200) throw new ArgumentOutOfRangeException(nameof(size));
        int start = 0;
        while (start < text.Length)
        {
            int end = Math.Min(text.Length, start + size);
            if (end < text.Length)
            {
                var boundary = text.LastIndexOfAny(['\n', '.', '?', '!'], end - 1, end - start);
                if (boundary >= start + size / 2) end = boundary + 1;
                else
                {
                    boundary = text.LastIndexOf(' ', end - 1, end - start);
                    if (boundary > start) end = boundary;
                }
            }
            var part = text[start..end].Trim();
            if (part.Length > 0) yield return part;
            if (end == text.Length) yield break;
            var next = Math.Max(start + 1, end - Math.Min(150, size / 4));
            while (next < end && next > 0 && !char.IsWhiteSpace(text[next - 1])) next++;
            start = next;
        }
    }
}
