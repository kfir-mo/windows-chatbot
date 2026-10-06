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

    private sealed class QueueAi(params string[] responses) : IAiClient
    {
        private readonly Queue<string> queue = new(responses);
        public List<string> Inputs = [];
        public List<string> Instructions = [];
        public Task<string> CompleteAsync(string instruction, string input, CancellationToken cancellation)
        { Inputs.Add(input); Instructions.Add(instruction); return Task.FromResult(queue.Dequeue()); }
    }
    private sealed class Search(string path) : IDocumentSearch
    {
        public bool HasStrongMatch(string query) => RetrievalText.HasStrongMatch(query, [new(path, "Support", "Call 4321")]);
        public bool Searched;
        public string? ReadPath;
        public Task RefreshAsync(bool force, IProgress<string>? progress, CancellationToken cancellation) => Task.CompletedTask;
        public Task<List<Passage>> SearchAsync(string query, CancellationToken cancellation)
        { Searched = true; return Task.FromResult(new List<Passage> { new(path, "Support", "Call 4321") }); }
        public Task<List<Passage>> ReadAsync(string selected, CancellationToken cancellation)
        { ReadPath = selected; return Task.FromResult(new List<Passage> { new(path, "Support", "Call 4321") }); }
    }
}
