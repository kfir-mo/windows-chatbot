using System.Text;
using System.Text.RegularExpressions;

namespace Knowledge.Core;

/// <summary>Immutable local inverted index. No embedding service or AI calls.</summary>
/// <remarks>Postings are sorted parallel arrays (passage id, weight) to keep memory low. A refresh reuses the previous
/// catalog's postings for unchanged passages (same object references) and tokenizes only new text.</remarks>
public sealed class PassageCatalog
{
    private readonly struct Posting(int[] ids, float[] weights)
    {
        public readonly int[] Ids = ids;
        public readonly float[] Weights = weights;
    }
    private sealed class PostingBuilder
    {
        public int[] Ids = new int[4];
        public float[] Weights = new float[4];
        public int Count;
        public void Add(int id, float weight)
        {
            if (Count == Ids.Length) { Array.Resize(ref Ids, Count * 2); Array.Resize(ref Weights, Count * 2); }
            Ids[Count] = id; Weights[Count++] = weight;
        }
        public Posting Build()
        {
            var ids = Ids[..Count]; var weights = Weights[..Count];
            Array.Sort(ids, weights);
            return new(ids, weights);
        }
    }
    private readonly Passage[] passages;
    private readonly int[] files; // passage id -> file ordinal, for document frequency
    private readonly Dictionary<string, Posting> postings;
    private readonly int[] lengths;
    private readonly bool[] listings; // passages that are mostly file names/paths (folder maps, indexes)
    private readonly double averageLength;
    private readonly int documentCount;
    public PassageCatalog(IEnumerable<Passage> source, PassageCatalog? previous = null)
    {
        passages = source.ToArray();
        files = new int[passages.Length];
        var fileIds = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < passages.Length; i++)
        {
            if (!fileIds.TryGetValue(passages[i].Path, out var f)) fileIds[passages[i].Path] = f = fileIds.Count;
            files[i] = f;
        }
        documentCount = fileIds.Count;
        lengths = new int[passages.Length];
        listings = new bool[passages.Length];
        var builders = new Dictionary<string, PostingBuilder>(StringComparer.Ordinal);
        PostingBuilder Builder(string term) { if (!builders.TryGetValue(term, out var b)) builders[term] = b = new(); return b; }
        var done = new bool[passages.Length];
        if (previous != null && previous.passages.Length > 0)
        {
            // Unchanged files keep their Passage objects, so reference identity maps old ids to new ids.
            var position = new Dictionary<Passage, int>(passages.Length, ReferenceEqualityComparer.Instance);
            for (int i = 0; i < passages.Length; i++) position.TryAdd(passages[i], i);
            var map = new int[previous.passages.Length];
            for (int j = 0; j < map.Length; j++)
            {
                map[j] = position.TryGetValue(previous.passages[j], out var i) ? i : -1;
                if (map[j] >= 0) { done[map[j]] = true; lengths[map[j]] = previous.lengths[j]; listings[map[j]] = previous.listings[j]; }
            }
            foreach (var (term, posting) in previous.postings)
            {
                PostingBuilder? builder = null;
                for (int k = 0; k < posting.Ids.Length; k++)
                    if (map[posting.Ids[k]] is var id and >= 0) (builder ??= Builder(term)).Add(id, posting.Weights[k]);
            }
        }
        // Tokenize new passages in parallel, in bounded batches so peak memory stays small.
        var pending = Enumerable.Range(0, passages.Length).Where(i => !done[i]).ToArray();
        const int batch = 2048;
        var parallel = new ParallelOptions { MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount / 2, 1, 4) };
        for (int start = 0; start < pending.Length; start += batch)
        {
            var slice = pending[start..Math.Min(pending.Length, start + batch)];
            var prepared = new KeyValuePair<string, float>[slice.Length][];
            Parallel.For(0, slice.Length, parallel, k =>
            {
                var p = passages[slice[k]];
                var body = SearchText.Tokens(p.Text).ToArray();
                lengths[slice[k]] = Math.Max(1, body.Length);
                listings[slice[k]] = RetrievalText.IsFileListName(p.Path) || RetrievalText.LooksLikeFileList(p.Text);
                var frequencies = new Dictionary<string, float>(StringComparer.Ordinal);
                void Add(IEnumerable<string> words, float weight) { foreach (var word in words) frequencies[word] = frequencies.GetValueOrDefault(word) + weight; }
                Add(body, 1);
                Add(SearchText.Tokens(Path.GetFileNameWithoutExtension(p.Path)), 3);
                Add(SearchText.Tokens(p.Location), 2);
                prepared[k] = frequencies.ToArray();
            });
            for (int k = 0; k < slice.Length; k++)
                foreach (var (term, weight) in prepared[k]) Builder(term).Add(slice[k], weight);
        }
        postings = new(builders.Count, StringComparer.Ordinal);
        foreach (var (term, builder) in builders) postings[term] = builder.Build();
        averageLength = lengths.Length == 0 ? 1 : lengths.Average();
    }

    public IEnumerable<Passage> Rank(string query, CancellationToken cancellation, string? withinPath = null, Func<Passage, bool>? allowed = null, bool requireCoverage = true)
    {
        var scores = new Dictionary<int, double>();
        var coverage = new Dictionary<int, int>();
        var terms = SearchText.Terms(query).Take(64).ToArray();
        var groups = Regex.Matches(query, @"[\p{L}\p{N}]{2,}").Select(m => SearchText.Terms(m.Value)).Where(g => g.Count > 0).ToArray();
        var fuzzyEligible = groups.Where(g => !g.Any(postings.ContainsKey)).SelectMany(g => g).ToHashSet();
        var coveredByDocument = new Dictionary<int, HashSet<string>>();
        foreach (var term in terms)
        {
            cancellation.ThrowIfCancellationRequested();
            if (!postings.TryGetValue(term, out var hits))
            {
                if (groups.Length < 2 || !groups.Any(g => g.Any(postings.ContainsKey)) || !fuzzyEligible.Contains(term) || term.Length < 5 || !term.All(char.IsLetter)) continue;
                var alternatives = postings.Keys.Where(word => Math.Abs(word.Length - term.Length) <= 1 && word[0] == term[0] && OneEditApart(term, word)).Take(3).ToArray();
                if (alternatives.Length != 1) continue; // Ambiguous corrections are not guessed.
                var alternative = postings[alternatives[0]];
                hits = new(alternative.Ids, alternative.Weights.Select(w => w * .55f).ToArray());
            }
            var documentFrequency = DistinctFiles(hits.Ids);
            var idf = Math.Log(1 + (documentCount - documentFrequency + .5) / (documentFrequency + .5));
            for (int k = 0; k < hits.Ids.Length; k++)
            {
                var id = hits.Ids[k]; double frequency = hits.Weights[k];
                if (withinPath != null && !passages[id].Path.Equals(withinPath, StringComparison.OrdinalIgnoreCase)) continue;
                var norm = frequency + 1.2 * (.25 + .75 * lengths[id] / averageLength);
                scores[id] = scores.GetValueOrDefault(id) + idf * frequency * 2.2 / norm;
                coverage[id] = coverage.GetValueOrDefault(id) + 1;
                if (!coveredByDocument.TryGetValue(files[id], out var covered)) coveredByDocument[files[id]] = covered = [];
                covered.Add(term);
            }
        }
        var ranked = scores.Where(x => !requireCoverage || groups.Length < 3 || groups.Count(g => g.Any(coveredByDocument[files[x.Key]].Contains)) >= Math.Ceiling(groups.Length * .34)).Select(x => (Id: x.Key, Score: x.Value * (1 + (double)coverage[x.Key] / Math.Max(1, terms.Length)) * (listings[x.Key] ? ListingWeight : 1)))
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
    /// <summary>A list of file names matches almost any question by name only; it must not outrank the documents it lists.</summary>
    private const double ListingWeight = .3;
    private int DistinctFiles(int[] ids)
    {
        // Ids are sorted and a file's passages are contiguous, so counting file changes counts distinct files.
        int count = 0, last = -1;
        foreach (var id in ids) if (files[id] != last) { last = files[id]; count++; }
        return count;
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

public static partial class RetrievalText
{
    public static bool HasStrongMatch(string question, IEnumerable<Passage> passages)
    {
        // Count question words (each with its Hebrew variants), not variants, so variants don't raise the bar.
        var groups = Regex.Matches(question, @"[\p{L}\p{N}]{2,}").Select(m => SearchText.Terms(m.Value)).Where(g => g.Count > 0).ToList();
        return groups.Count >= 2 && passages.Where(p => !LooksLikeFileList(p.Text)).Any(p =>
        {
            var words = SearchText.Terms(p.Text + " " + p.Location);
            return groups.Count(g => g.Any(words.Contains)) >= Math.Max(2, groups.Count * .6);
        });
    }
    /// <summary>True for text that is mostly file names or paths, such as a FOLDER_MAP.csv or a directory listing.</summary>
    public static bool LooksLikeFileList(string text)
    {
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length < 4) return false;
        var files = lines.Count(line => FileName().IsMatch(line));
        return files >= lines.Length * .5;
    }
    /// <summary>Spreadsheet/text files named like a folder map or file list (FOLDER_MAP.csv, file-list.xlsx, רשימת קבצים.txt).</summary>
    public static bool IsFileListName(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".csv" or ".txt" or ".xlsx" or ".md" && FileListName().IsMatch(Path.GetFileNameWithoutExtension(path));
    [GeneratedRegex(@"(folder|dir|directory|file|files|tree)[ _.-]?(map|list|tree|index)|^(map|tree|sitemap)$|רשימת[ _-]?קבצים|מפת[ _-]?(תיקיות|קבצים)", RegexOptions.IgnoreCase)] private static partial Regex FileListName();
    [GeneratedRegex(@"(\.(docx?|pptx?|xlsx?|pdf|txt|md|csv|html?|msg|zip|png|jpe?g)\b|[A-Za-z]:\\|\\\\[^\\]+\\)", RegexOptions.IgnoreCase)] private static partial Regex FileName();
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
