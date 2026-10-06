using Knowledge.Core;

var root = Path.GetFullPath(args.Length > 0 ? args[0] : "samples/test-documents");
var files = Directory.GetFiles(root, "*.docx", SearchOption.AllDirectories);
if (files.Length != 3) throw new Exception("Expected three sample documents.");
foreach (var path in files)
{
    var passages = DocumentIndex.Extract(path, 1800, default);
    if (passages.Count < 5 || passages.Any(p => string.IsNullOrWhiteSpace(p.Text)))
        throw new Exception("Missing document sections.");
    Console.WriteLine($"Extracted {passages.Count} sections: {Path.GetFileName(path)}");
}
var config = new Config { Documents = new DocumentOptions { Root = root } };
var index = new DocumentIndex(config);
await index.RefreshAsync(true, null, default);
foreach (var query in new[] { "סיסמה", "VPN", "חופשה", "עובד חדש" })
{
    var hits = await index.SearchAsync(query, default);
    if (hits.Count == 0) throw new Exception($"No results for {query}");
    Console.WriteLine($"Search {query}: {hits.Count} passages");
}
Console.WriteLine("Sample extraction and recursive search passed.");
