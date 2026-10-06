using System.IO.Compression;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace Knowledge.Core;
public static class SpreadsheetText
{
    // Read stored values only. Never execute formulas, macros or external workbook links.
    public static IEnumerable<Passage> Read(string path, int chunk, CancellationToken cancellation)
    {
        using var input = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var zip = new ZipArchive(input, ZipArchiveMode.Read);
        XDocument Xml(string name)
        {
            using var stream = (zip.GetEntry(name) ?? throw new InvalidDataException(name)).Open();
            using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 32 * 1024 * 1024 });
            return XDocument.Load(reader);
        }
        var strings = zip.GetEntry("xl/sharedStrings.xml") == null ? [] : Xml("xl/sharedStrings.xml").Descendants().Where(e => e.Name.LocalName == "si")
            .Select(e => string.Concat(e.Descendants().Where(t => t.Name.LocalName == "t").Select(t => t.Value))).ToArray();
        var relationships = Xml("xl/_rels/workbook.xml.rels").Descendants().Where(e => e.Name.LocalName == "Relationship" && (string?)e.Attribute("TargetMode") != "External")
            .ToDictionary(e => (string)e.Attribute("Id")!, e => (string)e.Attribute("Target")!);
        foreach (var sheet in Xml("xl/workbook.xml").Descendants().Where(e => e.Name.LocalName == "sheet"))
        {
            cancellation.ThrowIfCancellationRequested();
            var id = sheet.Attributes().First(a => a.Name.LocalName == "id").Value;
            if (!relationships.TryGetValue(id, out var target)) continue;
            var name = (string?)sheet.Attribute("name") ?? "Sheet";
            var entry = Uri.UnescapeDataString(new Uri(new Uri("https://package.invalid/xl/workbook.xml"), target).AbsolutePath.TrimStart('/'));
            var buffer = new StringBuilder(); string start = "", end = "";
            foreach (var row in Xml(entry).Descendants().Where(e => e.Name.LocalName == "row"))
            {
                cancellation.ThrowIfCancellationRequested();
                var cells = row.Elements().Where(e => e.Name.LocalName == "c").Select(cell =>
                {
                    var type = (string?)cell.Attribute("t");
                    var value = cell.Elements().FirstOrDefault(e => e.Name.LocalName == "v")?.Value ?? "";
                    if (type == "s") value = int.TryParse(value, out var n) && n >= 0 && n < strings.Length ? strings[n] : "";
                    if (type == "inlineStr") value = string.Concat(cell.Descendants().Where(e => e.Name.LocalName == "t").Select(e => e.Value));
                    if (type == "b") value = value == "1" ? "TRUE" : "FALSE";
                    return string.IsNullOrWhiteSpace(value) ? "" : $"{(string?)cell.Attribute("r")}: {value}";
                }).Where(v => v.Length > 0).ToArray();
                if (cells.Length == 0) continue;
                var line = string.Join(" | ", cells);
                if (buffer.Length > 0 && buffer.Length + line.Length > chunk)
                {
                    yield return new(path, $"{name} · שורות {start}–{end}", buffer.ToString());
                    buffer.Clear();
                }
                if (buffer.Length == 0) start = (string?)row.Attribute("r") ?? "?";
                end = (string?)row.Attribute("r") ?? "?";
                buffer.AppendLine(line);
            }
            if (buffer.Length > 0) yield return new(path, $"{name} · שורות {start}–{end}", buffer.ToString());
        }
    }
}
