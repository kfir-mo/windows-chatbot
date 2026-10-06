using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using UglyToad.PdfPig;
namespace Knowledge.Core;
public record Passage(string Path, string Location, string Text);
public record IndexStatistics(int Files, int Extracted, int Reused, int Warnings, long Milliseconds);
public record IndexedFile(long Length, DateTime Modified, List<Passage> Passages);
public interface IDocumentSearch
{
    bool CanRead(string path) => Access.CanRead(path);
    bool HasStrongMatch(string query) => false;
    Task RefreshAsync(bool force, IProgress<string>? progress, CancellationToken cancellation); Task<List<Passage>> SearchAsync(string query, CancellationToken cancellation);
    Task<List<string>> ListAsync(CancellationToken cancellation) => Task.FromResult(new List<string>());
    Task<List<Passage>> ReadAsync(string path, CancellationToken cancellation) => Task.FromResult(new List<Passage>());
    Task<List<Passage>> ReadAsync(string path, string query, CancellationToken cancellation) => ReadAsync(path, cancellation);
}
public static partial class SearchText
{
    private static readonly HashSet<string> Stop = new(("של את על מה איך אני לי זה זו הוא היא יש אין עם או גם האם אפשר צריך רוצה בבקשה כמה מתי איפה היכן איזה אילו מדוע " +
        // Conversational filler: these words rarely appear in the documents and must not decide which passage matches.
        "שלי שלך שלו שלה שלנו שלכם שלהם אנחנו אתם הם הן לעשות עושים עושה צריכה צריכים רוצים רוצה יכול יכולה יכולים אפשרי משהו מישהו כדי כל רק עוד כבר אם כן לא תודה היי שלום " +
        "the and how what when where why which a an is are to for of in please can do does i me my you your we our it this that there should could would will get").Split(' ', StringSplitOptions.RemoveEmptyEntries));
    [GeneratedRegex(@"\bwi[ -]fi\b", RegexOptions.IgnoreCase)] private static partial Regex WiFi();
    [GeneratedRegex(@"\p{Mn}")] private static partial Regex Marks();
    [GeneratedRegex(@"[\p{L}\p{N}]{2,}")] private static partial Regex Word();
    public static IEnumerable<string> Tokens(string s)
    {
        s = WiFi().Replace(s, "wifi");
        // Decomposing is only needed when the text has accents or Hebrew points; skipping it saves most of the indexing time.
        if (!s.IsNormalized(NormalizationForm.FormD) || s.Any(c => CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark))
            s = Marks().Replace(s.Normalize(NormalizationForm.FormD), "");
        s = s.ToLowerInvariant();
        foreach (Match match in Word().Matches(s))
        {
            var word = match.Value;
            if (Stop.Contains(word)) continue;
            yield return word;
            var hebrew = word.All(c => c >= 'א' && c <= 'ת');
            if (!hebrew) continue;
            // Conservative Hebrew prefix variants; preserve the exact word as well.
            var stems = new List<string>(3) { word };
            if (word.Length >= 4 && "ובכלמהש".Contains(word[0]))
            {
                if (!Stop.Contains(word[1..])) stems.Add(word[1..]);
                if (word.Length >= 5 && word[0] == 'ו' && "בכלמהש".Contains(word[1])) stems.Add(word[2..]);
            }
            foreach (var stem in stems)
            {
                if (stem != word) yield return stem;
                foreach (var variant in HebrewSuffixVariants(stem)) yield return variant;
            }
        }
    }
    /// <summary>Singular forms for common Hebrew plural/construct endings, so "שומרי מסך" (screen savers) matches
    /// "שומר מסך" (screen saver) and "הוראות" matches "הוראה". Both the index and the query use the same variants.</summary>
    private static IEnumerable<string> HebrewSuffixVariants(string word)
    {
        // Construct nouns often replace a final ה with ת (e.g. שפת / שפה).
        if (word.Length >= 3 && word[^1] == 'ת') yield return word[..^1] + "ה";
        // Plural masculine (ים) and feminine (ות): שומרים → שומר, הוראות → הוראה.
        if (word.Length >= 5 && word.EndsWith("ים")) yield return word[..^2];
        if (word.Length >= 5 && word.EndsWith("ות")) { yield return word[..^2]; yield return word[..^2] + "ה"; }
        // Plural construct (י): שומרי מסך → שומר מסך, מחשבי החברה → מחשב.
        else if (word.Length >= 4 && word[^1] == 'י' && word[^2] != 'י') yield return word[..^1];
    }
    public static HashSet<string> Terms(string s) => Tokens(s).ToHashSet();
    public static double Score(string text, HashSet<string> terms)
    {
        var words = Terms(text);
        return terms.Sum(t => words.Contains(t) ? 1.0 : 0);
    }
}
public sealed class DocumentIndex : IDocumentSearch
{
    /// <summary>File types that are indexed. Keep in sync with <see cref="Access.SafeDocument"/>.</summary>
    public static readonly string[] Extensions = [".docx", ".pptx", ".pdf", ".xlsx", ".txt", ".md", ".csv", ".htm", ".html", ".json"];
    private readonly Config config;
    private readonly SemaphoreSlim refreshGate = new(1, 1);
    private sealed record Snapshot(Dictionary<string, IndexedFile> Files, PassageCatalog Catalog);
    // The cache stores each path once (not once per passage) to keep the file and the loaded index small.
    private sealed record CachedPassage(string L, string T);
    private sealed record CachedFile(long Length, DateTime Modified, List<CachedPassage> Passages);
    private Snapshot snapshot;
    private readonly string cacheName;
    public IndexStatistics Statistics { get; private set; } = new(0, 0, 0, 0, 0);
    public DocumentIndex(Config config)
    {
        this.config = config;
        var identity = string.Join("|", config.Documents.EffectiveRoots.Select(p => Path.GetFullPath(p).ToLowerInvariant()).Order()) + "|v7|" + config.Documents.ChunkCharacters + "|" + config.Documents.SearchSubfolders;
        cacheName = "index7-" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..16] + ".bin";
        RemoveLegacyCaches();
        var files = new Dictionary<string, IndexedFile>(StringComparer.OrdinalIgnoreCase);
        var locations = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (path, cached) in Storage.LoadCompressed<Dictionary<string, CachedFile>>(cacheName) ?? [])
            files[path] = new(cached.Length, cached.Modified, cached.Passages.Select(p => new Passage(path, locations.TryGetValue(p.L, out var l) ? l : locations[p.L] = p.L, p.T)).ToList());
        snapshot = new(files, new PassageCatalog(files.Values.SelectMany(f => f.Passages)));
    }
    private static void RemoveLegacyCaches()
    {
        // Version 6 caches (index-*.bin) are never read again.
        try { foreach (var old in Directory.EnumerateFiles(Storage.Root, "index-*.bin")) File.Delete(old); } catch { }
    }
    public bool HasStrongMatch(string query) => RetrievalText.HasStrongMatch(query, Volatile.Read(ref snapshot).Catalog.Rank(query, default));
    public async Task RefreshAsync(bool force, IProgress<string>? progress, CancellationToken cancellation)
    {
        await refreshGate.WaitAsync(cancellation);
        try
        {
            await Task.Run(() =>
            {
                var watch = System.Diagnostics.Stopwatch.StartNew();
                var before = Volatile.Read(ref snapshot);
                var warnings = new List<string>();
                void Warn(string text) { lock (warnings) warnings.Add(text); }
                int reused = 0;
                // Pass 1 (sequential, cheap): decide which files are new or changed.
                var paths = config.Documents.EffectiveRoots.SelectMany(root => Enumerate(root, Warn, cancellation)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                var results = new IndexedFile?[paths.Count];
                var work = new List<(int Slot, string Path, long Length, DateTime Modified)>();
                for (int i = 0; i < paths.Count; i++)
                {
                    cancellation.ThrowIfCancellationRequested();
                    var path = paths[i];
                    var ext = Path.GetExtension(path).ToLowerInvariant();
                    if (ext is ".doc" or ".ppt" or ".xls") { Warn($"פורמט ישן אינו נתמך (שמרו כ־{ext}x): {Path.GetFileName(path)}"); continue; }
                    if (!Extensions.Contains(ext)) continue;
                    try
                    {
                        var info = new FileInfo(path);
                        if (info.Length > config.Documents.MaxFileMegabytes * 1024L * 1024)
                        { Warn($"קובץ גדול מדי: {info.Name}"); continue; }
                        using (File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)) { }
                        if (!force && before.Files.TryGetValue(path, out var old) && old.Length == info.Length && old.Modified == info.LastWriteTimeUtc)
                        { results[i] = old; reused++; }
                        else work.Add((i, path, info.Length, info.LastWriteTimeUtc));
                    }
                    catch { Warn($"לא ניתן לקרוא: {Path.GetFileName(path)}"); }
                }
                // Pass 2: extract new/changed files in parallel (bounded, so memory stays low on weak PCs).
                int extracted = 0, finished = 0;
                if (work.Count > 0) progress?.Report($"מעדכן מאגר: 0/{work.Count} מסמכים");
                Parallel.ForEach(work, new ParallelOptions { CancellationToken = cancellation, MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount / 2, 1, 4) }, item =>
                {
                    try
                    {
                        var passages = Extract(item.Path, config.Documents.ChunkCharacters, cancellation);
                        var info = new FileInfo(item.Path);
                        if (info.Length != item.Length || info.LastWriteTimeUtc != item.Modified) Warn($"הקובץ השתנה בזמן הקריאה: {info.Name}");
                        else
                        {
                            if (passages.Count == 0) Warn(Path.GetExtension(item.Path).Equals(".pdf", StringComparison.OrdinalIgnoreCase) ? $"אין טקסט ב־PDF (ייתכן שהוא סרוק): {info.Name}" : $"אין טקסט ניתן לחילוץ: {info.Name}");
                            results[item.Slot] = new(item.Length, item.Modified, passages);
                            Interlocked.Increment(ref extracted);
                        }
                    }
                    catch (OperationCanceledException) { throw; }
                    catch { Warn($"לא ניתן לקרוא: {Path.GetFileName(item.Path)}"); }
                    var done = Interlocked.Increment(ref finished);
                    if (done % 5 == 0 || done == work.Count) progress?.Report($"מעדכן מאגר: {done}/{work.Count} מסמכים");
                });
                cancellation.ThrowIfCancellationRequested();
                var next = new Dictionary<string, IndexedFile>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < paths.Count; i++) if (results[i] != null) next[paths[i]] = results[i]!;
                if (extracted > 0 || next.Count != before.Files.Count || !next.Keys.All(before.Files.ContainsKey))
                {
                    var updated = new Snapshot(next, new PassageCatalog(next.Values.SelectMany(f => f.Passages), before.Catalog));
                    cancellation.ThrowIfCancellationRequested();
                    Storage.SaveCompressed(cacheName, next.ToDictionary(x => x.Key, x => new CachedFile(x.Value.Length, x.Value.Modified, x.Value.Passages.Select(p => new CachedPassage(p.Location, p.Text)).ToList())));
                    Volatile.Write(ref snapshot, updated);
                }
                Statistics = new(next.Count, extracted, reused, warnings.Count, watch.ElapsedMilliseconds);
                Warnings = warnings.ToList();
                Summary = "מוכן · " + (next.Count == 1 ? "מסמך אחד" : $"{next.Count} מסמכים") + (warnings.Count == 1 ? " · קובץ אחד לא נכלל" : warnings.Count > 1 ? $" · {warnings.Count} קבצים לא נכללו" : "");
                progress?.Report(Summary);
            }, cancellation);
        }
        finally { refreshGate.Release(); }
    }
    /// <summary>Problems from the last refresh (unreadable, unsupported or empty files), for display.</summary>
    public IReadOnlyList<string> Warnings { get; private set; } = [];
    /// <summary>The status line of the last refresh ("מוכן · 12 מסמכים"), or empty before the first refresh.</summary>
    public string Summary { get; private set; } = "";
    private IEnumerable<string> Enumerate(string dir, Action<string> warn, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        string[] entries;
        try { entries = Directory.GetFileSystemEntries(dir); }
        catch { warn("תיקייה אינה זמינה: " + dir); yield break; }
        foreach (var entry in entries.Order(StringComparer.OrdinalIgnoreCase))
        {
            cancellation.ThrowIfCancellationRequested();
            FileAttributes attr;
            try { attr = File.GetAttributes(entry); } catch { continue; }
            if ((attr & FileAttributes.ReparsePoint) != 0) continue;
            // Office/LibreOffice lock files (~$name.docx, .~lock.name#) and hidden/system files are never documents.
            var name = Path.GetFileName(entry);
            if ((attr & (FileAttributes.Hidden | FileAttributes.System)) != 0 || name.StartsWith("~$") || name.StartsWith(".~lock")) continue;
            if ((attr & FileAttributes.Directory) != 0)
            {
                if (config.Documents.SearchSubfolders)
                    foreach (var child in Enumerate(entry, warn, cancellation)) yield return child;
            }
            else yield return entry;
        }
    }
    public Task<List<Passage>> SearchAsync(string query, CancellationToken cancellation) => Task.Run(() =>
    {
        var current = Volatile.Read(ref snapshot);
        var access = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        var candidates = new List<Passage>();
        var perFile = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        bool Allowed(Passage p)
        {
            if (!access.TryGetValue(p.Path, out var readable)) access[p.Path] = readable = IsCurrent(current, p.Path);
            return readable;
        }
        // A conversational question can contain words no document uses; if the strict pass finds nothing,
        // a relaxed pass still offers the best passages. The answer step rejects evidence that does not support an answer.
        foreach (var strict in new[] { true, false })
        {
            foreach (var passage in current.Catalog.Rank(query, cancellation, allowed: Allowed, requireCoverage: strict))
            {
                cancellation.ThrowIfCancellationRequested();
                if (!access.TryGetValue(passage.Path, out var readable)) access[passage.Path] = readable = IsCurrent(current, passage.Path);
                if (!readable || perFile.GetValueOrDefault(passage.Path) >= 3) continue;
                candidates.Add(passage);
                perFile[passage.Path] = perFile.GetValueOrDefault(passage.Path) + 1;
                if (candidates.Count >= config.Documents.MaxPassages * 4) break;
            }
            if (candidates.Count > 0) break;
        }
        return RetrievalText.Budget(candidates, query, config.Documents);
    }, cancellation);

    public Task<List<string>> ListAsync(CancellationToken cancellation) => Task.Run(() =>
    {
        var current = Volatile.Read(ref snapshot);
        return current.Files.Keys.Where(path => { cancellation.ThrowIfCancellationRequested(); return IsCurrent(current, path); })
            .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase).ToList();
    }, cancellation);

    public Task<List<Passage>> ReadAsync(string path, CancellationToken cancellation) => ReadAsync(path, "", cancellation);
    public Task<List<Passage>> ReadAsync(string path, string query, CancellationToken cancellation) => Task.Run(() =>
    {
        cancellation.ThrowIfCancellationRequested();
        var current = Volatile.Read(ref snapshot);
        if (!IsCurrent(current, path)) return new List<Passage>();
        var all = current.Files[path].Passages;
        if (!string.IsNullOrWhiteSpace(query))
        {
            var matches = current.Catalog.Rank(query, cancellation, withinPath: path).Take(config.Documents.MaxPassages * 4).ToList();
            return RetrievalText.Budget(matches, query, config.Documents);
        }
        // Cover the whole document rather than silently using only its first pages.
        var count = Math.Min(all.Count, Math.Min(config.Documents.MaxPassages, Math.Max(1, config.Documents.EvidenceCharacters / config.Documents.ExcerptCharacters)));
        var samples = Enumerable.Range(0, count).Select(i => all[count == 1 ? 0 : (int)Math.Round((double)i * (all.Count - 1) / (count - 1))]);
        return RetrievalText.Budget(samples, "", config.Documents, overview: true);
    }, cancellation);
    private static bool IsCurrent(Snapshot current, string path)
    {
        try { var info = new FileInfo(path); return current.Files.TryGetValue(path, out var f) && Access.CanRead(path) && info.LastWriteTimeUtc == f.Modified && info.Length == f.Length; }
        catch { return false; }
    }
    private static readonly XmlReaderSettings SafeXml = new() { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 64 * 1024 * 1024 };
    private static XDocument LoadXml(ZipArchive zip, string name)
    {
        using var stream = (zip.GetEntry(name) ?? throw new InvalidDataException(name)).Open();
        using var reader = XmlReader.Create(stream, SafeXml);
        return XDocument.Load(reader);
    }
    private static Dictionary<string, (string Target, string Type)> Relationships(ZipArchive zip, string name) =>
        zip.GetEntry(name) == null ? [] : LoadXml(zip, name).Descendants().Where(x => x.Name.LocalName == "Relationship" && (string?)x.Attribute("TargetMode") != "External")
            .ToDictionary(x => (string)x.Attribute("Id")!, x => ((string)x.Attribute("Target")!, (string?)x.Attribute("Type") ?? ""));
    private static string Resolve(string part, string target) => Uri.UnescapeDataString(new Uri(new Uri("https://package.invalid/" + part), target).AbsolutePath.TrimStart('/'));
    /// <summary>Text of one paragraph, without the text of nested paragraphs (text boxes) or legacy fallback copies.</summary>
    private static string ParagraphText(XElement paragraph)
    {
        var text = new StringBuilder();
        void Walk(XElement element)
        {
            foreach (var child in element.Elements())
            {
                var name = child.Name.LocalName;
                if (name is "p" or "Fallback" or "instrText" or "delText") continue;
                if (name == "t") text.Append(child.Value);
                else if (name == "tab") text.Append(' ');
                else if (name is "br" or "cr") text.Append('\n');
                else Walk(child);
            }
        }
        Walk(paragraph);
        return text.ToString();
    }
    public static List<Passage> Extract(string path, int chunk, CancellationToken cancellation)
    {
        var result = new List<Passage>();
        void Add(string location, string text)
        {
            text = Regex.Replace(text, @"[\t ]+", " ").Trim();
            foreach (var part in TextChunks.Split(text, chunk))
            {
                cancellation.ThrowIfCancellationRequested();
                result.Add(new(path, location, part));
            }
        }
        var extension = Path.GetExtension(path).ToLowerInvariant();
        if (extension is ".txt" or ".md" or ".csv")
        {
            Add("מסמך", PlainText.Read(path));
            return result;
        }
        if (extension is ".htm" or ".html")
        {
            Add("מסמך", PlainText.FromHtml(PlainText.Read(path)));
            return result;
        }
        if (extension == ".json")
        {
            Add("מסמך", PlainText.FromJson(PlainText.Read(path)));
            return result;
        }
        if (extension == ".xlsx")
        {
            foreach (var section in SpreadsheetText.Read(path, chunk, cancellation)) Add(section.Location, section.Text);
            return result;
        }
        if (extension == ".pdf")
        {
            using var pdf = PdfDocument.Open(path);
            foreach (var page in pdf.GetPages())
            {
                cancellation.ThrowIfCancellationRequested();
                Add($"עמוד {page.Number}", PdfTextOrder.Normalize(UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor.ContentOrderTextExtractor.GetText(page)));
            }
            return result;
        }
        using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
        if (extension == ".pptx")
        {
            // Presentation order can differ from slide filenames after slides are moved.
            var presentation = LoadXml(zip, "ppt/presentation.xml");
            var relationships = Relationships(zip, "ppt/_rels/presentation.xml.rels");
            int slideNumber = 0;
            foreach (var slide in presentation.Descendants().Where(x => x.Name.LocalName == "sldId"))
            {
                cancellation.ThrowIfCancellationRequested();
                slideNumber++;
                var id = slide.Attributes().First(x => x.Name.LocalName == "id" && x.Name.NamespaceName.Length > 0).Value;
                var part = Resolve("ppt/presentation.xml", relationships[id].Target);
                var xml = LoadXml(zip, part);
                string Placeholder(XElement shape) => (string?)shape.Descendants().FirstOrDefault(x => x.Name.LocalName == "ph")?.Attribute("type") ?? "";
                var shapes = xml.Descendants().Where(x => x.Name.LocalName == "sp").ToList();
                var title = shapes.Where(s => Placeholder(s) is "title" or "ctrTitle").Select(s => string.Join(" ", s.Descendants().Where(x => x.Name.LocalName == "p").Select(ParagraphText))).FirstOrDefault(t => !string.IsNullOrWhiteSpace(t))?.Trim();
                var text = string.Join("\n", xml.Descendants().Where(x => x.Name.LocalName == "p").Select(ParagraphText));
                // Speaker notes often hold the actual explanation of a slide.
                var notes = Relationships(zip, Path.GetDirectoryName(part)!.Replace('\\', '/') + "/_rels/" + Path.GetFileName(part) + ".rels").Values.FirstOrDefault(r => r.Type.EndsWith("/notesSlide"));
                if (notes.Target != null && zip.GetEntry(Resolve(part, notes.Target)) != null)
                {
                    var body = LoadXml(zip, Resolve(part, notes.Target)).Descendants().Where(x => x.Name.LocalName == "sp" && Placeholder(x) == "body")
                        .SelectMany(s => s.Descendants().Where(x => x.Name.LocalName == "p")).Select(ParagraphText).Where(t => !string.IsNullOrWhiteSpace(t)).ToList();
                    if (body.Count > 0) text += "\nהערות דובר:\n" + string.Join("\n", body);
                }
                var location = "שקופית " + slideNumber + (string.IsNullOrWhiteSpace(title) ? "" : " · " + (title.Length > 60 ? title[..60] + "…" : title));
                Add(location, text);
            }
        }
        else
        {
            var xml = LoadXml(zip, "word/document.xml");
            // Heading style IDs are localized (a Hebrew Word names "Heading 1" style "1"), so resolve them through styles.xml.
            var headingStyles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (zip.GetEntry("word/styles.xml") != null)
                foreach (var style in LoadXml(zip, "word/styles.xml").Descendants().Where(x => x.Name.LocalName == "style"))
                {
                    var styleName = style.Elements().FirstOrDefault(x => x.Name.LocalName == "name")?.Attributes().FirstOrDefault(a => a.Name.LocalName == "val")?.Value ?? "";
                    var styleId = style.Attributes().FirstOrDefault(a => a.Name.LocalName == "styleId")?.Value;
                    if (styleId != null && (styleName.StartsWith("heading", StringComparison.OrdinalIgnoreCase) || styleName.Equals("Title", StringComparison.OrdinalIgnoreCase) || style.Descendants().Any(x => x.Name.LocalName == "outlineLvl")))
                        headingStyles.Add(styleId);
                }
            string heading = "מסמך";
            var buffer = new StringBuilder();
            foreach (var paragraph in xml.Descendants().Where(x => x.Name.LocalName == "p" && !x.Ancestors().Any(a => a.Name.LocalName == "Fallback")))
            {
                var text = ParagraphText(paragraph);
                var properties = paragraph.Elements().FirstOrDefault(x => x.Name.LocalName == "pPr");
                var style = properties?.Elements().FirstOrDefault(x => x.Name.LocalName == "pStyle")?.Attributes().FirstOrDefault(x => x.Name.LocalName == "val")?.Value;
                var isHeading = style != null && (headingStyles.Contains(style) || style.Contains("Heading", StringComparison.OrdinalIgnoreCase))
                    || properties?.Elements().Any(x => x.Name.LocalName == "outlineLvl") == true;
                if (isHeading && !string.IsNullOrWhiteSpace(text))
                {
                    Add(heading, buffer.ToString());
                    buffer.Clear();
                    heading = text.Trim().Length > 80 ? text.Trim()[..80] + "…" : text.Trim();
                }
                buffer.AppendLine(text);
            }
            Add(heading, buffer.ToString());
        }
        return result;
    }
}
/// <summary>Plain text and HTML reading with encoding detection (UTF-8/UTF-16 with or without BOM, else Windows-1255 Hebrew).</summary>
public static partial class PlainText
{
    static PlainText() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    public static string Read(string path)
    {
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length >= 2 && (bytes[0] == 0xFF && bytes[1] == 0xFE || bytes[0] == 0xFE && bytes[1] == 0xFF) || bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            using var reader = new StreamReader(new MemoryStream(bytes), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            return reader.ReadToEnd();
        }
        try { return new UTF8Encoding(false, true).GetString(bytes); }
        catch (DecoderFallbackException) { return Encoding.GetEncoding(1255).GetString(bytes); }
    }
    [GeneratedRegex(@"<(script|style|head|noscript)\b.*?</\1\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline)] private static partial Regex Hidden();
    [GeneratedRegex(@"<(br|/p|/div|/li|/tr|/h[1-6]|/td|/th)\b[^>]*>", RegexOptions.IgnoreCase)] private static partial Regex Breaks();
    [GeneratedRegex(@"<[^>]+>")] private static partial Regex Tags();
    [GeneratedRegex(@"\n\s*\n\s*(\n\s*)+")] private static partial Regex BlankLines();
    /// <summary>JSON as readable lines ("key: value"), without braces and quotes; invalid JSON is indexed as plain text.</summary>
    public static string FromJson(string json)
    {
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(json, new() { AllowTrailingCommas = true, CommentHandling = System.Text.Json.JsonCommentHandling.Skip, MaxDepth = 64 });
            var text = new StringBuilder();
            void Walk(System.Text.Json.JsonElement element, string? name)
            {
                switch (element.ValueKind)
                {
                    case System.Text.Json.JsonValueKind.Object:
                        foreach (var property in element.EnumerateObject()) Walk(property.Value, property.Name);
                        break;
                    case System.Text.Json.JsonValueKind.Array:
                        // A list of plain values reads as one line (e.g. a menu path: "יומן > תצוגה > שמור").
                        if (element.EnumerateArray().All(x => x.ValueKind is System.Text.Json.JsonValueKind.String or System.Text.Json.JsonValueKind.Number) && element.GetArrayLength() > 1)
                            text.AppendLine((name == null ? "" : name + ": ") + string.Join(" > ", element.EnumerateArray().Select(x => x.ToString())));
                        else foreach (var item in element.EnumerateArray()) Walk(item, name);
                        break;
                    case System.Text.Json.JsonValueKind.Null: break;
                    default:
                        text.AppendLine((name == null ? "" : name + ": ") + element.ToString());
                        break;
                }
            }
            Walk(document.RootElement, null);
            return text.ToString();
        }
        catch (System.Text.Json.JsonException) { return json; }
    }
    public static string FromHtml(string html)
    {
        var text = Hidden().Replace(html, " ");
        text = Breaks().Replace(text, "\n");
        text = Tags().Replace(text, " ");
        return BlankLines().Replace(System.Net.WebUtility.HtmlDecode(text), "\n\n");
    }
}
