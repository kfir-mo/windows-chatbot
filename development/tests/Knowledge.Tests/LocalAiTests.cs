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
        var catalog = new LocalAiCatalog { Models = [new() { Model = "internal", Endpoint = "https://internal.example/v1/chat/completions", AllowedHosts = ["internal.example"], Authentication = "none", SystemPrompt = "Internal policy", MaxOutputTokens = 1200 }] };
        var current = new Config();
        var result = catalog.Apply(current, "removed-model"); result.Validate();
        Assert.Equal("internal", result.Ai.Model);
        Assert.Equal("https://internal.example/v1/chat/completions", result.Ai.Endpoint);
        Assert.Equal("none", result.Ai.Authentication);
        Assert.Equal("bearer", new LocalAiCatalog().Apply(current, null).Ai.Authentication);
        Assert.Equal("Internal policy", result.Ai.SystemPrompt);
        Assert.Equal("gpt-4o-mini", current.Ai.Model);
    }
    private const string ClosedNetwork = """
        [
          {
            "name": "gaia", "vendor": "customendpoint", "apiKey": "fictional-network-key", "apiType": "chat-completions",
            "models": [
              { "id": "GAIA-chat", "name": "GAIA-chat", "url": "https://ai.corp/v1/chat/completions", "toolCalling": true, "vision": true, "thinking": true, "maxInputTokens": 128000, "maxOutputTokens": 16000 },
              { "id": "GAIA-code", "name": "GAIA-code", "url": "http://ai.corp:4000/v1", "toolCalling": true, "vision": false, "maxInputTokens": 64000, "maxOutputTokens": 8000 },
              { "id": "GAIA-mini", "name": "GAIA-mini", "url": "https://mini.corp", "toolCalling": false, "vision": false, "thinking": false, "maxInputTokens": 32000, "maxOutputTokens": 4000 },
            ]
          }
        ]
        """;
    [Fact]
    public void ConfigFileBecomesTheModelList()
    {
        var catalog = AiConfigFile.Parse(ClosedNetwork);
        var models = catalog.Models;
        Assert.Equal(["GAIA-chat", "GAIA-code", "GAIA-mini"], models.Select(m => m.Model));
        Assert.Equal("https://ai.corp/v1/chat/completions", models[0].Endpoint);
        // A base address gets the API path; an http url is allowed for that host only.
        Assert.Equal("http://ai.corp:4000/v1/chat/completions", models[1].Endpoint);
        Assert.True(models[1].AllowHttp); Assert.False(models[0].AllowHttp);
        Assert.Equal("https://mini.corp/chat/completions", models[2].Endpoint);
        Assert.True(models[0].Thinking); Assert.False(models[1].Thinking); // "thinking" is missing on the code model
        Assert.Equal(16000, models[0].MaxOutputTokens); Assert.Equal(64000, models[1].MaxInputTokens);
        var config = catalog.Apply(new Config(), "GAIA-code"); config.Validate();
        Assert.Equal("max_tokens", config.Ai.MaxTokensField); Assert.Equal("bearer", config.Ai.Authentication);
        // A key in the file is used as is, without being saved anywhere.
        Assert.Equal("fictional-network-key", AiCredentials.Resolve(config.Ai));
        // OpenAI itself rejects max_tokens for reasoning models.
        Assert.Equal("max_completion_tokens", AiConfigFile.Parse("""[ { "apiKey": "", "models": [ { "id": "o4-mini", "url": "https://api.openai.com/v1/chat/completions", "thinking": true } ] } ]""").Models[0].MaxTokensField);
    }
    [Fact]
    public void AnswerModesPickAThinkingOrAQuickModelOnTheSameServer()
    {
        var catalog = AiConfigFile.Parse("""
            [ { "apiKey": "", "models": [
              { "id": "think", "url": "https://ai.corp/v1", "thinking": true },
              { "id": "coder", "url": "https://ai.corp/v1" },
              { "id": "quick", "url": "https://ai.corp/v1" },
              { "id": "other", "url": "https://elsewhere.corp/v1" } ] } ]
            """);
        Assert.Equal("think", catalog.ModelFor("think", AnswerMode.Normal));
        Assert.Equal("quick", catalog.ModelFor("think", AnswerMode.Fast)); // a code model is the last choice
        Assert.Equal("think", catalog.ModelFor("quick", AnswerMode.Deep));
        Assert.Equal("quick", catalog.ModelFor("quick", AnswerMode.Fast));
        // Another server has another key: its model is never borrowed.
        Assert.Equal("other", catalog.ModelFor("other", AnswerMode.Deep));
    }
    [Fact]
    public void BrokenConfigFileExplainsTheProblem()
    {
        Assert.Contains("שורה", Assert.Throws<InvalidDataException>(() => AiConfigFile.Parse("[ { \"name\": \"x\", ")).Message);
        Assert.Contains("models", Assert.Throws<InvalidDataException>(() => AiConfigFile.Parse("""[ { "name": "x" } ]""")).Message);
        Assert.Throws<InvalidDataException>(() => AiConfigFile.Parse("""[ { "models": [ { "id": "a", "url": "ftp://x" } ] } ]"""));
        Assert.Throws<InvalidDataException>(() => AiConfigFile.Parse("""[ { "models": [ { "id": "a", "url": "https://x" }, { "id": "a", "url": "https://y" } ] } ]"""));
    }
    [Fact]
    public void MissingOrBrokenFileMeansNoModelsAndAReason()
    {
        var file = Path.Combine(Path.GetTempPath(), "chaty-config-" + Guid.NewGuid() + ".json");
        var before = AiConfigFile.Path;
        try
        {
            AiConfigFile.Path = file;
            var missing = LocalAiCatalog.Load();
            Assert.Empty(missing.Models); Assert.Contains("לא נמצא", missing.Problem);
            File.WriteAllText(file, "[ { ");
            var broken = LocalAiCatalog.Load();
            Assert.Empty(broken.Models); Assert.Contains("שורה", broken.Problem);
            File.WriteAllText(file, ClosedNetwork);
            var loaded = LocalAiCatalog.Load();
            Assert.Equal(3, loaded.Models.Count); Assert.Null(loaded.Problem);
        }
        finally { AiConfigFile.Path = before; File.Delete(file); }
    }
}
