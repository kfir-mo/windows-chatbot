using Knowledge.Core;
using Xunit;

namespace Knowledge.Tests;

public sealed class AnswerFlowTests
{
    [Fact]
    public async Task StrongDocumentMatchOverridesRepeatedGeneralReply()
    {
        var path = Path.GetTempFileName();
        try
        {
            var ai = new QueueAi("""{"action":"reply","reply":"generic"}""", """{"action":"reply","reply":"still generic"}""",
                """{"answer":"Call 4321","citations":[1]}""", """{"answer":"Call 4321","citations":[1]}""");
            var answer = await new ChatEngine(new(), ai, new Search(path)).AskAsync("Call 4321", new(), null, default);
            Assert.Equal("Call 4321", answer.Text); Assert.Single(answer.Sources);
        }
        finally { File.Delete(path); }
    }
    [Fact]
    public async Task PersonalPromptIsUsedWithoutSourcesAndDoesNotAffectGeneralChat()
    {
        var type = new ChatType { Name = "writer", Prompt = "Use short sentences." };
        var config = type.Apply(new());
        var ai = new QueueAi("answer");
        await new ChatEngine(config, ai, new Search("unused")).AskAsync("Help me write", new(), null, default);
        Assert.Contains(type.Prompt, ai.Instructions.Single());
        var general = ChatType.General();
        general.Prompt = "ignored";
        Assert.Equal("", general.Apply(new()).AssistantPrompt);
    }
    [Fact]
    public async Task GeneralChatAnswersWithoutPlanningOrSearching()
    {
        var search = new Search("unused");
        var ai = new QueueAi("תשובה כללית");
        var config = ChatType.General().Apply(new Config { Documents = new() { Root = @"C:\private" } });
        Assert.Empty(config.Documents.EffectiveRoots);
        var result = await new ChatEngine(config, ai, search).AskAsync("איך מאפסים סיסמה?", new(), null, default);
        Assert.Equal("תשובה כללית", result.Text);
        Assert.Empty(result.Sources);
        Assert.False(search.Searched);
        Assert.Null(search.ReadPath);
        Assert.Single(ai.Inputs);
        // General chat has the answer modes too: still one call, asking for a short or a thorough answer.
        foreach (var (mode, words) in new[] { (AnswerMode.Fast, "fast mode"), (AnswerMode.Deep, "deep mode") })
        {
            var styled = new QueueAi("תשובה");
            await new ChatEngine(config, styled, search) { Mode = mode }.AskAsync("מה ההבדל בין RAM לאחסון?", new(), null, default);
            Assert.Contains(words, Assert.Single(styled.Instructions));
        }
        Assert.False(search.Searched);
    }
    [Fact]
    public void ModelInputPreservesReadableHebrew()
    {
        var json = AiJson.Serialize(new { question = "איך מאפסים סיסמה?" });
        Assert.Contains("איך מאפסים סיסמה", json);
        Assert.DoesNotContain("\\u05", json);
    }

