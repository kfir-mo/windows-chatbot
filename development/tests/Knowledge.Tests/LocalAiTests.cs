using System.Diagnostics;
using System.Text.Json;
using Knowledge.Core;
using Xunit;

namespace Knowledge.Tests;
public class LocalAiTests
{
    [Fact]
    public void CatalogReplacesStaleUserEndpointsAndKeepsModelAuthenticationAndSharedLimit()
    {
        var catalog = new LocalAiCatalog { RequestsPerMinute = 17, Models = [new() { Model = "internal", Endpoint = "https://internal.example/v1/chat/completions", AllowedHosts = ["internal.example"], Authentication = "none", SystemPrompt = "Internal policy", MaxOutputTokens = 1200 }] };
        var current = new Config(); current.Gateway.Endpoint = "https://old.example";
        var result = catalog.Apply(current, "removed-model"); result.Validate();
        Assert.Equal("internal", result.Ai.Model);
        Assert.Equal("https://internal.example/v1/chat/completions", result.Ai.Endpoint);
        Assert.Equal("none", result.Ai.Authentication);
        Assert.Equal("bearer", new LocalAiCatalog().Apply(current, null).Ai.Authentication);
        Assert.Equal(17, result.Ai.RequestsPerMinute);
        Assert.Equal("Internal policy", result.Ai.SystemPrompt);
        Assert.Equal("", result.Gateway.Endpoint);
        Assert.Equal("gpt-4o-mini", current.Ai.Model);
    }
    [Fact]
    public async Task GatePacesConcurrentRequestsAndCancellationDoesNotConsumeSlot()
    {
        var gate = new AiRequestGate(TimeSpan.FromMilliseconds(240));
        var clock = Stopwatch.StartNew();
        await Task.WhenAll(gate.WaitAsync(2, default), gate.WaitAsync(2, default));
        using var cancel = new CancellationTokenSource(20);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => gate.WaitAsync(2, cancel.Token));
        await Task.WhenAll(gate.WaitAsync(2, default), gate.WaitAsync(2, default));
        Assert.True(clock.ElapsedMilliseconds >= 230, "Concurrent requests must share the rolling window.");
    }
    [Fact]
    public async Task GateDoesNotWaitWhileTheBudgetHasRoom()
    {
        // One question makes 3–4 calls in a row; none of them may wait (or report waiting) while the window has room.
        var gate = new AiRequestGate(TimeSpan.FromSeconds(60));
        var waited = false;
        var clock = Stopwatch.StartNew();
        for (int i = 0; i < 4; i++) await gate.WaitAsync(18, default, _ => waited = true);
        Assert.False(waited);
        Assert.True(clock.ElapsedMilliseconds < 1000);
    }
    [Fact]
    public async Task BackoffExtendsAnAlreadyWaitingRequest()
    {
        var gate = new AiRequestGate(TimeSpan.FromMilliseconds(100));
        await gate.WaitAsync(1, default);
        var pending = gate.WaitAsync(1, default);
        var clock = Stopwatch.StartNew();
        await gate.BackOffAsync(TimeSpan.FromMilliseconds(240));
        await pending;
        Assert.True(clock.ElapsedMilliseconds >= 230);
    }
    [Fact]
    public async Task GateBudgetSurvivesRecreation()
    {
        var name = "rate-test-" + Guid.NewGuid() + ".bin";
        try
        {
            await new AiRequestGate(TimeSpan.FromMilliseconds(200), name).WaitAsync(1, default);
            var clock = Stopwatch.StartNew();
            await new AiRequestGate(TimeSpan.FromMilliseconds(200), name).WaitAsync(1, default);
            Assert.True(clock.ElapsedMilliseconds >= 150);
        }
        finally { File.Delete(Path.Combine(Storage.Root, name)); }
    }
    [Fact]
    public void CatalogAppliesDeploymentLimitsAndAcceptsComments()
    {
        var file = Path.GetTempFileName();
        try
        {
            File.WriteAllText(file, """
                // Internal network example
                { "RequestsPerMinute": 120, "Documents": { "MaxPassages": 12, "EvidenceCharacters": 9000 }, "History": { "RecentMessages": 12 },
                  "Models": [ { "Model": "local-llm", "Endpoint": "http://ai.corp/v1/chat/completions", "AllowedHosts": ["ai.corp"], "AllowHttp": true, "Authentication": "none", "MaxTokensField": "max_tokens" } ], }
                """);
            var config = LocalAiCatalog.Load(file).Apply(new Config(), null);
            Assert.Equal(120, config.Ai.RequestsPerMinute); Assert.Equal(12, config.Documents.MaxPassages); Assert.Equal(9000, config.Documents.EvidenceCharacters);
            Assert.Equal(1000, config.Documents.ExcerptCharacters); Assert.Equal(12, config.History.RecentMessages); Assert.Equal("none", config.Ai.Authentication);
            File.WriteAllText(file, """{ "Documents": { "MaxPassages": 99 }, "Models": [ { "Model": "m" } ] }""");
            Assert.Throws<InvalidDataException>(() => LocalAiCatalog.Load(file));
        }
        finally { File.Delete(file); }
    }
    [Fact]
    public void CatalogRejectsUnsafeRateAndDuplicateModels()
    {
        var file = Path.GetTempFileName();
        try
        {
            File.WriteAllText(file, JsonSerializer.Serialize(new LocalAiCatalog { RequestsPerMinute = LocalAiCatalog.MaxRequestsPerMinute + 1 }));
            Assert.Throws<InvalidDataException>(() => LocalAiCatalog.Load(file));
            File.WriteAllText(file, JsonSerializer.Serialize(new LocalAiCatalog { Models = [new(), new()] }));
            Assert.Throws<InvalidDataException>(() => LocalAiCatalog.Load(file));
        }
        finally { File.Delete(file); }
    }
}
