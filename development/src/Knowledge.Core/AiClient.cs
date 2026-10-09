using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
namespace Knowledge.Core;
public interface IAiClient
{
    Task<string> CompleteAsync(string instruction, string input, CancellationToken cancellation);
}
public sealed class AiClient : IAiClient, IDisposable
{
    private readonly Config config;
    private readonly HttpClient http;
    private long inputTokens, outputTokens, successfulRequests, usageReports, attemptedRequests;
    public long AttemptedRequests => Interlocked.Read(ref attemptedRequests);
    public long UsageReports => Interlocked.Read(ref usageReports);
    public long InputTokens => Interlocked.Read(ref inputTokens);
    public long OutputTokens => Interlocked.Read(ref outputTokens);
    public long SuccessfulRequests => Interlocked.Read(ref successfulRequests);
    public AiClient(Config config, HttpMessageHandler? handler = null)
    {
        config.Validate();
        this.config = config;
        http = new(handler ?? new HttpClientHandler { AllowAutoRedirect = false, UseProxy = config.Ai.UseProxy, UseDefaultCredentials = config.Ai.Authentication == "windows" })
        {
            Timeout = TimeSpan.FromSeconds(config.Ai.TimeoutSeconds),
            MaxResponseContentBufferSize = 2 * 1024 * 1024
        };
    }
    public Task<string> CompleteAsync(string instruction, string input, CancellationToken cancellation) => CompleteCoreAsync(instruction, input, cancellation, null);
    public Task<string> TestConnectionAsync(string? key, CancellationToken cancellation) => CompleteCoreAsync("Reply with exactly OK.", "Connection test.", cancellation, key);
    private async Task<string> CompleteCoreAsync(string instruction, string input, CancellationToken cancellation, string? keyOverride)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, config.Ai.Endpoint);
        var a = config.Ai;
        if (!string.IsNullOrWhiteSpace(a.SystemPrompt)) instruction = a.SystemPrompt + "\n\n" + instruction;
        if (a.Authentication is "bearer" or "header")
        {
            var key = keyOverride ?? AiCredentials.Resolve(a);
            if (string.IsNullOrWhiteSpace(key))
                throw new InvalidOperationException("לא הוגדר מפתח API. הזינו מפתח בהגדרות ה־AI.");
            if (a.Authentication == "bearer")
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            else
                request.Headers.Add(a.HeaderName, key);
        }
        var body = new Dictionary<string, object> { ["model"] = a.Model };
        if (a.Format == "responses") { body["instructions"] = instruction; body["input"] = input; }
        else body["messages"] = new[] { new { role = "system", content = instruction }, new { role = "user", content = input } };
        if (a.SendStoreFalse) body["store"] = false;
        var limitField = a.MaxTokensField is "" ? (a.Format == "responses" ? "max_output_tokens" : "max_completion_tokens") : a.MaxTokensField;
        if (limitField != "none") body[limitField] = a.MaxOutputTokens;
        if (a.Temperature.HasValue) body["temperature"] = a.Temperature.Value;
        request.Content = JsonContent.Create(body);
        // No request budget of its own: calls go out until the server answers 429, and that answer is shown as is.
        Interlocked.Increment(ref attemptedRequests);
        using var response = await http.SendAsync(request, cancellation);
        var text = await response.Content.ReadAsStringAsync(cancellation);
        JsonDocument? parsed = null;
        try { parsed = JsonDocument.Parse(text); } catch (JsonException) when (!response.IsSuccessStatusCode) { }
        using var json = parsed;
        // Some proxies report an error inside a 200 body ({"error": {...}} without choices).
        var error = json?.RootElement is { ValueKind: JsonValueKind.Object } reply && reply.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.Object
            && !reply.TryGetProperty("choices", out _) && !reply.TryGetProperty("output", out _) ? e : (JsonElement?)null;
        if (!response.IsSuccessStatusCode || error != null) throw Failure((int)response.StatusCode, error, response.Headers.RetryAfter);
        var root = json!.RootElement;
        Interlocked.Increment(ref successfulRequests);
        var usedInput = UsageReader.Read(root, string.IsNullOrWhiteSpace(a.InputTokensPath) ? (a.Format == "responses" ? "usage.input_tokens" : "usage.prompt_tokens") : a.InputTokensPath);
        var usedOutput = UsageReader.Read(root, string.IsNullOrWhiteSpace(a.OutputTokensPath) ? (a.Format == "responses" ? "usage.output_tokens" : "usage.completion_tokens") : a.OutputTokensPath);
        if (usedInput.HasValue && usedOutput.HasValue)
        {
            Interlocked.Add(ref inputTokens, usedInput.Value); Interlocked.Add(ref outputTokens, usedOutput.Value);
            Interlocked.Increment(ref usageReports);
        }
        return a.Format == "responses" ? string.Join("\n", root.GetProperty("output").EnumerateArray().Where(x => x.TryGetProperty("content", out _)).SelectMany(x => x.GetProperty("content").EnumerateArray()).Where(x => x.GetProperty("type").GetString() == "output_text").Select(x => x.GetProperty("text").GetString())) : Answer(root);
    }
    /// <summary>The visible answer of a chat completion. Reasoning models put their thinking in reasoning_content (or a leading
    /// &lt;think&gt; block); that is never the answer. When the thinking used up max_tokens, content is null and finish_reason is "length".</summary>
    public static string Answer(JsonElement root)
    {
        var choice = root.GetProperty("choices")[0];
        var message = choice.GetProperty("message");
        var content = message.TryGetProperty("content", out var c) ? c.ValueKind switch
        {
            JsonValueKind.String => c.GetString(),
            JsonValueKind.Array => string.Concat(c.EnumerateArray().Where(p => p.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String).Select(p => p.GetProperty("text").GetString())),
            _ => null
        } : null;
        content = System.Text.RegularExpressions.Regex.Replace(content ?? "", @"^\s*<think>.*?(</think>\s*|$)", "", System.Text.RegularExpressions.RegexOptions.Singleline);
        if (!string.IsNullOrWhiteSpace(content)) return content;
        var reason = choice.TryGetProperty("finish_reason", out var f) && f.ValueKind == JsonValueKind.String ? f.GetString() : null;
        throw new InvalidOperationException(reason == "length"
            ? "המודל הגיע למגבלת אורך התשובה (maxOutputTokens) עוד לפני שכתב תשובה, כנראה בזמן החשיבה. נסו שאלה ממוקדת יותר, מודל בלי חשיבה, או maxOutputTokens גבוה יותר בהגדרת ה־JSON."
            : "המודל החזיר תשובה ריקה. נסו שוב.");
    }
    private static Exception Failure(int status, JsonElement? error, RetryConditionHeaderValue? retry)
    {
        // Only map known error codes and read numbers out of the message. Never expose the response body: it may echo the key
        // ("Rate limit exceeded for api_key: …").
        string? code = null, message = null;
        if (error is { } e)
        {
            if (e.TryGetProperty("code", out var c)) code = c.ValueKind switch { JsonValueKind.String => c.GetString(), JsonValueKind.Number => c.GetRawText(), _ => null };
            if (e.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String) message = m.GetString();
        }
        if (status is >= 200 and < 300) status = int.TryParse(code, out var reported) && reported >= 400 ? reported : 500;
        if (status == 429 && code != "insufficient_quota") return RateLimitException.From(message, retry);
        var reason = (status, code) switch
        {
            (>= 300 and < 400, _) => "השרת החזיר הפניה לכתובת אחרת. הפניות חסומות; הגדירו את כתובת ה-API הסופית בהגדרות.",
            (401, _) => "האימות נדחה. בדקו את המפתח או את הרשאות המשתמש לשירות ה-AI.",
            (403, _) => "אין הרשאה לבקשה או למודל שהוגדר.",
            (404, _) => "המודל או כתובת ה-API לא נמצאו. בדקו את ההגדרות.",
            (429, _) => "מכסת ה-API או התקציב בחשבון אזלו. בדקו חיוב ויתרה בחשבון ה-API.",
            (400, _) => "השרת דחה את מבנה הבקשה. בדקו מודל ופורמט API.",
            (>= 500, _) => "תקלה זמנית בשירות ה-AI. נסו שוב בהמשך.",
            _ => "הבקשה נדחתה. בדקו כתובת, הרשאות ומודל."
        };
        return new InvalidOperationException($"HTTP {status}: {reason}");
    }
    public void Dispose() => http.Dispose();
    public static JsonDocument ParseJson(string value)
    {
        value = value.Trim();
        if (value.StartsWith("```"))
        {
            value = value[(value.IndexOf('\n') + 1)..];
            value = value[..value.LastIndexOf("```", StringComparison.Ordinal)];
        }
        return JsonDocument.Parse(value);
    }
}


