using Knowledge.Core;
using System.Text;
using System.Text.Json;

var root = @"D:\test\מדריכים לבדיקה";
var reportRoot = Path.GetFullPath("validation/guides"); Directory.CreateDirectory(reportRoot);
var cases = new (int Id, string Question, string Expected)[] {
    (1,"כמה מקום פנוי נדרש להורדת אפליקציה?","850 MB"),
    (3,"מתי מתבצע עדכון אוטומטי?","ביום שלישי ב־21:30"),
    (5,"האם איפוס רך מוחק מסמכים?","לא. מנקה מטמון בלבד ושומר מסמכים והעדפות"),
    (2,"איזה קובץ אפשר להתקין ידנית?","orapp עד 120 MB"),
    (4,"כמה זמן נשמרות העדפות לאחר הסרת אפליקציה?","14 ימים, אם נבחרה האפשרות לשמור העדפות"),
    (6,"מתי שינוי שפה נכנס לתוקף?","לאחר כניסה מחדש"),
    (11,"מה שם רשת המעבדה?","Oren-Lab"),
    (12,"לכמה זמן תקף קוד אורח?","6 שעות מההפעלה, למכשיר אחד"),
    (13,"מתי VPN מתנתק מחוסר פעילות?","25 דקות"),
    (17,"מה מגבלת ההעלאה הרגילה?","250 MB לקובץ"),
    (18,"מה מגבלת העברה גדולה ותוקף הקישור?","2 GB לקובץ, קישור ל־72 שעות"),
    (19,"כמה זמן קובץ שנמחק נשמר בסל?","30 ימים"),
    (36,"לכמה זמן תקף קישור איפוס סיסמה?","17 דקות, לשימוש אחד"),
    (37,"כמה קודי שחזור מתקבלים באימות דו־שלבי?","8 קודים חד־פעמיים"),
    (38,"כמה מכשירים אפשר לרשום לחשבון?","עד 5 מכשירים")
};
var files = Directory.GetFiles(root).Order().ToArray();
var all = new StringBuilder();
foreach (var file in files)
{
    var text = string.Join("\n", DocumentIndex.Extract(file, 1800, default).Select(p => p.Location + "\n" + p.Text));
    all.AppendLine("\n## " + Path.GetFileName(file)).AppendLine(text);
    Console.WriteLine($"{Path.GetFileName(file)}: {text.Length} characters");
}
File.WriteAllText(Path.Combine(reportRoot,"extracted.md"), all.ToString());
var config = new Config { Documents = new() { Roots = [root] } };
var index = new DocumentIndex(config); await index.RefreshAsync(true, null, default);
var report = new StringBuilder("# Guide evaluation\n\n");
foreach (var test in cases)
{
    var hits = await index.SearchAsync(test.Question, default);
    report.AppendLine($"## {test.Question}\n\nExpected: {test.Expected}\n\nRetrieved: {string.Join(", ", hits.Select(p => Path.GetFileName(p.Path)).Distinct())}\n");
    Console.WriteLine($"{test.Id:00}: top={Path.GetFileName(hits.FirstOrDefault()?.Path)}; sources={hits.Select(p=>p.Path).Distinct().Count()}");
}
File.WriteAllText(Path.Combine(reportRoot,"retrieval.md"), report.ToString());
var faqReport = new StringBuilder("# All guide FAQ retrieval checks\n\n");
int faqTotal = 0, faqCorrect = 0;
foreach (var file in files)
{
    var body = string.Join("\n", DocumentIndex.Extract(file, 1800, default).Select(p => p.Text));
    var question = System.Text.RegularExpressions.Regex.Match(body, @"שאלה:\s*([^\r\n]+\?)");
    if (!question.Success) continue;
    faqTotal++;
    var hits = await index.SearchAsync(question.Groups[1].Value, default);
    var correct = hits.FirstOrDefault()?.Path == file; if (correct) faqCorrect++;
    faqReport.AppendLine($"- {(correct ? "PASS" : "FAIL")}: {Path.GetFileName(file)} — {question.Groups[1].Value} (top: {Path.GetFileName(hits.FirstOrDefault()?.Path)})");
}
faqReport.AppendLine($"\nResult: {faqCorrect}/{faqTotal} expected guides ranked first.");
File.WriteAllText(Path.Combine(reportRoot,"all-guide-retrieval.md"), faqReport.ToString());
Console.WriteLine($"All-guide FAQ retrieval: {faqCorrect}/{faqTotal}");
if (!args.Contains("--live")) return;
var localRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WindowsChat.Local");
Storage.Root = localRoot;
var preferences = Storage.Load<Config>("local-settings.bin") ?? new();
config = LocalAiCatalog.Load(Path.GetFullPath("local/ai.config.json")).Apply(preferences, preferences.Ai.Model);
if (string.IsNullOrWhiteSpace(AiCredentials.Resolve(config.Ai))) throw new InvalidOperationException("No personal API key is configured. No live requests sent.");
config.Documents.Roots = [root]; config.GeneralChat = false;
var results = new List<object>();
var liveCases = args.Contains("--search-review") ? new (int Id,string Question,string Expected)[] {
    (6,"איך לשנות שפת ממשק?","מרכז אורן > העדפות > שפה > עברית > החל; לאחר כניסה מחדש"),
    (11,"מה שם רשת המעבדה?","Oren-Lab"),
    (0,"איך מתחברים למדפסת OR-PR-999?","No supporting source; do not substitute OR-PR-204")
} : cases.Where(t => !args.Contains("--retest") || t.Id == 5).ToArray();
foreach (var test in liveCases)
{
    using var ai = new AiClient(config);
    var question = test.Question;
    var conversation = new Conversation { Messages = [new("user", question, DateTimeOffset.Now, [])] };
    var answer = await new ChatEngine(config, ai, index).AskAsync(question, conversation, null, default);
    var sources = answer.Sources.Select(s => Path.GetFileName(s.Path)).Distinct().ToArray();
    results.Add(new { test.Id, test.Question, test.Expected, answer.Text, Sources = sources, ai.AttemptedRequests });
    File.WriteAllText(Path.Combine(reportRoot,args.Contains("--search-review") ? "answers-search-review.json" : args.Contains("--retest") ? "answers-retest.json" : "answers.json"), JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
    Console.WriteLine($"ANSWER {test.Id:00}: {answer.Text} | {string.Join(", ",sources)}");
}
