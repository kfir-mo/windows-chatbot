using System.Text.Json;

namespace Knowledge.Core;

// Deployment configuration, deliberately separate from user preferences and credentials.
// Everything an administrator may tune lives in ai.config.json beside Chaty.exe; see development/AI-INSTRUCTIONS.md.
public sealed class LocalAiCatalog
{
    public const int MaxRequestsPerMinute = 600;
    /// <summary>Shared budget across all models; counts planning, review, failures and connection tests.</summary>
    public int RequestsPerMinute { get; set; } = 18;
    public bool SearchSubfolders { get; set; } = true;
    /// <summary>Optional search/evidence limits. Omitted values keep the built-in defaults.</summary>
    public DocumentLimits Documents { get; set; } = new();
    /// <summary>Optional conversation-memory limits. Omitted values keep the built-in defaults.</summary>
    public HistoryOptions History { get; set; } = new();
    public List<AiOptions> Models { get; set; } = [new()];
    public static string FilePath => Path.Combine(Path.GetDirectoryName(Environment.ProcessPath)!, "ai.config.json");
    public static LocalAiCatalog Load(string? path = null)
    {
        var catalog = JsonSerializer.Deserialize<LocalAiCatalog>(File.ReadAllText(path ?? FilePath),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true, ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) ?? throw new InvalidDataException("ai.config.json is empty.");
        if (catalog.Models.Count == 0 || catalog.Models.Any(m => string.IsNullOrWhiteSpace(m.Model)) ||
            catalog.Models.Select(m => m.Model).Distinct(StringComparer.Ordinal).Count() != catalog.Models.Count)
            throw new InvalidDataException("ai.config.json: Models must contain unique model names.");
        if (catalog.RequestsPerMinute is < 1 or > MaxRequestsPerMinute)
            throw new InvalidDataException($"ai.config.json: RequestsPerMinute must be between 1 and {MaxRequestsPerMinute}.");
        foreach (var model in catalog.Models)
        {
            try { catalog.Apply(new Config(), model.Model).Validate(); }
            catch (InvalidDataException e) { throw new InvalidDataException($"ai.config.json, model \"{model.Model}\": {e.Message}"); }
        }
        return catalog;
    }
    public Config Apply(Config current, string? modelName)
    {
        var value = JsonSerializer.Deserialize<Config>(JsonSerializer.Serialize(current))!;
        var model = Models.FirstOrDefault(m => m.Model == modelName) ?? Models[0];
        value.Ai = JsonSerializer.Deserialize<AiOptions>(JsonSerializer.Serialize(model))!;
        // bearer (default), header (custom header such as api-key), windows (integrated auth) or none (open internal server).
        if (string.IsNullOrWhiteSpace(value.Ai.Authentication)) value.Ai.Authentication = "bearer";
        value.Ai.SecretEnvironmentVariable = "WINDOWSCHAT_LOCAL_API_KEY";
        value.Ai.RequestsPerMinute = RequestsPerMinute;
        value.Gateway.Endpoint = "";
        value.Documents.SearchSubfolders = SearchSubfolders;
        Documents.ApplyTo(value.Documents);
        value.History = JsonSerializer.Deserialize<HistoryOptions>(JsonSerializer.Serialize(History))!;
        return value;
    }
}
/// <summary>The tunable part of <see cref="DocumentOptions"/>; folders come from each chat type, not from here.</summary>
public sealed class DocumentLimits
{
    public int ChunkCharacters { get; set; } = 1800;
    public int MaxFileMegabytes { get; set; } = 50;
    public int MaxPassages { get; set; } = 8;
    public int EvidenceCharacters { get; set; } = 4800;
    public int ExcerptCharacters { get; set; } = 1000;
    public void ApplyTo(DocumentOptions target)
    {
        target.ChunkCharacters = ChunkCharacters; target.MaxFileMegabytes = MaxFileMegabytes; target.MaxPassages = MaxPassages;
        target.EvidenceCharacters = EvidenceCharacters; target.ExcerptCharacters = ExcerptCharacters;
    }
}
