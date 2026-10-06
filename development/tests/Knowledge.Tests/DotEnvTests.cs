using Knowledge.Core;
using Xunit;

namespace Knowledge.Tests;
public sealed class DotEnvTests
{
    [Fact]
    public void UnquotedUncPathPreservesWindowsSeparators()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, @"DOCUMENTS_ROOT=\\server\share\guides" + "\nAI_USE_PROXY=false");
            var config = new Config();
            DotEnv.Apply(config, DotEnv.Read(path));
            Assert.Equal(@"\\server\share\guides", config.Documents.Root);
            Assert.False(config.Ai.UseProxy);
        }
        finally { File.Delete(path); }
    }
    [Fact]
    public void EnvironmentFileOverridesEndpointAndAllOptionTypes()
    {
        var config = new Config();
        DotEnv.Apply(config, new Dictionary<string, string>
        {
            ["AI_ENDPOINT"] = "https://private.example/v1/chat/completions",
            ["AI_ALLOWED_HOSTS"] = "private.example, backup.example",
            ["AI_MODEL"] = "internal-model",
            ["AI_API_KEY"] = "fictional-key",
            ["DOCUMENTS_ROOT"] = "D:/test-documents",
            ["AI_TIMEOUT_SECONDS"] = "45",
            ["UI_START_EXPANDED"] = "true"
        });
        config.Validate();
        Assert.Equal("private.example", new Uri(config.Ai.Endpoint).Host);
        Assert.Equal("internal-model", config.Ai.Model);
        Assert.Equal(45, config.Ai.TimeoutSeconds);
        Assert.Equal("fictional-key", AiCredentials.Resolve(config.Ai));
        Assert.True(config.Ui.StartExpanded);
        Assert.DoesNotContain("fictional-key", System.Text.Json.JsonSerializer.Serialize(config));
    }

    [Fact]
    public void ParsingAndUpdatingPreserveUnrelatedSettings()
    {
        var path = Path.Combine(Path.GetTempPath(), "chat-env-" + Guid.NewGuid());
        try
        {
            File.WriteAllText(path, "# Settings\nAI_MODEL=gpt-4o-mini\nAI_API_KEY=\"fictional=#key\"\nDOCUMENTS_ROOT='D:/test docs'\n");
            var values = DotEnv.Read(path);
            Assert.Equal("fictional=#key", values["AI_API_KEY"]);
            Assert.Equal("D:/test docs", values["DOCUMENTS_ROOT"]);
            DotEnv.SetValue(path, "AI_API_KEY", "replacement");
            Assert.Equal("replacement", DotEnv.Read(path)["AI_API_KEY"]);
            Assert.Equal("gpt-4o-mini", DotEnv.Read(path)["AI_MODEL"]);
        }
        finally { File.Delete(path); }
    }
}

