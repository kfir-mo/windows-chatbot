using Knowledge.Core;
using System.Text.Json;
var label = args.FirstOrDefault() ?? "current";
var index = new DocumentIndex(new Config { Documents = new() { Roots = [@"D:\test\מדריכים לבדיקה"] } });
await index.RefreshAsync(false, null, default);
var cases = new (string Query,int Expected)[] {
 ("מה שם הרשת במעבדה",11), ("מה תקרת העלאה לקובץ בודד",17),
 ("כמה גיגה אפשר לשלוח בהעברה גדולה",18), ("אחרי כמה זמן מנתקים VPN",13),
 ("איפוס רך מסמכים",5), ("קודי שיחזור אימות",37), ("מה הכתובת של הפרוקסי",15),
 ("איפו נשמר דוח האבחון",47), ("חיבור למדספת",31), ("שיחזור קובץ מהסל",19),
 ("איך לשנות שפת ממשק",6), ("העדפות אחרי הסרת אפליקציה",4),
 ("\"Oren-Lab\"",11), ("\"OR-PR-204\"",31), ("OR-PR-999",0),
 ("\"phantom network\"",0), ("כמה עולה רישיון Photoshop?",0), ("מדיניות חופשת לידה",0)
};
var results = new List<object>(); int passed = 0;
foreach(var item in cases) {
 var hits = await index.SearchAsync(item.Query, default);
 var top = hits.FirstOrDefault() == null ? "" : Path.GetFileName(hits[0].Path);
 var ok = item.Expected == 0 ? hits.Count == 0 : top.StartsWith(item.Expected.ToString("00")+"_");
 if(ok)passed++;
 results.Add(new{ item.Query,item.Expected,Passed=ok,Top=top,Files=hits.Select(p=>Path.GetFileName(p.Path)).Distinct().ToArray() });
 Console.WriteLine($"{(ok ? "PASS" : "FAIL")} {item.Query}: {top}");
}
Directory.CreateDirectory("validation/search-review");
File.WriteAllText($"validation/search-review/{label}.json",JsonSerializer.Serialize(new{Passed=passed,Total=cases.Length,Results=results},new JsonSerializerOptions{WriteIndented=true,Encoder=System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping}));
Console.WriteLine($"{passed}/{cases.Length}");
