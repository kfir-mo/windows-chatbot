using System.IO.Compression;
using System.Text.RegularExpressions;
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
public static class SearchText
{
    private static readonly HashSet<string> Stop = new("של את על מה איך אני לי זה זו הוא היא יש אין עם או גם האם אפשר צריך רוצה בבקשה כמה מתי איפה היכן איזה אילו מדוע the and how what when where why which a an is are to for of in please can do does i me my".Split(' '));
    public static IEnumerable<string> Tokens(string s)
    {
        s = Regex.Replace(s, @"\bwi[ -]fi\b", "wifi", RegexOptions.IgnoreCase);
        s = Regex.Replace(s.Normalize(System.Text.NormalizationForm.FormD), @"\p{Mn}", "").ToLowerInvariant();
        foreach (Match match in Regex.Matches(s, @"[\p{L}\p{N}]{2,}"))
        {
            var word = match.Value;
            if (Stop.Contains(word)) continue;
            yield return word;
            // Hebrew construct nouns often replace a final ה with ת (e.g. שפת / שפה).
            if (word.Length >= 3 && word.All(c => c >= 'א' && c <= 'ת') && word.EndsWith('ת'))
                yield return word[..^1] + "ה";
            // Conservative Hebrew prefix variants; preserve the exact word as well.
            if (word.Length >= 4 && word.All(c => c >= 'א' && c <= 'ת') && "ובכלמהש".Contains(word[0]))
            {
                if (!Stop.Contains(word[1..])) yield return word[1..];
                if (word.Length >= 5 && word[0] == 'ו' && "בכלמהש".Contains(word[1])) yield return word[2..];
            }
        }
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
    private readonly Config config;
    private readonly SemaphoreSlim refreshGate = new(1, 1);
    private sealed record Snapshot(Dictionary<string, IndexedFile> Files, PassageCatalog Catalog);
    private Snapshot snapshot;
    private readonly string cacheName;
    public IndexStatistics Statistics { get; private set; } = new(0, 0, 0, 0, 0);
    public DocumentIndex(Config config)
    {
        this.config = config;
        var identity = string.Join("|", config.Documents.EffectiveRoots.Select(p => Path.GetFullPath(p).ToLowerInvariant()).Order()) + "|v6|" + config.Documents.ChunkCharacters + "|" + config.Documents.SearchSubfolders;
        cacheName = "index-" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(identity)))[..16] + ".bin";
        var files = new Dictionary<string, IndexedFile>(Storage.Load<Dictionary<string, IndexedFile>>(cacheName) ?? [], StringComparer.OrdinalIgnoreCase);
        snapshot = new(files, new PassageCatalog(files.Values.SelectMany(f => f.Passages)));
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
                var next = new Dictionary<string, IndexedFile>(StringComparer.OrdinalIgnoreCase);
                var warnings = new List<string>();
                int extracted = 0, reused = 0;
                foreach (var path in config.Documents.EffectiveRoots.SelectMany(root => Enumerate(root, warnings, cancellation)).Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    cancellation.ThrowIfCancellationRequested();
                    var ext = Path.GetExtension(path).ToLowerInvariant();
                    if (ext is ".doc" or ".ppt") { warnings.Add($"פורמט ישן אינו נתמך: {Path.GetFileName(path)}"); continue; }
                    if (ext is not (".docx" or ".pptx" or ".pdf" or ".xlsx" or ".txt" or ".md")) continue;
                    try
                    {
                        var info = new FileInfo(path);
                        if (info.Length > config.Documents.MaxFileMegabytes * 1024L * 1024)
                        { warnings.Add($"קובץ גדול מדי: {info.Name}"); continue; }
                        using var readable = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                        if (!force && before.Files.TryGetValue(path, out var old) && old.Length == info.Length && old.Modified == info.LastWriteTimeUtc)
                        { next[path] = old; reused++; }
                        else
                        {
                            var length = info.Length; var modified = info.LastWriteTimeUtc;
                            var passages = Extract(path, config.Documents.ChunkCharacters, cancellation);
                            info.Refresh();
                            if (info.Length != length || info.LastWriteTimeUtc != modified)
                            { warnings.Add($"הקובץ השתנה בזמן הקריאה: {info.Name}"); continue; }
                            if (passages.Count == 0) warnings.Add($"אין טקסט ניתן לחילוץ: {info.Name}");
                            next[path] = new(length, modified, passages); extracted++;
                        }
                        if (next.Count % 10 == 0) progress?.Report($"מעדכן מאגר: {next.Count} מסמכים");
                    }
                    catch (OperationCanceledException) { throw; }
                    catch { warnings.Add($"לא ניתן לקרוא: {Path.GetFileName(path)}"); }
                }
                cancellation.ThrowIfCancellationRequested();
                if (extracted > 0 || next.Count != before.Files.Count || !next.Keys.All(before.Files.ContainsKey))
                {
                    var updated = new Snapshot(next, new PassageCatalog(next.Values.SelectMany(f => f.Passages), before.Catalog));
                    cancellation.ThrowIfCancellationRequested();
                    Storage.Save(cacheName, next);
                    Volatile.Write(ref snapshot, updated);
                }
                Statistics = new(next.Count, extracted, reused, warnings.Count, watch.ElapsedMilliseconds);
                progress?.Report($"המאגר מוכן: {next.Count} מסמכים · {extracted} עודכנו" + (warnings.Count > 0 ? " · " + string.Join("; ", warnings.Take(5)) : ""));
            }, cancellation);
        }
        finally { refreshGate.Release(); }
    }
    private IEnumerable<string> Enumerate(string dir, List<string> warnings, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        string[] entries;
        try { entries = Directory.GetFileSystemEntries(dir); }
        catch { warnings.Add("תיקייה אינה זמינה: " + dir); yield break; }
        foreach (var entry in entries.Order(StringComparer.OrdinalIgnoreCase))
        {
            cancellation.ThrowIfCancellationRequested();
            FileAttributes attr;
            try { attr = File.GetAttributes(entry); } catch { continue; }
            if ((attr & FileAttributes.ReparsePoint) != 0) continue;
            if ((attr & FileAttributes.Directory) != 0)
            {
                if (config.Documents.SearchSubfolders)
                    foreach (var child in Enumerate(entry, warnings, cancellation)) yield return child;
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
        foreach (var passage in current.Catalog.Rank(query, cancellation, allowed: Allowed))
        {
            cancellation.ThrowIfCancellationRequested();
            if (!access.TryGetValue(passage.Path, out var readable)) access[passage.Path] = readable = IsCurrent(current, passage.Path);
            if (!readable || perFile.GetValueOrDefault(passage.Path) >= 3) continue;
            candidates.Add(passage);
            perFile[passage.Path] = perFile.GetValueOrDefault(passage.Path) + 1;
            if (candidates.Count >= config.Documents.MaxPassages * 4) break;
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
        try { return current.Files.TryGetValue(path, out var f) && Access.CanRead(path) && new FileInfo(path).LastWriteTimeUtc == f.Modified && new FileInfo(path).Length == f.Length; }
        catch { return false; }
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
        if (extension is ".txt" or ".md")
        {
            Add("מסמך", File.ReadAllText(path));
            return result;
        }
        if (extension == ".xlsx")
        {
            foreach (var section in SpreadsheetText.Read(path, chunk, cancellation)) Add(section.Location, section.Text);
            return result;
        }
        if (Path.GetExtension(path).Equals(".pdf", StringComparison.OrdinalIgnoreCase))
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
        if (Path.GetExtension(path).Equals(".pptx", StringComparison.OrdinalIgnoreCase))
        {
            // Presentation order can differ from slide filenames after slides are moved.
            using var presentationData = (zip.GetEntry("ppt/presentation.xml") ?? throw new InvalidDataException()).Open();
            using var relationshipsData = (zip.GetEntry("ppt/_rels/presentation.xml.rels") ?? throw new InvalidDataException()).Open();
            var presentation = XDocument.Load(presentationData);
            var relationships = XDocument.Load(relationshipsData).Descendants().Where(x => x.Name.LocalName == "Relationship" && (string?)x.Attribute("TargetMode") != "External").ToDictionary(x => (string)x.Attribute("Id")!, x => (string)x.Attribute("Target")!);
            int slideNumber = 0;
            foreach (var slide in presentation.Descendants().Where(x => x.Name.LocalName == "sldId"))
            {
                cancellation.ThrowIfCancellationRequested();
                slideNumber++;
                var id = slide.Attributes().First(x => x.Name.LocalName == "id" && x.Name.NamespaceName.Length > 0).Value;
                var target = new Uri(new Uri("https://package.invalid/ppt/presentation.xml"), relationships[id]).AbsolutePath.TrimStart('/');
                var entry = zip.GetEntry(Uri.UnescapeDataString(target)) ?? throw new InvalidDataException();
                using var data = entry.Open();
                var xml = XDocument.Load(data);
                Add("שקופית " + slideNumber, string.Join("\n", xml.Descendants().Where(x => x.Name.LocalName == "p").Select(x => string.Join(" ", x.Descendants().Where(t => t.Name.LocalName == "t").Select(t => t.Value)))));
            }
        }
        else
        {
            var entry = zip.GetEntry("word/document.xml") ?? throw new InvalidDataException();
            using var data = entry.Open();
            var xml = XDocument.Load(data);
            string heading = "מסמך";
            var buffer = new System.Text.StringBuilder();
            foreach (var paragraph in xml.Descendants().Where(x => x.Name.LocalName == "p"))
            {
                var text = string.Join("", paragraph.Descendants().Where(x => x.Name.LocalName == "t").Select(x => x.Value));
                var style = paragraph.Descendants().FirstOrDefault(x => x.Name.LocalName == "pStyle")?.Attributes().FirstOrDefault(x => x.Name.LocalName == "val")?.Value;
                if (style?.Contains("Heading", StringComparison.OrdinalIgnoreCase) == true)
                {
                    Add(heading, buffer.ToString());
                    buffer.Clear();
                    heading = text;
                }
                buffer.AppendLine(text);
            }
            Add(heading, buffer.ToString());
        }
        return result;
    }
}

