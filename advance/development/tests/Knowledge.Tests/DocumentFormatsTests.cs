using System.IO.Compression;
using Knowledge.Core;
using Xunit;

namespace Knowledge.Tests;
public sealed class DocumentFormatsTests
{
    [Fact]
    public void VisualHebrewPdfIsNormalizedWithoutChangingIdentifiers()
    {
        var visual = "ךמסמ\nהלאש\nהבושת\nםש תשר Oren-Lab\nןמז 25 VPN";
        var result = PdfTextOrder.Normalize(visual);
        Assert.Contains("מסמך", result); Assert.Contains("שאלה", result);
        Assert.Contains("רשת שם Oren-Lab", result); Assert.Contains("25 VPN", result);
        var logical = "מסמך\nשאלה\nתשובה\nשם רשת Oren-Lab\nזמן 25 VPN";
        Assert.Equal(logical, PdfTextOrder.Normalize(logical));
    }
    [Fact]
    public async Task TextMarkdownAndSpreadsheetAreIndexedAndPreserveStoredValues()
    {
        var dir = Path.Combine(Path.GetTempPath(), "formats-" + Guid.NewGuid()); Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "guide.txt"), "השינוי נכנס לתוקף לאחר כניסה מחדש.");
            File.WriteAllText(Path.Combine(dir, "guide.md"), "# Proxy\nproxy.example:8087");
            var workbook = Path.Combine(dir, "limits.xlsx");
            using (var zip = ZipFile.Open(workbook, ZipArchiveMode.Create))
            {
                void Part(string name, string xml) { using var writer = new StreamWriter(zip.CreateEntry(name).Open()); writer.Write(xml); }
                Part("xl/workbook.xml", """<workbook xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships"><sheets><sheet name="Uploads" r:id="rId1"/></sheets></workbook>""");
                Part("xl/_rels/workbook.xml.rels", """<Relationships><Relationship Id="rId1" Target="worksheets/sheet1.xml"/></Relationships>""");
                Part("xl/sharedStrings.xml", "<sst><si><t>Upload limit</t></si></sst>");
                Part("xl/worksheets/sheet1.xml", """<worksheet><sheetData><row r="1"><c r="A1" t="s"><v>0</v></c><c r="B1" t="inlineStr"><is><t>250 MB</t></is></c></row><row r="2"><c r="A2"><f>1+2</f><v>3</v></c></row></sheetData></worksheet>""");
            }
            var text = string.Join("\n", DocumentIndex.Extract(workbook, 1800, default).Select(p => p.Text));
            Assert.Contains("Upload limit", text); Assert.Contains("250 MB", text); Assert.Contains("A2: 3", text); Assert.DoesNotContain("1+2", text);
            var index = new DocumentIndex(new() { Documents = new() { Roots = [dir] } });
            await index.RefreshAsync(false, null, default);
            Assert.Equal(3, (await index.ListAsync(default)).Count);
            Assert.Single((await index.SearchAsync("Upload limit", default)).Select(p => p.Path).Distinct());
        }
        finally { Directory.Delete(dir, true); }
    }
    [Fact]
    public void RankDropsWeakBoilerplateMatches()
    {
        var catalog = new PassageCatalog([
            new("wifi.pdf", "WiFi", "מה שם רשת המעבדה? שם הרשת Oren-Lab. בדקו הרשאות חשבון המעבדה."),
            new("printer.txt", "Printer", "בדקו הרשאות חשבון המעבדה. קוד מדפסת OR-204."),
            new("mail.txt", "Mail", "בדקו הרשאות חשבון המעבדה. מכסת הדואר 3 GB.")]);
        var found = catalog.Rank("מה שם רשת המעבדה?", default).ToList();
        Assert.Single(found); Assert.Equal("wifi.pdf", found[0].Path);
    }
}
