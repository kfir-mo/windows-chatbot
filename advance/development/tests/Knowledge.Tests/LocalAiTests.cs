using System.Diagnostics;
using System.Text.Json;
using Knowledge.Core;
using Xunit;

namespace Knowledge.Tests;
public class LocalAiTests
{
    [Fact]
    public void CatalogReplacesStaleUserEndpointsAndEnforcesBearerAndSharedLimit()
    {
        var catalog = new LocalAiCatalog { RequestsPerMinute = 17, Models = [new() { Model = "internal", Endpoint = "https://internal.example/v1/chat/completions", AllowedHosts = ["internal.example"], Authentication = "none", SystemPrompt = "Internal policy", MaxOutputTokens = 1200 }] };
        var current = new Config(); current.Gateway.Endpoint = "https://old.example";
        var result = catalog.Apply(current, "removed-model"); result.Validate();
        Assert.Equal("internal", result.Ai.Model);
        Assert.Equal("https://internal.example/v1/chat/completions", result.Ai.Endpoint);
        Assert.Equal("bearer", result.Ai.Authentication);
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
        await gate.WaitAsync(2, default);
        using var cancel = new CancellationTokenSource(20);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => gate.WaitAsync(2, cancel.Token));
        await Task.WhenAll(gate.WaitAsync(2, default), gate.WaitAsync(2, default));
        Assert.True(clock.ElapsedMilliseconds >= 230, "Concurrent requests must share the paced rolling window.");
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
    public void CatalogRejectsUnsafeRateAndDuplicateModels()
    {
        var file = Path.GetTempFileName();
        try
        {
            File.WriteAllText(file, JsonSerializer.Serialize(new LocalAiCatalog { RequestsPerMinute = 20 }));
            Assert.Throws<InvalidDataException>(() => LocalAiCatalog.Load(file));
            File.WriteAllText(file, JsonSerializer.Serialize(new LocalAiCatalog { Models = [new(), new()] }));
            Assert.Throws<InvalidDataException>(() => LocalAiCatalog.Load(file));
        }
        finally { File.Delete(file); }
    }
}
