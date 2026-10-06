using System.Net.Http.Json;
namespace Knowledge.Core;
public sealed class GatewayOptions
{
    public string Endpoint { get; set; } = "";
}
public record CatalogEntry(string Id, string Name, string Project, string Category);
public record LoginResult(string User, bool PersonalAllowed, List<CatalogEntry> Catalog);
public record TurnRequest(string TypeId, string Question, Conversation Conversation, string Prompt, bool HasSources, List<Passage> Evidence, List<string> Documents, string DisplayName = "");
public sealed class GatewayClient : IDisposable
{
    private readonly HttpClient http;
    public GatewayClient(GatewayOptions settings)
    {
        if (!Uri.TryCreate(settings.Endpoint, UriKind.Absolute, out var uri) || !string.IsNullOrEmpty(uri.UserInfo) || (uri.Scheme != "https" && !(uri.Scheme == "http" && uri.IsLoopback)))
            throw new InvalidDataException("הגדירו GATEWAY_ENDPOINT לכתובת HTTPS של השרת הארגוני.");
        http = new(new HttpClientHandler { UseDefaultCredentials = true, AllowAutoRedirect = false, UseProxy = false }) { BaseAddress = new Uri(settings.Endpoint.TrimEnd('/') + "/"), Timeout = TimeSpan.FromMinutes(5) };
        http.DefaultRequestHeaders.Add("X-WindowsChat", "1");
    }
    public async Task RefreshAsync(string typeId, CancellationToken ct)
    {
        using var response = await http.PostAsJsonAsync("api/sources/refresh", new { typeId }, ct);
        await Check(response, ct);
    }
    public async Task<LoginResult> LoginAsync(CancellationToken ct)
    {
        using var response = await http.GetAsync("api/session", ct);
        await Check(response, ct);
        return (await response.Content.ReadFromJsonAsync<LoginResult>(cancellationToken: ct))!;
    }
    public async Task<ChatMessage> AskAsync(TurnRequest turn, CancellationToken ct)
    {
        using var response = await http.PostAsJsonAsync("api/chat", turn, ct);
        await Check(response, ct);
        return (await response.Content.ReadFromJsonAsync<ChatMessage>(cancellationToken: ct))!;
    }
    private static Task Check(HttpResponseMessage response, CancellationToken ct)
    {
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException((int)response.StatusCode switch {
            401 => "ההזדהות הארגונית נכשלה. התחברו עם חשבון הדומיין שלכם.",
            403 => "אין לכם הרשאה לשימוש ב־AI או לעוזר שנבחר. פנו למנהל המערכת.",
            429 => "השרת עמוס. נסו שוב בעוד רגע.",
            _ => "השרת לא הצליח להשלים את הבקשה. פנו למנהל המערכת." });
        return Task.CompletedTask;
    }
    public void Dispose() => http.Dispose();
}