    [Fact]
    public async Task ReviewCorrectsGreetingAndChecksGroundedAnswer()
    {
        var path = Path.GetTempFileName();
        try
        {
            var search = new Search(path);
            var ai = new QueueAi("""{"action":"reply","reply":"hello"}""",
                """{"action":"search","query":"password"}""",
                """{"answer":"wrong draft","citations":[1]}""",
                """{"answer":"Call 4321","citations":[1]}""");
            var answer = await new ChatEngine(new(), ai, search).AskAsync("איך מאפסים סיסמה?", new(), null, default);
            Assert.Equal("Call 4321", answer.Text);
            Assert.Single(answer.Sources);
            Assert.Contains("איך מאפסים סיסמה", ai.Inputs[0]);
            Assert.Contains("wrong draft", ai.Inputs[^1]);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task FirstSourceSelectionReadsDocumentInsteadOfKeywordSearch()
    {
        var path = Path.GetTempFileName();
        try
        {
            var search = new Search(path);
            var chat = new Conversation { Messages = [new("assistant", "choose", DateTimeOffset.Now, [new(path, "section one"), new(path, "section two")])] };
            var ai = new QueueAi("""{"answer":"Call 4321","citations":[1]}""", """{"answer":"Call 4321","citations":[1]}""");
            var answer = await new ChatEngine(new(), ai, search).AskAsync("הראשון", chat, null, default);
            Assert.Equal(path, search.ReadPath);
            Assert.False(search.Searched);
            Assert.Single(answer.Sources);
            Assert.Contains("Call 4321", ai.Inputs[0]);
            Assert.Equal(2, ai.Inputs.Count);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task AnswerModesUseDifferentSteps()
    {
        var path = Path.GetTempFileName();
        try
        {
            const string answer = """{"answer":"Reset it","citations":[1]}""";
            // Fast: no planner and no reviewer, one answer call asking for a short answer.
            var fast = new QueueAi(answer);
            var search = new Search(path);
            var result = await new ChatEngine(new(), fast, search) { Mode = AnswerMode.Fast }.AskAsync("how do I reset the frozen app", new(), null, default);
            Assert.Equal("Reset it", result.Text);
            Assert.Single(fast.Instructions);
            Assert.Contains("fast mode", fast.Instructions[0]);
            Assert.True(search.Searched);
            Assert.Null(search.ReadPath);

            // Normal: planner, answer and reviewer; no full document read.
            var normal = new QueueAi("""{"action":"search","query":"reset frozen app"}""", answer, answer);
            search = new Search(path);
            await new ChatEngine(new(), normal, search).AskAsync("how do I reset the frozen app", new(), null, default);
            Assert.Equal(3, normal.Instructions.Count);
            Assert.DoesNotContain(normal.Instructions, i => i.Contains("fast mode") || i.Contains("deep mode"));
            Assert.Null(search.ReadPath);

            // Deep: also reads the best document and asks for a complete answer and a completeness review.
            var deep = new QueueAi("""{"action":"search","query":"reset frozen app"}""", answer, answer);
            search = new Search(path, "If it still fails, open a support ticket.");
            await new ChatEngine(new(), deep, search) { Mode = AnswerMode.Deep }.AskAsync("how do I reset the frozen app", new(), null, default);
            Assert.Equal(path, search.ReadPath);
            Assert.Contains("support ticket", deep.Inputs[1]);
            Assert.Contains("deep mode", deep.Instructions[1]);
            Assert.Contains("left out", deep.Instructions[2]);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task FastModeSearchesAgainWithThePlannersWordsOnlyAfterAMiss()
    {
        var path = Path.GetTempFileName();
        try
        {
            // The user's own words found nothing to cite; the planner adds the documents' words and the second answer cites.
            var ai = new QueueAi("""{"answer":"","citations":[]}""", """{"action":"search","query":"uninstall removal"}""", """{"answer":"Remove it","citations":[1]}""");
            var search = new Search(path);
            var result = await new ChatEngine(new(), ai, search) { Mode = AnswerMode.Fast }.AskAsync("how do I take the app off", new(), null, default);
            Assert.Equal("Remove it", result.Text);
            Assert.Equal(3, ai.Instructions.Count);
            Assert.Equal("uninstall removal", search.Queries[^1]);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task MaximumSearchFindsADocumentThroughTheAisRewordingsAndKeepsOnlyRelevantPassages()
    {
        // A misspelled English error; the guide is in Hebrew and only a rewording reaches it.
        const string question = "can sign you in becouse your domain isnt available";
        var guide = new Passage(@"C:\Docs\שגיאות.docx", "התחברות", "השגיאה We can't sign you in because your domain isn't available מופיעה כשאין חיבור לרשת הארגון. התחברו ל-VPN ונסו שוב.");
        var noise = new Passage(@"C:\Docs\מדפסות.docx", "כללי", "רשימת מדפסות זמינות בבניין.");
        var search = new WordSearch(("הדומיין", guide), ("available", noise));
        var ai = new QueueAi("""{"queries":["We can't sign you in because your domain isn't available","הדומיין לא זמין בהתחברות"]}""", """{"relevant":[2]}""",
            """{"answer":"התחברו ל-VPN ונסו שוב.","citations":[1]}""", """{"answer":"התחברו ל-VPN ונסו שוב.","citations":[1]}""");
        var result = await new ChatEngine(new(), ai, search) { Mode = AnswerMode.Maximum }.AskAsync(question, new(), null, default);
        Assert.Equal("התחברו ל-VPN ונסו שוב.", result.Text);
        Assert.Equal(guide.Path, Assert.Single(result.Sources).Path);
        // Rewording, choosing passages, answer and review: no planner; every wording was searched.
        Assert.Equal(4, ai.Instructions.Count);
        Assert.Contains("search queries", ai.Instructions[0]);
        Assert.Contains("הדומיין לא זמין בהתחברות", search.Queries);
        Assert.Contains(question, search.Queries);
        // Only the chosen passage (and the rest of its document) reaches the answer; the printer list does not.
        Assert.Contains("VPN", ai.Inputs[2]); Assert.DoesNotContain("מדפסות", ai.Inputs[2]);
        Assert.Contains("deep mode", ai.Instructions[2]);
    }
    [Fact]
    public async Task DeepAndMaximumAskFirstWhenTheDocumentsFoundCoverSeveralTopics()
    {
        var path = Path.GetTempFileName();
        try
        {
            const string ask = """{"questions":[{"question":"באיזו מערכת?","options":["Outlook","Teams","VPN","Outlook","Word"]},{"question":"  ","options":[]}]}""";
            // Deep: a quick local search, then one AI call that sees what was found and asks; nothing else runs.
            var ai = new QueueAi(ask);
            var search = new Search(path);
            var engine = new ChatEngine(new(), ai, search) { Mode = AnswerMode.Deep, MayClarify = true };
            var result = await engine.AskAsync("יש לי בעיה במחשב", new(), null, default);
            Assert.Equal("", result.Text);
            var question = Assert.Single(engine.Clarifications);
            Assert.Equal("באיזו מערכת?", question.Question); Assert.Equal(4, question.Options.Count);
            Assert.Single(ai.Instructions); Assert.Contains("Call 4321", ai.Inputs[0]);
            Assert.Equal(["יש לי בעיה במחשב"], search.Queries);

            // A specific request: the same call says no questions and the deep answer follows.
            const string answer = """{"answer":"Reset it","citations":[1]}""";
            ai = new QueueAi("""{"questions":[]}""", """{"action":"search","query":"Outlook problem"}""", answer, answer);
            engine = new ChatEngine(new(), ai, new Search(path)) { Mode = AnswerMode.Deep, MayClarify = true };
            Assert.Equal("Reset it", (await engine.AskAsync("Outlook לא נפתח", new(), null, default)).Text);
            Assert.Empty(engine.Clarifications); Assert.Equal(4, ai.Instructions.Count);

            // The second try carries the answers and may not ask; normal mode never asks.
            ai = new QueueAi("""{"action":"search","query":"Outlook problem"}""", answer, answer);
            await new ChatEngine(new(), ai, new Search(path)) { Mode = AnswerMode.Deep }.AskAsync("יש לי בעיה במחשב\nבאיזו מערכת? Outlook", new(), null, default);
            Assert.Equal(3, ai.Instructions.Count);
            ai = new QueueAi("""{"action":"search","query":"computer problem"}""", answer, answer);
            await new ChatEngine(new(), ai, new Search(path)) { MayClarify = true }.AskAsync("יש לי בעיה במחשב", new(), null, default);
            Assert.Equal(3, ai.Instructions.Count);

            // Maximum asks the same way; a greeting never costs the extra call.
            ai = new QueueAi(ask);
            engine = new ChatEngine(new(), ai, new Search(path)) { Mode = AnswerMode.Maximum, MayClarify = true };
            await engine.AskAsync("זה לא עובד", new(), null, default);
            Assert.Single(engine.Clarifications); Assert.Single(ai.Instructions);
            ai = new QueueAi("""{"answer":"היי","citations":[]}""");
            engine = new ChatEngine(new(), ai, new Search(path)) { Mode = AnswerMode.Deep, MayClarify = true };
            await engine.AskAsync("שלום", new(), null, default);
            Assert.Empty(engine.Clarifications); Assert.Empty(ai.Instructions);
        }
        finally { File.Delete(path); }
    }
    [Fact]
    public async Task ConversationTitleIsAShortSummaryNotTheRequest()
    {
        var ai = new QueueAi("\"כתיבת קוד בפייתון.\"\nextra line");
        Assert.Equal("כתיבת קוד בפייתון", await ConversationTopic.SummarizeAsync(ai, "איך כותבים קוד פשוט בפייתון? תן לי דוגמה מלאה עם הסברים", null, default));
        Assert.Null(ConversationTopic.Clean("  "));
        // Until the title arrives (or if it can't be had): the first words, never longer than the header can show.
        var fallback = ConversationTopic.Fallback("איך כותבים קוד פשוט בפייתון עם ספריות חיצוניות?");
        Assert.True(fallback.Length <= ConversationTopic.MaxCharacters + 1); Assert.EndsWith("…", fallback);
        Assert.Equal("שאלה קצרה", ConversationTopic.Fallback("שאלה קצרה?"));
    }
    [Fact]
    public void InlineNumberedPointsGetTheirOwnLines()
    {
        Assert.Equal("יש לבצע את הצעדים הבאים:\n1. שער אורן\n2. אבטחה\n3. אימות דו שלבי. גרסה 2.5 בלבד.", AnswerFormat.SplitInlineLists("יש לבצע את הצעדים הבאים: 1. שער אורן 2. אבטחה 3. אימות דו שלבי. גרסה 2.5 בלבד."));
        Assert.Equal("שלב 3. בלבד", AnswerFormat.SplitInlineLists("שלב 3. בלבד"));
        Assert.Equal("```\n1. a 2. b\n```", AnswerFormat.SplitInlineLists("```\n1. a 2. b\n```"));
    }
    private sealed class QueueAi(params string[] responses) : IAiClient
    {
        private readonly Queue<string> queue = new(responses);
        public List<string> Inputs = [];
        public List<string> Instructions = [];
        public Task<string> CompleteAsync(string instruction, string input, CancellationToken cancellation)
        { Inputs.Add(input); Instructions.Add(instruction); return Task.FromResult(queue.Dequeue()); }
    }
    /// <summary>Finds a passage when the query contains its word, like the keyword index.</summary>
    private sealed class WordSearch(params (string Word, Passage Passage)[] entries) : IDocumentSearch
    {
        public List<string> Queries = [];
        public Task RefreshAsync(bool force, IProgress<string>? progress, CancellationToken cancellation) => Task.CompletedTask;
        public Task<List<Passage>> SearchAsync(string query, CancellationToken cancellation)
        { Queries.Add(query); return Task.FromResult(entries.Where(e => query.Contains(e.Word, StringComparison.OrdinalIgnoreCase)).Select(e => e.Passage).ToList()); }
        public Task<List<Passage>> ReadAsync(string path, CancellationToken cancellation) => Task.FromResult(entries.Select(e => e.Passage).Where(p => p.Path == path).ToList());
        public bool CanRead(string path) => true;
    }
    private sealed class Search(string path, string? more = null) : IDocumentSearch
    {
        public bool HasStrongMatch(string query) => RetrievalText.HasStrongMatch(query, [new(path, "Support", "Call 4321")]);
        public bool Searched;
        public List<string> Queries = [];
        public string? ReadPath;
        public Task RefreshAsync(bool force, IProgress<string>? progress, CancellationToken cancellation) => Task.CompletedTask;
        public Task<List<Passage>> SearchAsync(string query, CancellationToken cancellation)
        { Searched = true; Queries.Add(query); return Task.FromResult(new List<Passage> { new(path, "Support", "Call 4321") }); }
        public Task<List<Passage>> ReadAsync(string selected, CancellationToken cancellation)
        {
            ReadPath = selected;
            List<Passage> read = [new(path, "Support", "Call 4321")];
            if (more != null) read.Add(new(path, "End", more));
            return Task.FromResult(read);
        }
    }
}
