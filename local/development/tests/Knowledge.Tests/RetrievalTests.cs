using Knowledge.Core;
using Xunit;

namespace Knowledge.Tests;

public sealed class RetrievalTests
{
    [Fact]
    public void RankingPrefersRareTopicOverRepeatedCommonTerms()
    {
        var passages = Enumerable.Range(0, 500).Select(i => new Passage($"file{i}.docx", "מסמך", string.Join(" ", Enumerable.Repeat("מידע מסמך עובדים בקשה", 100)))).ToList();
        passages.Add(new("specific.docx", "גישה", "למערכת נרקיס דרוש אישור המנהל."));
        Assert.Equal("specific.docx", new PassageCatalog(passages).Rank("בקשה למערכת נרקיס", default).First().Path);
    }
    [Fact]
    public void HebrewDiacriticsAndPrefixesMatchWithoutAi()
    {
        var catalog = new PassageCatalog([new("support.docx", "גישה", "סִיסְמָה חדשה למערכת")]);
        Assert.Single(catalog.Rank("הסיסמה", default));
        Assert.Empty(catalog.Rank("zzzzunknownword", default));
    }
    [Fact]
    public void HebrewPluralAndConstructFormsMatchSingular()
    {
        var catalog = new PassageCatalog([
            new(@"C:\docs\מדריך עדכון שומר מסך.docx", "מסמך", "פתחו את ההגדרות ובחרו תמונה לנעילת המחשב."),
            new(@"C:\docs\נוהל חופשה.docx", "מסמך", "בקשת חופשה מאושרת על ידי המנהל.")]);
        Assert.Equal("מדריך עדכון שומר מסך.docx", Path.GetFileName(catalog.Rank("שומרי מסך", default).First().Path));
        Assert.Equal("נוהל חופשה.docx", Path.GetFileName(catalog.Rank("חופשות", default).First().Path));
    }
    [Fact]
    public void FolderMapDoesNotOutrankTheDocumentItLists()
    {
        var map = string.Join("\n", Enumerate().Select(i => $@"C:\docs\IT\מדריך {i}.docx,{i}"));
        static IEnumerable<int> Enumerate() => Enumerable.Range(1, 30);
        var catalog = new PassageCatalog([
            new(@"C:\docs\FOLDER_MAP.csv", "מסמך", map + "\nC:\\docs\\IT\\מדריך עדכון שומר מסך.docx,31\nC:\\docs\\IT\\שומר מסך ישן.docx,32"),
            new(@"C:\docs\IT\מדריך עדכון שומר מסך.docx", "מסמך", "כדי לעדכן את שומר המסך פתחו הגדרות > התאמה אישית > מסך נעילה.")]);
        Assert.EndsWith("מדריך עדכון שומר מסך.docx", catalog.Rank("איך מעדכנים שומר מסך", default).First().Path);
        Assert.True(RetrievalText.LooksLikeFileList(map));
        Assert.True(RetrievalText.IsFileListName(@"C:\docs\FOLDER_MAP.csv"));
        Assert.False(RetrievalText.IsFileListName(@"C:\docs\מדריך עדכון שומר מסך.docx"));
        Assert.False(RetrievalText.LooksLikeFileList("שורה ראשונה\nשורה שנייה\nשורה שלישית\nשורה רביעית"));
    }
    [Fact]
    public void EvidenceKeepsDifferentNumbersAndRespectsBudget()
    {
        var shared = string.Join(" ", Enumerable.Repeat("This is the procedure for requesting approval from your manager", 8));
        var passages = new[] { new Passage("a.docx", "policy", shared + " allowed days 5"), new Passage("a.docx", "policy", shared + " allowed days 10") };
        var options = new DocumentOptions { EvidenceCharacters = 1400, ExcerptCharacters = 700 };
        var result = RetrievalText.Budget(passages, "allowed days", options);
        Assert.Equal(2, result.Count);
        Assert.Contains("days 5", result[0].Text);
        Assert.Contains("days 10", result[1].Text);
        Assert.True(result.Sum(p => p.Text.Length) <= 1400);
    }
    [Fact]
    public void ExcerptFindsAnswerNearEndInsteadOfTakingBeginning()
    {
        var text = string.Join(" ", Enumerable.Repeat("general unrelated information", 200)) + "\nנרקיס אישור בקשה קוד 76543";
        var result = RetrievalText.Excerpt(text, "נרקיס", 600);
        Assert.Contains("76543", result);
        Assert.True(result.Length <= 600);
        Assert.Contains(result, text);
    }
    [Fact]
    public void ContextIsBoundedAndDoesNotDuplicateCurrentQuestion()
    {
        var chat = new Conversation();
        for (int i = 0; i < 100; i++) chat.Messages.Add(new(i % 2 == 0 ? "user" : "assistant", new string('x', 6000), DateTimeOffset.Now, []));
        chat.Messages.Add(new("user", "unique-current-request", DateTimeOffset.Now, []));
        var sources = Enumerable.Range(0, 40).Select(i => new Source($"some-long-file-name-number-{i}.docx", "section")).ToList();
        var context = ConversationContext.Build("unique-current-request", chat, sources, new());
        Assert.True(context.Length <= 2000);
        Assert.DoesNotContain("unique-current-request", context);
        using var json = System.Text.Json.JsonDocument.Parse(context[(context.IndexOf("availableSources: ") + 18)..]);
        Assert.True(json.RootElement.GetArrayLength() > 0);
    }
}
