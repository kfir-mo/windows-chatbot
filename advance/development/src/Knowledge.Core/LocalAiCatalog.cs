using System.Text.Json;

namespace Knowledge.Core;

// Deployment configuration, deliberately separate from user preferences and credentials.
public sealed class LocalAiCatalog
{
    public int RequestsPerMinute { get; set; } = 18;
    public bool SearchSubfolders { get; set; } = true;
    public List<AiOptions> Models { get; set; } = [new()];
    public static string FilePath => Path.Combine(Path.GetDirectoryName(Environment.ProcessPath)!, "ai.config.json");
    public static LocalAiCatalog Load(string? path = null)
    {
        var catalog = JsonSerializer.Deserialize<LocalAiCatalog>(File.ReadAllText(path ?? FilePath),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? throw new InvalidDataException("ai.config.json is empty.");
        if (catalog.Models.Count == 0 || catalog.Models.Any(m => string.IsNullOrWhiteSpace(m.Model)) ||
            catalog.Models.Select(m => m.Model).Distinct(StringComparer.Ordinal).Count() != catalog.Models.Count)
            throw new InvalidDataException("ai.config.json: Models must contain unique model names.");
        if (catalog.RequestsPerMinute is < 1 or > 18)
            throw new InvalidDataException("ai.config.json: RequestsPerMinute must be between 1 and 18.");
        foreach (var model in catalog.Models) catalog.Apply(new Config(), model.Model).Validate();
        return catalog;
    }
    public Config Apply(Config current, string? modelName)
    {
        var value = JsonSerializer.Deserialize<Config>(JsonSerializer.Serialize(current))!;
        var model = Models.FirstOrDefault(m => m.Model == modelName) ?? Models[0];
        value.Ai = JsonSerializer.Deserialize<AiOptions>(JsonSerializer.Serialize(model))!;
        value.Ai.Authentication = "bearer";
        value.Ai.SecretEnvironmentVariable = "WINDOWSCHAT_LOCAL_API_KEY";
        value.Ai.RequestsPerMinute = RequestsPerMinute;
        value.Gateway.Endpoint = "";
        value.Documents.SearchSubfolders = SearchSubfolders;
        return value;
    }
}
