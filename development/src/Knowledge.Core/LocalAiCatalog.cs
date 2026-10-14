using System.Text.Json;

namespace Knowledge.Core;

// Deployment configuration, deliberately separate from user preferences and credentials.
// The models come from %APPDATA%\config.json (AiConfigFile); see development/AI-INSTRUCTIONS.md.
public sealed class LocalAiCatalog
{
    public bool SearchSubfolders { get; set; } = true;
    /// <summary>Search/evidence limits (built-in defaults).</summary>
    public DocumentLimits Documents { get; set; } = new();
    /// <summary>Conversation-memory limits (built-in defaults).</summary>
    public HistoryOptions History { get; set; } = new();
    /// <summary>The models of %APPDATA%\config.json; empty until that file exists and is valid.</summary>
    public List<AiOptions> Models { get; set; } = [];
    /// <summary>apiKey values from the file, by model id. Usually empty: the user types the key in the app.</summary>
    [System.Text.Json.Serialization.JsonIgnore] public Dictionary<string, string> Keys { get; } = new(StringComparer.Ordinal);
    /// <summary>Why there are no models (no file, unreadable or invalid JSON), shown under the empty model picker.</summary>
    [System.Text.Json.Serialization.JsonIgnore] public string? Problem { get; init; }
    /// <summary>There is no request budget: calls go out until the server itself answers 429.</summary>
    public static LocalAiCatalog Load() => AiConfigFile.Load();
    public void Check(string origin)
    {
        if (Models.Count == 0 || Models.Any(m => string.IsNullOrWhiteSpace(m.Model)) ||
            Models.Select(m => m.Model).Distinct(StringComparer.Ordinal).Count() != Models.Count)
            throw new InvalidDataException($"{origin}: Models must contain unique model names.");
        foreach (var model in Models)
        {
            try { Apply(new Config(), model.Model).Validate(); }
            catch (InvalidDataException e) { throw new InvalidDataException($"{origin}, model \"{model.Model}\": {e.Message}"); }
        }
    }
    /// <summary>The model an answer mode uses: "מהיר" prefers one without thinking (it answers at once), "מעמיק" one with
    /// thinking; only models on the chosen model's server, which share its key. Otherwise, and in "רגיל", the chosen model.
    /// A code model is the last choice for fast answers to general questions.</summary>
    public string ModelFor(string chosen, AnswerMode mode)
    {
        var model = Models.FirstOrDefault(m => m.Model == chosen);
        if (model == null || mode == AnswerMode.Normal || model.Thinking == (mode == AnswerMode.Deep)) return chosen;
        static string Server(AiOptions m) => Uri.TryCreate(m.Endpoint, UriKind.Absolute, out var uri) ? uri.GetLeftPart(UriPartial.Authority) : m.Endpoint;
        return Models.Where(m => m.Thinking == (mode == AnswerMode.Deep) && Server(m) == Server(model) && m.Format == model.Format)
            .OrderBy(m => m.Model.Contains("code", StringComparison.OrdinalIgnoreCase)).FirstOrDefault()?.Model ?? chosen;
    }
    public Config Apply(Config current, string? modelName)
    {
        var value = JsonSerializer.Deserialize<Config>(JsonSerializer.Serialize(current))!;
        var model = Models.FirstOrDefault(m => m.Model == modelName) ?? Models.FirstOrDefault();
        if (model != null)
        {
            value.Ai = JsonSerializer.Deserialize<AiOptions>(JsonSerializer.Serialize(model))!;
            if (Keys.TryGetValue(model.Model, out var key)) value.Ai.EnvironmentFileKey = key;
        }
        // bearer (default), header (custom header such as api-key), windows (integrated auth) or none (open internal server).
        if (string.IsNullOrWhiteSpace(value.Ai.Authentication)) value.Ai.Authentication = "bearer";
        value.Ai.SecretEnvironmentVariable = "WINDOWSCHAT_LOCAL_API_KEY";
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
