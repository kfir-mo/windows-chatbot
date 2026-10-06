using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;
using System.Xml.Linq;
using Knowledge.Core;

var root = Path.Combine(Path.GetTempPath(), "WindowsChat-500-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var topics = new[] { "סיסמה", "חופשה", "הרשאות", "מדפסת", "נסיעות", "רכש", "קליטה", "אבטחה", "חשבוניות", "ציוד", "password", "vacation", "permissions", "printer", "travel", "procurement", "onboarding", "security", "invoices", "equipment" };
var tests = new List<(string Question, string Expected, string Fact)>();
try
{
    var corpus = Stopwatch.StartNew();
    for (int i = 0; i < 500; i++)
    {
        var topic = topics[i % topics.Length];
        var project = "project" + i.ToString("D4");
        var path = Path.Combine(root, $"{topic}-{project}.docx");
        var fact = "CODE" + i.ToString("D4");
        var boilerplate = string.Join(" ", Enumerable.Repeat("ארגון עובדים מידע מסמך הנחיות בקשה עבודה company employee information document procedure request work.", 22));
        var paragraphs = Enumerable.Range(0, 8).Select(n => $"Section {n}. {boilerplate}").Append($"{topic} {project}: אישור הבקשה approval code {fact}. פנו למוקד contact support.");
        XNamespace w = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        using var writer = new StreamWriter(zip.CreateEntry("word/document.xml").Open());
        new XDocument(new XElement(w + "document", new XElement(w + "body", paragraphs.Select(t => new XElement(w + "p", new XElement(w + "r", new XElement(w + "t", t))))))).Save(writer);
        tests.Add(($"מה קוד האישור עבור {topic} {project}?", path, fact));
    }
    var config = new Config { Documents = new() { Root = root } };
    var index = new DocumentIndex(config);
    await index.RefreshAsync(false, null, default);
    var cold = index.Statistics;
    if (cold.Files != 500 || cold.Extracted != 500) throw new Exception("500-file extraction incomplete");
    await index.RefreshAsync(false, null, default);
    var warm = index.Statistics;
    if (warm.Extracted != 0 || warm.Reused != 500) throw new Exception("Unchanged files were re-extracted");
    var reloaded = new DocumentIndex(config);
    var baselinePassages = Directory.GetFiles(root, "*.docx").SelectMany(p => DocumentIndex.Extract(p, 1800, default)).ToList();
    HashSet<string> OldTerms(string value) => System.Text.RegularExpressions.Regex.Matches(value.ToLowerInvariant(), @"[\p{L}\p{N}]{2,}").Select(m => m.Value).Where(w => !new[] { "של", "את", "על", "מה", "איך", "the", "and", "how", "what" }.Contains(w)).ToHashSet();
    var baselineTimes = new List<double>();
    var baselineSizes = new List<int>();
    foreach (var test in tests.Take(20))
    {
        var terms = OldTerms(test.Question);
        var timer = Stopwatch.StartNew();
        var hits = baselinePassages.Select(p => (p, score: OldTerms(p.Text).Intersect(terms).Count() + .5 * OldTerms(Path.GetFileName(p.Path)).Intersect(terms).Count()))
            .Where(x => x.score > 0).OrderByDescending(x => x.score).Take(8).Select(x => x.p).ToList();
        baselineTimes.Add(timer.Elapsed.TotalMilliseconds);
        baselineSizes.Add(hits.Sum(p => p.Text.Length));
    }
    var latencies = new List<double>();
    int top1 = 0, factHits = 0, characters = 0;
    foreach (var test in tests)
    {
        var timer = Stopwatch.StartNew();
        var hits = await reloaded.SearchAsync(test.Question, default);
        latencies.Add(timer.Elapsed.TotalMilliseconds);
        if (hits.FirstOrDefault()?.Path == test.Expected) top1++;
        if (hits.Any(h => h.Path == test.Expected && h.Text.Contains(test.Fact))) factHits++;
        var size = hits.Sum(h => h.Text.Length);
        if (size > config.Documents.EvidenceCharacters) throw new Exception("Evidence budget exceeded");
        characters += size;
    }
    if (top1 != tests.Count || factHits != tests.Count) throw new Exception($"Retrieval regression: top1 {top1}/{tests.Count}; facts {factHits}/{tests.Count}");
    // Update only one file; verify deletion without requiring another refresh.
    var removed = tests[0].Expected;
    File.Delete(removed);
    if ((await index.SearchAsync(tests[0].Question, default)).Any(p => p.Path == removed)) throw new Exception("Deleted source returned");
    var changed = tests[1].Expected;
    File.SetLastWriteTimeUtc(changed, DateTime.UtcNow.AddMinutes(1));
    await index.RefreshAsync(false, null, default);
    if (index.Statistics.Extracted != 1 || index.Statistics.Files != 499) throw new Exception("Incremental refresh failed");
    var report = new { files = 500, queries = tests.Count, top1, factHits, cold, warm, incremental = index.Statistics,
        searchMedianMs = latencies.Order().ElementAt(latencies.Count / 2), searchMaxMs = latencies.Max(),
        meanEvidenceCharacters = characters / tests.Count, evidenceLimit = config.Documents.EvidenceCharacters,
        baselineQueries = baselineTimes.Count, baselineMedianMs = baselineTimes.Order().ElementAt(baselineTimes.Count / 2), baselineMeanEvidenceCharacters = baselineSizes.Average(),
        oldMaximumEvidenceCharacters = 8 * 1800, aiCallsForIndexingAndSearch = 0,
        notes = "Synthetic DOCX corpus on local disk, not SMB. Character counts are not token counts. Covers rare identifiers, Hebrew/English, facts near document end, cache reload, unchanged/update/delete behavior." };
    Directory.CreateDirectory("validation");
    File.WriteAllText("validation/index-benchmark.json", JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine(JsonSerializer.Serialize(report));
}
finally { Directory.Delete(root, true); }
