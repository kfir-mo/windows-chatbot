using System.Text;
using System.Text.RegularExpressions;

namespace Knowledge.Core;

/// <summary>Immutable local inverted index. No embedding service or AI calls.</summary>
public sealed class PassageCatalog
{
    private readonly Passage[] passages;
    private readonly Dictionary<string, Dictionary<int, double>> postings = new(StringComparer.Ordinal);
    private readonly int[] lengths;
    private readonly double averageLength;
    private readonly int documentCount;
    private sealed record Prepared(Dictionary<string, double> Frequencies, int Length);
    private readonly Dictionary<Passage, Prepared> prepared = [];
    public PassageCatalog(IEnumerable<Passage> source, PassageCatalog? previous = null)
    {
        passages = source.ToArray();
        documentCount = passages.Select(p => p.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        lengths = new int[passages.Length];
        for (int i = 0; i < passages.Length; i++)
        {
            var p = passages[i];
            if (previous != null && previous.prepared.TryGetValue(p, out var cached))
            {
                prepared[p] = cached;
                lengths[i] = cached.Length;
                foreach (var (word, frequency) in cached.Frequencies)
                {
                    if (!postings.TryGetValue(word, out var hits)) postings[word] = hits = [];
                    hits[i] = frequency;
                }
                continue;
            }
            var body = SearchText.Tokens(p.Text).ToArray();
            lengths[i] = Math.Max(1, body.Length);
            var frequencies = new Dictionary<string, double>();
            void Add(IEnumerable<string> words, double weight)
            {
                foreach (var group in words.GroupBy(x => x))
                {
                    if (!postings.TryGetValue(group.Key, out var hits)) postings[group.Key] = hits = [];
                    hits[i] = hits.GetValueOrDefault(i) + group.Count() * weight;
                    frequencies[group.Key] = frequencies.GetValueOrDefault(group.Key) + group.Count() * weight;
                }
            }
            Add(body, 1);
            Add(SearchText.Tokens(Path.GetFileNameWithoutExtension(p.Path)), 3);
            Add(SearchText.Tokens(p.Location), 2);
            prepared[p] = new(frequencies, lengths[i]);
        }
        averageLength = lengths.Length == 0 ? 1 : lengths.Average();
    }

    public IEnumerable<Passage> Rank(string query, CancellationToken cancellation, string? withinPath = null, Func<Passage, bool>? allowed = null)
    {
        var scores = new Dictionary<int, double>();
        var coverage = new Dictionary<int, int>();
        var terms = SearchText.Terms(query).Take(64).ToArray();
        var groups = Regex.Matches(query, @"[\p{L}\p{N}]{2,}").Select(m => SearchText.Terms(m.Value)).Where(g => g.Count > 0).ToArray();
        var fuzzyEligible = groups.Where(g => !g.Any(postings.ContainsKey)).SelectMany(g => g).ToHashSet();
        var coveredByDocument = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var term in terms)
        {
            cancellation.ThrowIfCancellationRequested();
            var exactTerm = postings.TryGetValue(term, out var hits);
            if (!exactTerm)
            {
                if (groups.Length < 2 || !groups.Any(g => g.Any(postings.ContainsKey)) || !fuzzyEligible.Contains(term) || term.Length < 5 || !term.All(char.IsLetter)) continue;
                var alternatives = postings.Keys.Where(word => Math.Abs(word.Length - term.Length) <= 1 && word[0] == term[0] && OneEditApart(term, word)).Take(3).ToArray();
                if (alternatives.Length != 1) continue; // Ambiguous corrections are not guessed.
                hits = new Dictionary<int, double>();
                foreach (var alternative in alternatives)
                    foreach (var hit in postings[alternative]) hits[hit.Key] = Math.Max(hits.GetValueOrDefault(hit.Key), hit.Value * .55);
            }
            var documentFrequency = hits!.Keys.Select(id => passages[id].Path).Distinct(StringComparer.OrdinalIgnoreCase).Count();
            var idf = Math.Log(1 + (documentCount - documentFrequency + .5) / (documentFrequency + .5));
            foreach (var (id, frequency) in hits)
            {
                if (withinPath != null && !passages[id].Path.Equals(withinPath, StringComparison.OrdinalIgnoreCase)) continue;
                var norm = frequency + 1.2 * (.25 + .75 * lengths[id] / averageLength);
                scores[id] = scores.GetValueOrDefault(id) + idf * frequency * 2.2 / norm;
                coverage[id] = coverage.GetValueOrDefault(id) + 1;
                if (!coveredByDocument.TryGetValue(passages[id].Path, out var covered)) coveredByDocument[passages[id].Path] = covered = [];
                covered.Add(term);
            }
        }
        var ranked = scores.Where(x => groups.Length < 3 || groups.Count(g => g.Any(coveredByDocument[passages[x.Key].Path].Contains)) >= Math.Ceiling(groups.Length * .34)).Select(x => (Id: x.Key, Score: x.Value * (1 + (double)coverage[x.Key] / Math.Max(1, terms.Length))))
            .OrderByDescending(x => x.Score).ThenBy(x => x.Id).ToArray();
        var exact = Regex.Matches(query, "\"([^\"]+)\"").Select(m => NormalizeLiteral(m.Groups[1].Value))
            .Concat(Regex.Matches(query, @"(?<![\p{L}\p{N}])[A-Za-z][A-Za-z0-9]*(?:[-_][A-Za-z0-9]+)+(?![\p{L}\p{N}])")
                .Select(m => m.Value).Where(v => v.Any(char.IsDigit)).Select(NormalizeLiteral)).Distinct().ToArray();
        double? threshold = null;
        foreach (var hit in ranked)
        {
            cancellation.ThrowIfCancellationRequested();
            if (threshold.HasValue && hit.Score < threshold.Value) yield break;
            var passage = passages[hit.Id];
            if (exact.Length > 0)
            {
                var literal = NormalizeLiteral(passage.Text + " " + passage.Location + " " + Path.GetFileNameWithoutExtension(passage.Path));
                if (exact.Any(e => !Regex.IsMatch(literal, @"(?<![\p{L}\p{N}])" + Regex.Escape(e) + @"(?![\p{L}\p{N}])"))) continue;
            }
            if (allowed != null && !allowed(passage)) continue;
            threshold ??= hit.Score * .45;
            yield return passage;
        }
    }
    private static bool OneEditApart(string a, string b)
    {
        if (a == b) return false;
        int i = 0; while (i < Math.Min(a.Length, b.Length) && a[i] == b[i]) i++;
        if (i == Math.Min(a.Length, b.Length)) return Math.Abs(a.Length - b.Length) == 1;
        if (a.Length == b.Length)
            return a.AsSpan(i + 1).SequenceEqual(b.AsSpan(i + 1)) ||
                (i + 1 < a.Length && a[i] == b[i + 1] && a[i + 1] == b[i] && a.AsSpan(i + 2).SequenceEqual(b.AsSpan(i + 2)));
        return a.Length > b.Length ? a.AsSpan(i + 1).SequenceEqual(b.AsSpan(i)) : a.AsSpan(i).SequenceEqual(b.AsSpan(i + 1));
    }
    private static string NormalizeLiteral(string text) => Regex.Replace(text.Normalize(NormalizationForm.FormKC).ToLowerInvariant(), @"\s+", " ").Trim();
}

