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
    public Action<TimeSpan>? WaitingForRateLimit { get; set; }
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
        if (a.RequestsPerMinute > 0) await AiRequestGate.Local.WaitAsync(a.RequestsPerMinute, cancellation, WaitingForRateLimit);
        Interlocked.Increment(ref attemptedRequests);
        using var response = await http.SendAsync(request, cancellation);
        if (!response.IsSuccessStatusCode)
        {
            if ((int)response.StatusCode == 429 && a.RequestsPerMinute > 0)
            {
                var retry = response.Headers.RetryAfter;
                var wait = retry?.Delta ?? (retry?.Date - DateTimeOffset.UtcNow) ?? TimeSpan.FromSeconds(61);
                await AiRequestGate.Local.BackOffAsync(wait > TimeSpan.FromSeconds(61) ? wait : TimeSpan.FromSeconds(61));
            }
            // Only map known error codes. Never expose the response body, which may echo credentials.
            string? code = null;
            try
            {
                using var failure = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellation));
                if (failure.RootElement.TryGetProperty("error", out var error) && error.TryGetProperty("code", out var errorCode) && errorCode.ValueKind == JsonValueKind.String) code = errorCode.GetString();
            }
            catch (JsonException) { }
            var reason = ((int)response.StatusCode, code) switch
            {
                (>= 300 and < 400, _) => "השרת החזיר הפניה לכתובת אחרת. הפניות חסומות; הגדירו את כתובת ה-API הסופית בהגדרות (ai.config.json).",
                (401, _) => "האימות נדחה. בדקו את המפתח או את הרשאות המשתמש לשירות ה-AI.",
                (403, _) => "אין הרשאה לבקשה או למודל שהוגדר.",
                (404, _) => "המודל או כתובת ה-API לא נמצאו. בדקו את ההגדרות.",
                (429, "insufficient_quota") => "מכסת ה-API או התקציב בחשבון אזלו. בדקו חיוב ויתרה בחשבון ה-API.",
                (429, _) => "חריגה ממגבלת הבקשות. המתינו מעט ובדקו מגבלות שימוש.",
                (400, _) => "השרת דחה את מבנה הבקשה. בדקו מודל ופורמט API.",
                (>= 500, _) => "תקלה זמנית בשירות ה-AI. נסו שוב בהמשך.",
                _ => "הבקשה נדחתה. בדקו כתובת, הרשאות ומודל."
            };
            throw new InvalidOperationException($"HTTP {(int)response.StatusCode}: {reason}");
        }
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellation));
        Interlocked.Increment(ref successfulRequests);
        var usedInput = UsageReader.Read(json.RootElement, string.IsNullOrWhiteSpace(a.InputTokensPath) ? (a.Format == "responses" ? "usage.input_tokens" : "usage.prompt_tokens") : a.InputTokensPath);
        var usedOutput = UsageReader.Read(json.RootElement, string.IsNullOrWhiteSpace(a.OutputTokensPath) ? (a.Format == "responses" ? "usage.output_tokens" : "usage.completion_tokens") : a.OutputTokensPath);
        if (usedInput.HasValue && usedOutput.HasValue)
        {
            Interlocked.Add(ref inputTokens, usedInput.Value); Interlocked.Add(ref outputTokens, usedOutput.Value);
            Interlocked.Increment(ref usageReports);
        }
        return a.Format == "responses" ? string.Join("\n", json.RootElement.GetProperty("output").EnumerateArray().Where(x => x.TryGetProperty("content", out _)).SelectMany(x => x.GetProperty("content").EnumerateArray()).Where(x => x.GetProperty("type").GetString() == "output_text").Select(x => x.GetProperty("text").GetString())) : json.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ?? "";
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


