using Knowledge.Core;
using System.Net;
using System.Text.Json;
using Xunit;

namespace Knowledge.Tests;

public sealed class ApiTests
{
    [Fact]
    public async Task RealHttpRedirectIsNotFollowed()
    {
        using var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var config = new Config();
        config.Ai.Endpoint = $"http://127.0.0.1:{port}/api";
        config.Ai.AllowedHosts = ["127.0.0.1"];
        config.Ai.AllowHttp = true;
        config.Ai.Authentication = "none";
        config.Ai.TimeoutSeconds = 3;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var server = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync(deadline.Token);
            using var stream = client.GetStream();
            using var reader = new StreamReader(stream, leaveOpen: true);
            while (!string.IsNullOrEmpty(await reader.ReadLineAsync(deadline.Token))) { }
            var response = System.Text.Encoding.ASCII.GetBytes($"HTTP/1.1 302 Found\r\nLocation: http://127.0.0.1:{port}/redirected\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(response, deadline.Token);
        });
        using var ai = new AiClient(config);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => ai.TestConnectionAsync(null, deadline.Token));
        Assert.Contains("302", error.Message);
        await server;
        Assert.False(listener.Pending());
        Assert.False(config.Ai.UseProxy);
    }
    [Theory]
    [InlineData("chat-completions", "{\"choices\":[{\"message\":{\"content\":\"שלום\"}}]}")]
    [InlineData("responses", "{\"output\":[{\"type\":\"message\",\"content\":[{\"type\":\"output_text\",\"text\":\"שלום\"}]}]}")]
    public async Task ConfiguredFormatSendsBoundedRequestAndParsesAnswer(string format, string response)
    {
        var config = new Config();
        config.Ai.Format = format;
        config.Ai.Authentication = "none";
        var handler = new StubHandler(async request =>
        {
            Assert.Equal(config.Ai.Endpoint, request.RequestUri?.AbsoluteUri);
            using var payload = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            Assert.False(payload.RootElement.GetProperty("store").GetBoolean());
            Assert.Equal(config.Ai.Model, payload.RootElement.GetProperty("model").GetString());
            Assert.Equal(config.Ai.MaxOutputTokens, payload.RootElement.GetProperty(format == "responses" ? "max_output_tokens" : "max_completion_tokens").GetInt32());
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(response) };
        });
        using var ai = new AiClient(config, handler);
        Assert.Equal("שלום", await ai.CompleteAsync("system", "user", default));
    }
    [Fact]
    public async Task StreamReportsTheTextSoFarWithoutThinking()
    {
        var config = new Config();
        config.Ai.Authentication = "none";
        var events = "data: {\"choices\":[{\"delta\":{\"content\":\"<think>plan\"}}]}\n\n" +
            "data: {\"choices\":[{\"delta\":{\"content\":\"</think>שלום\"}}]}\n\n" +
            "data: {\"choices\":[{\"delta\":{\"reasoning_content\":\"x\"}}]}\n\n" +
            "data: {\"choices\":[{\"delta\":{\"content\":\" עולם\"},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n";
        var handler = new StubHandler(async request =>
        {
            using var payload = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            Assert.True(payload.RootElement.GetProperty("stream").GetBoolean());
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(events, System.Text.Encoding.UTF8, "text/event-stream") };
        });
        using var ai = new AiClient(config, handler);
        var seen = new List<string>();
        Assert.Equal("שלום עולם", await ai.StreamAsync("system", "user", seen.Add, default));
        Assert.Equal(["שלום", "שלום עולם"], seen);
    }
    [Fact]
    public async Task AGatewayThatRefusesStreamingIsAskedAgainWithoutIt()
    {
        var config = new Config();
        config.Ai.Authentication = "none";
        config.Ai.Endpoint = "https://no-stream.example/v1/chat/completions";
        config.Ai.AllowedHosts = ["no-stream.example"];
        var streamed = 0; var plain = 0;
        var handler = new StubHandler(async request =>
        {
            using var payload = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            if (payload.RootElement.TryGetProperty("stream", out _)) { streamed++; return new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("{\"error\":{\"message\":\"unknown field stream\"}}") }; }
            plain++;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"choices\":[{\"message\":{\"content\":\"שלום\"}}]}") };
        });
        using var ai = new AiClient(config, handler);
        Assert.Equal("שלום", await ai.StreamAsync("system", "user", _ => { }, default));
        Assert.Equal("שלום", await ai.StreamAsync("system", "user", _ => { }, default));
        Assert.Equal(1, streamed); Assert.Equal(2, plain);
    }
    [Fact]
    public async Task StreamFallsBackToAPlainAnswer()
    {
        var config = new Config();
        config.Ai.Authentication = "none";
        var handler = new StubHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"choices\":[{\"message\":{\"content\":\"שלום\"}}]}") }));
        using var ai = new AiClient(config, handler);
        Assert.Equal("שלום", await ai.StreamAsync("system", "user", _ => { }, default));
    }
    [Theory]
    [InlineData("", "")]
    [InlineData("{\"answer\":\"של", "של")]
    [InlineData("{\"answer\": \"שורה\\nשנייה \\\"מצוטט\\\" \\u05D0", "שורה\nשנייה \"מצוטט\" א")]
    [InlineData("{\"answer\":\"גמור\",\"citations\":[1]}", "גמור")]
    [InlineData("{\"answer\":\"חצי \\", "חצי ")]
    public void PartialAnswerReadsTheAnswerFieldAsItIsWritten(string raw, string expected) => Assert.Equal(expected, ChatEngine.PartialAnswer(raw));
    [Fact]
    public async Task InternalServerOptionsShapeTheRequest()
    {
        var config = new Config();
        config.Ai.Authentication = "none"; config.Ai.MaxTokensField = "max_tokens"; config.Ai.SendStoreFalse = false; config.Ai.Temperature = .2;
        var handler = new StubHandler(async request =>
        {
            using var payload = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            Assert.False(payload.RootElement.TryGetProperty("store", out _));
            Assert.False(payload.RootElement.TryGetProperty("max_completion_tokens", out _));
            Assert.Equal(config.Ai.MaxOutputTokens, payload.RootElement.GetProperty("max_tokens").GetInt32());
            Assert.Equal(.2, payload.RootElement.GetProperty("temperature").GetDouble());
            Assert.Null(request.Headers.Authorization);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"choices\":[{\"message\":{\"content\":\"ok\"}}]}") };
        });
        using var ai = new AiClient(config, handler);
        Assert.Equal("ok", await ai.CompleteAsync("system", "user", default));
    }

    [Fact]
    public async Task MissingCredentialFailsBeforeNetwork()
    {
        var config = new Config();
        config.Ai.SecretEnvironmentVariable = "CHAT_TEST_MISSING_" + Guid.NewGuid();
        using var ai = new AiClient(config, new StubHandler(_ => throw new Exception("Network must not be called")));
        await Assert.ThrowsAsync<MissingKeyException>(() => ai.CompleteAsync("system", "user", default));
    }

    [Fact]
    public async Task ErrorBodyIsNotExposed()
    {
        var config = new Config();
        config.Ai.Authentication = "none";
        using var ai = new AiClient(config, new StubHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("private-error-secret") })));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => ai.CompleteAsync("system", "user", default));
        Assert.Contains("401", error.Message);
        Assert.DoesNotContain("private-error-secret", error.Message);
    }

    private sealed class StubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> reply) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => reply(request);
    }

    [Fact]
    public async Task EnvironmentFileKeyIsUsedWithoutPowerShellEnvironment()
    {
        var config = new Config();
        config.Ai.EnvironmentFileKey = "fictional-test-token";
        using var ai = new AiClient(config, new StubHandler(request =>
        {
            Assert.Equal("fictional-test-token", request.Headers.Authorization?.Parameter);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"choices\":[{\"message\":{\"content\":\"OK\"}}]}") });
        }));
        Assert.Equal("OK", await ai.TestConnectionAsync(null, default));
    }

    [Fact]
    public async Task QuotaFailureIsActionableWithoutEchoingSecrets()
    {
        var config = new Config(); config.Ai.Authentication = "none";
        using var ai = new AiClient(config, new StubHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.TooManyRequests)
        { Content = new StringContent("{\"error\":{\"code\":\"insufficient_quota\",\"message\":\"do not expose private-error-secret\"}}") })));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => ai.TestConnectionAsync(null, default));
        Assert.Contains("תקציב", error.Message);
        Assert.DoesNotContain("private-error-secret", error.Message);
    }

    private static AiClient Reply(HttpStatusCode status, string body)
    {
        var config = new Config(); config.Ai.Authentication = "none";
        return new AiClient(config, new StubHandler(_ => Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) })));
    }
    [Fact]
    public async Task ReasoningIsNeverTheAnswer()
    {
        using var ai = Reply(HttpStatusCode.OK, """
            {"id":"chatcmpl-1","object":"chat.completion","choices":[{"finish_reason":"stop","index":0,
              "message":{"role":"assistant","reasoning_content":"private thinking","content":"<think>more thinking</think>\nשלום"},
              "provider_specific_fields":{"token_ids":null}}],"usage":{"completion_tokens":5,"prompt_tokens":3,"total_tokens":8,"completion_tokens_details":{"reasoning_tokens":2}}}
            """);
        Assert.Equal("שלום", await ai.CompleteAsync("i", "q", default));
        Assert.Equal(1, ai.UsageReports);
    }
    [Fact]
    public async Task ThinkingThatUsedTheWholeBudgetIsExplained()
    {
        using var ai = Reply(HttpStatusCode.OK, """
            {"choices":[{"finish_reason":"length","index":0,"message":{"role":"assistant","reasoning_content":"long thinking","content":null}}],"usage":{"completion_tokens":16,"prompt_tokens":3,"total_tokens":19}}
            """);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => ai.CompleteAsync("i", "q", default));
        Assert.Contains("maxOutputTokens", error.Message);
        Assert.DoesNotContain("long thinking", error.Message);
    }
    private const string LimitBody = """
        {"error":{"message":"Rate limit exceeded for api_key: fictional-secret-key. Limit type: requests. Current limit: 15, Remaining: 0. Limit resets at: 2026-10-07 11:32:10 UTC","type":"None","param":"None","code":"429"}}
        """;
    [Fact]
    public async Task ServerRateLimitStopsWithTheResetTimeAndNoKey()
    {
        using var ai = Reply(HttpStatusCode.TooManyRequests, LimitBody);
        var error = await Assert.ThrowsAsync<RateLimitException>(() => ai.CompleteAsync("i", "q", default));
        Assert.Contains("15", error.Message);
        Assert.DoesNotContain("fictional-secret-key", error.Message);
        Assert.Equal(new DateTimeOffset(2026, 10, 7, 11, 32, 10, TimeSpan.Zero), error.ResetsAt);
        // The same error inside a 200 body is still the server's limit.
        using var proxied = Reply(HttpStatusCode.OK, LimitBody);
        await Assert.ThrowsAsync<RateLimitException>(() => proxied.CompleteAsync("i", "q", default));
        var soon = RateLimitException.From("Limit resets at: 2026-10-07 11:32:10 UTC", null, new DateTimeOffset(2026, 10, 7, 11, 31, 40, TimeSpan.Zero));
        Assert.Contains("30 שניות", soon.Message);
    }
    [Fact]
    public async Task ThereIsNoInternalRequestBudget()
    {
        var calls = 0;
        var config = new Config(); config.Ai.Authentication = "none";
        using var ai = new AiClient(config, new StubHandler(_ =>
        {
            calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"choices\":[{\"message\":{\"content\":\"OK\"}}]}") });
        }));
        var clock = System.Diagnostics.Stopwatch.StartNew();
        for (int i = 0; i < 40; i++) await ai.CompleteAsync("i", "q", default);
        Assert.Equal(40, calls);
        Assert.True(clock.ElapsedMilliseconds < 5000);
    }
}