public static class RetrievalText
{
    public static bool HasStrongMatch(string question, IEnumerable<Passage> passages)
    {
        var terms = SearchText.Terms(question);
        return terms.Count >= 2 && passages.Any(p => SearchText.Score(p.Text + " " + p.Location, terms) >= Math.Max(2, terms.Count * .6));
    }
    // Return a contiguous, verbatim excerpt around the best matching sentence/window.
    public static string Excerpt(string text, string query, int limit)
    {
        if (text.Length <= limit) return text;
        var terms = SearchText.Terms(query);
        var bestStart = 0;
        var bestScore = -1.0;
        for (int start = 0; start < text.Length; start += Math.Max(100, limit / 3))
        {
            var length = Math.Min(limit, text.Length - start);
            var score = SearchText.Score(text.Substring(start, length), terms);
            if (score > bestScore) { bestStart = start; bestScore = score; }
        }
        // Do not cut the first word or the last word in half.
        while (bestStart > 0 && bestStart < text.Length && !char.IsWhiteSpace(text[bestStart - 1])) bestStart++;
        var end = Math.Min(text.Length, bestStart + limit);
        while (end < text.Length && end > bestStart && !char.IsWhiteSpace(text[end])) end--;
        return text[bestStart..end].Trim();
    }

    public static List<Passage> Budget(IEnumerable<Passage> passages, string query, DocumentOptions options, bool overview = false)
    {
        var selected = new List<Passage>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var remaining = options.EvidenceCharacters;
        foreach (var p in passages)
        {
            if (remaining < 150 || selected.Count >= options.MaxPassages) break;
            var text = Excerpt(p.Text, overview ? "" : query, Math.Min(options.ExcerptCharacters, remaining));
            if (string.IsNullOrWhiteSpace(text) || !seen.Add(Regex.Replace(text, @"\s+", " "))) continue;
            if (selected.Any(s => s.Path.Equals(p.Path, StringComparison.OrdinalIgnoreCase) && Overlaps(s.Text, text))) continue;
            selected.Add(p with { Text = text });
            remaining -= text.Length;
        }
        return selected;
    }
    private static bool Overlaps(string a, string b)
    {
        // Similar wording with different limits/dates is distinct evidence, never a duplicate.
        return a.Contains(b, StringComparison.Ordinal) || b.Contains(a, StringComparison.Ordinal);
    }
}
