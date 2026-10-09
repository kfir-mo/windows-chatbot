using System.Text.Json;
using System.Text.Json.Nodes;

namespace Knowledge.Core;

/// <summary>The AI configuration: <c>%APPDATA%\config.json</c>, the closed network's endpoint JSON — a list of providers,
/// each with an apiKey and its models (<c>[{ "name", "vendor", "apiKey", "apiType", "models": [{ "id", "name", "url",
/// "toolCalling", "vision", "thinking", "maxInputTokens", "maxOutputTokens" }] }]</c>). It is the only source of models, so a
/// PC needs nothing but Chaty.exe and this file. It is read at startup and whenever "הגדרות AI" opens.</summary>
public static class AiConfigFile
{
    /// <summary>Tests point this elsewhere; the app uses %APPDATA%\config.json.</summary>
    public static string Path { get; set; } = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "config.json");

    /// <summary>The models in the file, or an empty catalog whose <see cref="LocalAiCatalog.Problem"/> says why.</summary>
    public static LocalAiCatalog Load()
    {
        string text;
        try
        {
            if (!File.Exists(Path)) return new() { Problem = "לא נמצא קובץ ההגדרות של ה־AI. יש לשמור אותו בנתיב:" };
            text = File.ReadAllText(Path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return new() { Problem = "לא ניתן לקרוא את קובץ ההגדרות של ה־AI:" }; }
        try { return Parse(text); }
        catch (InvalidDataException e) { return new() { Problem = "קובץ ההגדרות של ה־AI אינו תקין: " + e.Message }; }
    }

    public static LocalAiCatalog Parse(string json)
    {
        JsonNode? root;
        try { root = JsonNode.Parse(json, documentOptions: new() { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip }); }
        catch (JsonException e) { throw new InvalidDataException($"ה־JSON אינו תקין (שורה {e.LineNumber + 1}). בדקו פסיקים, מירכאות וסוגריים."); }
        List<JsonNode?> providers = root switch
        {
            JsonArray list => list.ToList(),
            JsonObject single => [single],
            _ => throw new InvalidDataException("הקובץ צריך להכיל רשימה של ספקים: [ { \"name\": …, \"models\": [ … ] } ].")
        };
        var catalog = new LocalAiCatalog();
        foreach (var node in providers)
        {
            if (node is not JsonObject provider) throw new InvalidDataException("כל ספק ברשימה צריך להיות אובייקט { … }.");
            var label = Text(provider, "name") ?? "ספק";
            var format = (Text(provider, "apiType") ?? "chat-completions") switch
            {
                "chat-completions" or "chat" or "openai" => "chat-completions",
                "responses" => "responses",
                var other => throw new InvalidDataException($"{label}: apiType \"{other}\" אינו נתמך. השתמשו ב־chat-completions.")
            };
            var key = Text(provider, "apiKey") ?? "";
            if (key.Any(char.IsWhiteSpace)) throw new InvalidDataException($"{label}: ה־apiKey מכיל רווחים.");
            if (provider["models"] is not JsonArray list || list.Count == 0) throw new InvalidDataException($"{label}: חסרה רשימת models.");
            foreach (var item in list)
            {
                if (item is not JsonObject model) throw new InvalidDataException($"{label}: כל מודל צריך להיות אובייקט {{ … }}.");
                // The id is what the server expects in "model"; in this network it equals the name.
                var id = Text(model, "id") ?? Text(model, "name") ?? throw new InvalidDataException($"{label}: למודל חסר id.");
                var url = Text(model, "url") ?? Text(provider, "url") ?? throw new InvalidDataException($"{id}: חסרה כתובת url.");
                var endpoint = Endpoint(url, format) ?? throw new InvalidDataException($"{id}: הכתובת \"{url}\" אינה כתובת http/https מלאה.");
                var thinking = Flag(model, "thinking");
                var output = Number(model, "maxOutputTokens");
                catalog.Models.Add(new()
                {
                    Model = id, Endpoint = endpoint.AbsoluteUri, AllowedHosts = [endpoint.Host], AllowHttp = endpoint.Scheme == "http", Format = format,
                    Authentication = "bearer", MaxOutputTokens = output > 0 ? output : 4096, MaxInputTokens = Math.Max(0, Number(model, "maxInputTokens")),
                    Thinking = thinking, Vision = Flag(model, "vision"),
                    // Internal OpenAI-compatible servers (vLLM, LiteLLM) take max_tokens; OpenAI itself rejects it for reasoning models.
                    MaxTokensField = format == "responses" ? "" : endpoint.Host.Equals("api.openai.com", StringComparison.OrdinalIgnoreCase) ? "max_completion_tokens" : "max_tokens",
                    SendStoreFalse = false,
                    // A reasoning model thinks before it answers, so it gets longer before a request counts as stuck.
                    TimeoutSeconds = thinking ? 300 : 120
                });
                if (key.Length > 0) catalog.Keys[id] = key;
            }
        }
        if (catalog.Models.Select(m => m.Model).Distinct(StringComparer.Ordinal).Count() != catalog.Models.Count)
            throw new InvalidDataException("שני מודלים באותו id. לכל מודל צריך id אחר.");
        catalog.Check("config.json");
        return catalog;
    }

    /// <summary>A model url may be the full endpoint or the server's base address (…/v1); the API path is added when missing.</summary>
    private static Uri? Endpoint(string url, string format)
    {
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(uri.UserInfo)) return null;
        var suffix = format == "responses" ? "/responses" : "/chat/completions";
        var path = uri.AbsolutePath.TrimEnd('/');
        return path.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) ? uri : new UriBuilder(uri) { Path = path + suffix }.Uri;
    }
    private static string? Text(JsonObject node, string name) =>
        node[name] is JsonValue value && value.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text) && text != "*" ? text.Trim() : null;
    private static bool Flag(JsonObject node, string name) =>
        node[name] is JsonValue value && (value.TryGetValue<bool>(out var flag) ? flag : value.TryGetValue<string>(out var text) && bool.TryParse(text, out flag) && flag);
    private static int Number(JsonObject node, string name) =>
        node[name] is JsonValue value && (value.TryGetValue<int>(out var number) || value.TryGetValue<string>(out var text) && int.TryParse(text, out number)) ? number : 0;
}
