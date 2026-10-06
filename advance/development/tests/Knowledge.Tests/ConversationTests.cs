using Knowledge.Core;
using Xunit;

namespace Knowledge.Tests;

public sealed class ConversationTests
{
    [Theory]
    [InlineData("תוכל לעזור לי?", "בשמחה, במה תרצה עזרה?")]
    [InlineData("Explain what a computer is", "A computer processes information.")]
    public async Task DirectReplyNeverSearchesDocumentsOrRouting(string question, string reply)
    {
        var config = new Config();
        var search = new RecordingSearch();
        var ai = new FakeAi(System.Text.Json.JsonSerializer.Serialize(new { action = "reply", reply }));
        var answer = await new ChatEngine(config, ai, search).AskAsync(question, new(), null, default);
        Assert.Equal(reply, answer.Text);
        Assert.False(search.Called);
        Assert.Empty(answer.Sources);
        Assert.Equal(2, ai.Calls);
    }
    [Fact]
    public async Task GreetingNeedsNoAiTokens()
    {
        var ai = new FakeAi("must not be called");
        var search = new RecordingSearch();
        var answer = await new ChatEngine(new(), ai, search).AskAsync("היי", new(), null, default);
        Assert.Contains("היי", answer.Text);
        Assert.Equal(0, ai.Calls);
        Assert.False(search.Called);
    }

    [Fact]
    public async Task FollowUpContextReachesPlannerAndResolvedQueryReachesSearch()
    {
        var chat = new Conversation();
        chat.Messages.Add(new("user", "מה התהליך לקבלת VPN?", DateTimeOffset.Now, []));
        var search = new RecordingSearch();
        var ai = new FakeAi("{\"action\":\"search\",\"query\":\"VPN approval duration זמן אישור\"}");
        await new ChatEngine(new(), ai, search).AskAsync("כמה זמן זה לוקח?", chat, null, default);
        Assert.Contains("VPN", ai.Input);
        Assert.True(search.Called);
        Assert.Contains("VPN approval duration", search.Query);
    }

    [Theory]
    [InlineData("היי")]
    [InlineData("hello!")]
    [InlineData("תודה רבה")]
    public async Task UnavailableAiDoesNotSearchForGreeting(string question)
    {
        var search = new RecordingSearch();
        var answer = await new ChatEngine(new(), new FakeAi("invalid response"), search).AskAsync(question, new(), null, default);
        Assert.False(search.Called);
        Assert.DoesNotContain("לא נמצא מידע", answer.Text);
    }

    [Fact]
    public async Task ExplicitDocumentRequestStillSearchesWhenAiUnavailable()
    {
        var search = new RecordingSearch();
        await new ChatEngine(new(), new FakeAi("invalid response"), search).AskAsync("חפש מסמך על סיסמה", new(), null, default);
        Assert.True(search.Called);
    }

    [Fact]
    public void OnlyActualUserMessagesMakeConversationPersistable()
    {
        var empty = new Conversation();
        Assert.False(empty.HasUserMessages);
        empty.Messages.Add(new("assistant", "שלום", DateTimeOffset.Now, []));
        empty.Messages.Add(new("user", "   ", DateTimeOffset.Now, []));
        Assert.False(empty.HasUserMessages);
        empty.Messages.Add(new("user", "היי", DateTimeOffset.Now, []));
        Assert.True(empty.HasUserMessages);
        Assert.Equal("היי", empty.Title);
    }

    private sealed class FakeAi(string result) : IAiClient
    {
        public int Calls; public string Input = "";
        public Task<string> CompleteAsync(string instruction, string input, CancellationToken cancellation)
        { Calls++; Input = input; return Task.FromResult(result); }
    }
    private sealed class RecordingSearch : IDocumentSearch
    {
        public bool Called; public string Query = "";
        public Task RefreshAsync(bool force, IProgress<string>? progress, CancellationToken cancellation) => Task.CompletedTask;
        public Task<List<Passage>> SearchAsync(string query, CancellationToken cancellation)
        { Called = true; Query = query; return Task.FromResult(new List<Passage>()); }
    }
}

