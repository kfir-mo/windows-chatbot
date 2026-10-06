using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using ClosedXML.Excel;
using Knowledge.Core;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using WindowsChat.Server;
using Xunit;
[assembly: CollectionBehavior(DisableTestParallelization=true)]
namespace Server.Tests;
public class SecurityTests
{
    [Fact]
    public void AiMembershipAndTopicMembershipAreBothRequired()
    {
        var settings=new ServerSettings{AiGroups=["AI"],Assistants=[new(){Id="a",Name="private",Groups=["PROJECT"]}]};
        ClaimsPrincipal User(params string[] groups)=>new(new ClaimsIdentity(groups.Select(g=>new Claim(ClaimTypes.Role,g)),"test"));
        Assert.Empty(Permissions.Catalog(settings,User("PROJECT")));
        Assert.Empty(Permissions.Catalog(settings,User("AI")));
        Assert.Single(Permissions.Catalog(settings,User("AI","PROJECT")));
    }
    [Fact]
    public void UsageMappingSupportsBothOpenAiFormatsAndCustomPaths()
    {
        using var doc=JsonDocument.Parse("""{"usage":{"prompt_tokens":12,"completion_tokens":4,"input_tokens":10,"output_tokens":2},"private":{"meter":[{"tokens":9}]},"bad":-1}""");
        Assert.Equal(12,UsageReader.Read(doc.RootElement,"usage.prompt_tokens"));
        Assert.Equal(4,UsageReader.Read(doc.RootElement,"usage.completion_tokens"));
        Assert.Equal(10,UsageReader.Read(doc.RootElement,"usage.input_tokens"));
        Assert.Equal(2,UsageReader.Read(doc.RootElement,"usage.output_tokens"));
        Assert.Equal(9,UsageReader.Read(doc.RootElement,"/private/meter/0/tokens"));
        Assert.Null(UsageReader.Read(doc.RootElement,"missing")); Assert.Null(UsageReader.Read(doc.RootElement,"bad"));
    }
    [Fact]
    public void ExcelHasUsageSummaryAndNeverTreatsUserTextAsFormula()
    {
        var row=new AuditRow(Guid.NewGuid(),DateTimeOffset.UtcNow,"user","assistant","=WEBSERVICE(\"https://invalid\")","answer","completed","model",1,null,null);
        using var workbook=new XLWorkbook(new MemoryStream(AuditStore.Excel([row])));
        Assert.Equal(2,workbook.Worksheets.Count);
        Assert.False(workbook.Worksheet(1).Cell(2,3).HasFormula);
        Assert.Equal("לא דווח",workbook.Worksheet(1).Cell(2,5).GetString());
        Assert.Equal(1,workbook.Worksheet(2).Cell(2,4).GetValue<int>());
    }
    [Fact]
    public async Task ServerAuthorizesEveryRequestAndAuditsActualModelUsage()
    {
        var root=Path.Combine(Path.GetTempPath(),"server-tests-"+Guid.NewGuid());Directory.CreateDirectory(root);
        var previous=Environment.GetEnvironmentVariable("WINDOWSCHAT_SERVER_HOME");
        try
        {
            var settings=new ServerSettings{AiGroups=["AI"],AdminGroups=["ADMIN"],PersonalGroups=["PERSONAL"],Assistants=[new(){Id="secret",Name="Secret",Prompt="hidden-prompt",Groups=["RESTRICTED"]}]};
            var store=new SettingsStore(root);store.Save(settings);
            File.WriteAllText(Path.Combine(root,"config.json"),"{}");File.WriteAllText(Path.Combine(root,".env"),"AI_API_KEY=server-only-fictional-key");
            Environment.SetEnvironmentVariable("WINDOWSCHAT_SERVER_HOME",root);
            var handler=new FakeAiHandler();
            using var factory=new WebApplicationFactory<Program>().WithWebHostBuilder(b=>b.ConfigureServices(s=>{s.AddSingleton<Func<Config,AiClient>>(_=>c=>new AiClient(c,handler));s.AddSingleton<Microsoft.AspNetCore.Hosting.IStartupFilter,LoopbackFilter>();}));
            using var client=factory.CreateClient(new(){BaseAddress=new Uri(settings.Url),AllowAutoRedirect=false});
            var sessionResponse=await client.GetAsync("/api/session"); Assert.True(sessionResponse.IsSuccessStatusCode,await sessionResponse.Content.ReadAsStringAsync()); var session=await sessionResponse.Content.ReadFromJsonAsync<LoginResult>();Assert.Empty(session!.Catalog);
            var raw=await client.GetStringAsync("/api/session");Assert.DoesNotContain("hidden-prompt",raw);Assert.DoesNotContain("server-only",raw);
            var turn=new TurnRequest(ChatType.GeneralId,"hello",new(),"",false,[],[]);
            Assert.Equal(HttpStatusCode.Forbidden,(await client.PostAsJsonAsync("/api/chat",turn)).StatusCode);
            client.DefaultRequestHeaders.Add("X-WindowsChat","1");
            Assert.Equal(HttpStatusCode.Forbidden,(await client.PostAsJsonAsync("/api/chat",turn with{TypeId="org:secret"})).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden,(await client.PostAsJsonAsync("/api/sources/refresh",new { typeId="org:secret" })).StatusCode);
            settings.Assistants.Add(new(){Id="allowed",Name="Allowed",Groups=["AI"],Paths=[root]});store.Save(settings);
            Assert.Equal(HttpStatusCode.OK,(await client.PostAsJsonAsync("/api/sources/refresh",new { typeId="org:allowed" })).StatusCode);
            Assert.Equal(0,handler.Calls);
            var answer=await client.PostAsJsonAsync("/api/chat",turn);Assert.Equal(HttpStatusCode.OK,answer.StatusCode);
            Assert.Equal("Hello",(await answer.Content.ReadFromJsonAsync<ChatMessage>())!.Text);
            var audit=new AuditStore(Path.Combine(root,"data")).Read(DateOnly.FromDateTime(DateTime.UtcNow),DateOnly.FromDateTime(DateTime.UtcNow));
            Assert.Single(audit);Assert.Equal(12,audit[0].InputTokens);Assert.Equal(4,audit[0].OutputTokens);Assert.Equal("hello",audit[0].Question);Assert.Equal("Hello",audit[0].Answer);
            Assert.Equal(HttpStatusCode.Forbidden,(await client.PutAsJsonAsync("/api/admin/settings",settings)).StatusCode);
            settings.AiGroups=[];store.Save(settings);
            Assert.Equal(HttpStatusCode.Forbidden,(await client.PostAsJsonAsync("/api/chat",turn)).StatusCode);Assert.Equal(1,handler.Calls);
            settings.AdminGroups=[];store.Save(settings);
            Assert.Equal(HttpStatusCode.Forbidden,(await client.GetAsync("/api/admin/state")).StatusCode);
        }
        finally{Environment.SetEnvironmentVariable("WINDOWSCHAT_SERVER_HOME",previous);Directory.Delete(root,true);}
    }
    private sealed class LoopbackFilter : Microsoft.AspNetCore.Hosting.IStartupFilter
    {
        public Action<Microsoft.AspNetCore.Builder.IApplicationBuilder> Configure(Action<Microsoft.AspNetCore.Builder.IApplicationBuilder> next) => app => { Microsoft.AspNetCore.Builder.UseExtensions.Use(app, async (context, continuation) => { context.Connection.RemoteIpAddress = IPAddress.Loopback; await continuation(); }); next(app); };
    }
    private sealed class FakeAiHandler : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
        { Calls++; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent("""{"choices":[{"message":{"content":"Hello"}}],"usage":{"prompt_tokens":12,"completion_tokens":4}}""",System.Text.Encoding.UTF8,"application/json")}); }
    }
}


