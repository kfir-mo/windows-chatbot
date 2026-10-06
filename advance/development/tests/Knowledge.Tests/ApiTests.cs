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
    public async Task MissingCredentialFailsBeforeNetwork()
    {
        var config = new Config();
        config.Ai.SecretEnvironmentVariable = "CHAT_TEST_MISSING_" + Guid.NewGuid();
        using var ai = new AiClient(config, new StubHandler(_ => throw new Exception("Network must not be called")));
        await Assert.ThrowsAsync<InvalidOperationException>(() => ai.CompleteAsync("system", "user", default));
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
}
