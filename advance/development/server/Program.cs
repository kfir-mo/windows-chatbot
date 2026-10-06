using System.Collections.Concurrent;
using System.Net;
using System.Security.Claims;
using System.Security.Principal;
using System.Text.Json;
using Knowledge.Core;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Negotiate;
using WindowsChat.Server;

var defaultHome=File.Exists(Path.Combine(AppContext.BaseDirectory,"settings.json"))?AppContext.BaseDirectory:Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,".."));
var root=Path.GetFullPath(Environment.GetEnvironmentVariable("WINDOWSCHAT_SERVER_HOME") ?? defaultHome);
var store=new SettingsStore(root);
var initial=store.Read(); SettingsStore.Validate(initial);
var listen=new Uri(initial.Url);
if(initial.LocalDevelopment && (!listen.IsLoopback || listen.Scheme!="http"))throw new InvalidOperationException("LocalDevelopment requires a loopback HTTP URL.");
if(!initial.LocalDevelopment && (listen.Scheme!="https" || initial.AdminGroups.Length==0 || initial.AiGroups.Length==0))throw new InvalidOperationException("Production requires HTTPS, AdminGroups and AiGroups.");
var env=DotEnv.Read(Path.Combine(root,".env"));
var builder=WebApplication.CreateBuilder(new WebApplicationOptions { Args=args, ContentRootPath=root, WebRootPath=Path.Combine(AppContext.BaseDirectory,"wwwroot") });
builder.Logging.ClearProviders(); builder.Logging.AddSimpleConsole(o=>o.SingleLine=true);
builder.Logging.AddFilter("Microsoft.AspNetCore",LogLevel.Warning);
builder.WebHost.UseUrls(initial.Url);
builder.WebHost.ConfigureKestrel(options=>{
    options.Limits.MaxRequestBodySize=256*1024;
    if(!initial.LocalDevelopment)options.ConfigureHttpsDefaults(https=>https.ServerCertificate=new System.Security.Cryptography.X509Certificates.X509Certificate2(env.GetValueOrDefault("SERVER_CERTIFICATE_PATH",initial.CertificatePath),env.GetValueOrDefault("SERVER_CERTIFICATE_PASSWORD")));
});
if(!initial.LocalDevelopment) builder.Services.AddAuthentication(NegotiateDefaults.AuthenticationScheme).AddNegotiate();
builder.Services.AddAuthorization();
builder.Services.AddSingleton<Func<Config,AiClient>>(_=>config=>new AiClient(config));
builder.Services.AddAntiforgery(o=>{o.HeaderName="X-CSRF-Token";o.Cookie.SecurePolicy=initial.LocalDevelopment?CookieSecurePolicy.SameAsRequest:CookieSecurePolicy.Always;});
var audit=new AuditStore(Path.Combine(root,"data"));
builder.Services.AddSingleton(audit);builder.Services.AddSingleton(store);builder.Services.AddHostedService<ExcelWorker>();
var app=builder.Build();
var indexes=new ConcurrentDictionary<string,(DocumentIndex Index,DateTime Refreshed)>();
var indexGate=new SemaphoreSlim(1,1);
var requests=new SemaphoreSlim(8,8);
var users=new ConcurrentDictionary<string,SemaphoreSlim>();
if(!initial.LocalDevelopment) app.UseAuthentication();
app.Use(async(context,next)=>{
    context.Response.Headers.CacheControl="no-store";
    context.Response.Headers["X-Content-Type-Options"]="nosniff";
    context.Response.Headers["X-Frame-Options"]="DENY";
    context.Response.Headers["Content-Security-Policy"]="default-src 'self'; script-src 'self'; style-src 'self'; frame-ancestors 'none'; base-uri 'none'";
    if(!context.Request.Host.Host.Equals(listen.Host,StringComparison.OrdinalIgnoreCase)){context.Response.StatusCode=400;return;}
    var settings=store.Read();
    if(initial.LocalDevelopment)
    {
        if(context.Connection.RemoteIpAddress is not { } ip || !IPAddress.IsLoopback(ip)){context.Response.StatusCode=403;return;}
        context.User=new ClaimsPrincipal(new ClaimsIdentity(new[]{new Claim(ClaimTypes.Name,WindowsIdentity.GetCurrent().Name)}.Concat(settings.AiGroups.Concat(settings.AdminGroups).Concat(settings.PersonalGroups).Select(g=>new Claim(ClaimTypes.Role,g))),"LocalDevelopment"));
    }
    if(context.User.Identity?.IsAuthenticated!=true){await context.ChallengeAsync();return;}
    var adminPath=context.Request.Path.StartsWithSegments("/api/admin") || context.Request.Path=="/" || context.Request.Path.StartsWithSegments("/admin.js") || context.Request.Path.StartsWithSegments("/admin.css");
    if(adminPath ? !Permissions.InGroups(context.User,settings.AdminGroups) : !Permissions.InGroups(context.User,settings.AiGroups)){context.Response.StatusCode=403;return;}
    if(HttpMethods.IsPost(context.Request.Method)||HttpMethods.IsPut(context.Request.Method)||HttpMethods.IsDelete(context.Request.Method))
    {
        var origin=context.Request.Headers.Origin.ToString();
        if(origin.Length>0 && origin!=listen.GetLeftPart(UriPartial.Authority)){context.Response.StatusCode=403;return;}
        if(!context.Request.HasJsonContentType()){context.Response.StatusCode=415;return;}
        if(adminPath)
        {try{await context.RequestServices.GetRequiredService<IAntiforgery>().ValidateRequestAsync(context);}catch(AntiforgeryValidationException){context.Response.StatusCode=403;return;}}
        else if(context.Request.Headers["X-WindowsChat"]!="1"){context.Response.StatusCode=403;return;}
    }
    try {await next(context);} catch(OperationCanceledException) when(context.RequestAborted.IsCancellationRequested){} catch(Exception error) {app.Logger.LogError("Request failed: {Type} at {Stack}",error.GetType().Name,error.StackTrace);if(!context.Response.HasStarted){context.Response.StatusCode=500;await context.Response.WriteAsJsonAsync(new {error="הפעולה נכשלה. בדקו הגדרות שרת והרשאות."});}}
});
app.MapGet("/",()=>Results.File(Path.Combine(AppContext.BaseDirectory,"wwwroot","index.html"),"text/html; charset=utf-8"));
app.MapGet("/admin.js",()=>Results.File(Path.Combine(AppContext.BaseDirectory,"wwwroot","admin.js"),"text/javascript; charset=utf-8"));
app.MapGet("/admin.css",()=>Results.File(Path.Combine(AppContext.BaseDirectory,"wwwroot","admin.css"),"text/css; charset=utf-8"));
app.MapGet("/api/session",(HttpContext c)=>{var s=store.Read();return new LoginResult(c.User.Identity!.Name!,Permissions.InGroups(c.User,s.PersonalGroups),Permissions.Catalog(s,c.User));});
app.MapGet("/api/admin/state",(HttpContext c,IAntiforgery antiforgery)=>new {settings=store.Read(),user=c.User.Identity!.Name,localDevelopment=initial.LocalDevelopment,csrf=antiforgery.GetAndStoreTokens(c).RequestToken});
app.MapPut("/api/admin/settings",(ServerSettings value)=>{var current=store.Read();value.Url=current.Url;value.LocalDevelopment=current.LocalDevelopment;value.CertificatePath=current.CertificatePath;store.Save(value);audit.MarkAllDirty();indexes.Clear();return Results.Ok();});
app.MapPost("/api/admin/preview",(PreviewRequest request)=>{
    var user=new ClaimsPrincipal(new ClaimsIdentity(request.Groups.Select(g=>new Claim(ClaimTypes.Role,g)),"Preview"));
    var s=store.Read();return new {allowed=Permissions.InGroups(user,s.AiGroups),personal=Permissions.InGroups(user,s.AiGroups)&&Permissions.InGroups(user,s.PersonalGroups),catalog=Permissions.Catalog(s,user)};
});
var providerGate=new object();
app.MapGet("/api/admin/provider",()=>{
    var c=new Config();var values=DotEnv.Read(Path.Combine(root,".env"));DotEnv.Apply(c,values);
    return new {c.Ai.Endpoint,c.Ai.Model,c.Ai.Format,c.Ai.Authentication,c.Ai.HeaderName,c.Ai.AllowedHosts,c.Ai.InputTokensPath,c.Ai.OutputTokensPath,hasKey=!string.IsNullOrWhiteSpace(c.Ai.EnvironmentFileKey)};
});
app.MapPut("/api/admin/provider",(ProviderUpdate value)=>{
    var c=new Config();DotEnv.Apply(c,DotEnv.Read(Path.Combine(root,".env")));
    c.Ai.Endpoint=value.Endpoint;c.Ai.Model=value.Model;c.Ai.Format=value.Format;c.Ai.Authentication=value.Authentication;c.Ai.HeaderName=value.HeaderName;c.Ai.AllowedHosts=value.AllowedHosts;c.Ai.InputTokensPath=value.InputTokensPath;c.Ai.OutputTokensPath=value.OutputTokensPath;
    c.Validate();
    if(value.Key.Length>4096||value.Key.Any(char.IsWhiteSpace))return Results.BadRequest();
    lock(providerGate)
    {
        var path=Path.Combine(root,".env");
        foreach(var pair in new Dictionary<string,string>{{"AI_ENDPOINT",value.Endpoint},{"AI_MODEL",value.Model},{"AI_FORMAT",value.Format},{"AI_AUTHENTICATION",value.Authentication},{"AI_HEADER_NAME",value.HeaderName},{"AI_ALLOWED_HOSTS",string.Join(',',value.AllowedHosts)},{"AI_INPUT_TOKENS_PATH",value.InputTokensPath},{"AI_OUTPUT_TOKENS_PATH",value.OutputTokensPath}})DotEnv.SetValue(path,pair.Key,pair.Value);
        if(value.Key.Length>0)DotEnv.SetValue(path,"AI_API_KEY",value.Key);
    }
    return Results.Ok();
});
app.MapGet("/api/admin/logs",(string? from,string? to)=>audit.Read(ParseDate(from,DateTime.UtcNow.AddDays(-7)),ParseDate(to,DateTime.UtcNow)).Take(200));
app.MapGet("/api/admin/export",(string? from,string? to)=>Results.File(AuditStore.Excel(audit.Read(ParseDate(from,DateTime.UtcNow.AddDays(-7)),ParseDate(to,DateTime.UtcNow))),"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet","usage.xlsx"));
app.MapGet("/api/admin/dashboard",(string? period)=>new[]{"today","week","month","all"}.Contains(period??"today")?Results.Ok(audit.Dashboard(period??"today",store.Read().TimeZoneId)):Results.BadRequest());
app.MapGet("/api/admin/users",()=>audit.AllUsers());
app.MapGet("/api/admin/log-path",(string typeId)=>{
    var s=store.Read();var a=s.Assistants.FirstOrDefault(a=>"org:"+a.Id==typeId);
    if(a==null&&typeId!="builtin:general")return Results.NotFound();
    var path=audit.LogPath(s,typeId,a?.Name??"כללי",a?.Project??"כללי");return Results.Ok(new{path,exists=File.Exists(path)});
});
app.MapPost("/api/sources/refresh", async (RefreshRequest request, HttpContext context) => {
    var settings = store.Read();
    var profile = settings.Assistants.FirstOrDefault(a => "org:" + a.Id == request.TypeId && a.Enabled);
    if (profile == null || !Permissions.InGroups(context.User, profile.Groups)) return Results.StatusCode(403);
    if (!await indexGate.WaitAsync(0, context.RequestAborted)) return Results.StatusCode(429);
    try {
        var config = profile.ToChatType().Apply(Config.Load(root));
        var key = profile.Id + ":" + string.Join("|", profile.Paths);
        var index = indexes.TryGetValue(key, out var entry) ? entry.Index : new DocumentIndex(config);
        await index.RefreshAsync(false, null, context.RequestAborted);
        if (indexes.Count > 16) indexes.Clear();
        indexes[key] = (index, DateTime.UtcNow);
        return Results.Ok();
    } finally { indexGate.Release(); }
});
app.MapPost("/api/chat",async (TurnRequest turn,HttpContext context)=>{
    var s=store.Read();var name=context.User.Identity!.Name!;
    var managed=turn.TypeId.StartsWith("org:",StringComparison.Ordinal);
    var profile=managed?s.Assistants.FirstOrDefault(a=>"org:"+a.Id==turn.TypeId&&a.Enabled):null;
    if(managed && (profile==null||!Permissions.InGroups(context.User,profile.Groups)))return Results.StatusCode(403);
    if(!managed&&turn.TypeId!=ChatType.GeneralId&&!Permissions.InGroups(context.User,s.PersonalGroups))return Results.StatusCode(403);
    if(string.IsNullOrWhiteSpace(turn.Question)||turn.DisplayName.Length>80||turn.TypeId.Length>160||turn.Question.Length>6000||turn.Prompt.Length>6000||turn.Conversation.Messages.Count>40||turn.Conversation.Messages.Any(m=>m.Text.Length>20000||m.Sources.Count>40)||turn.Evidence.Count>30||turn.Evidence.Sum(p=>p.Text.Length)>30000||turn.Documents.Count>40)return Results.BadRequest();
    var userGate=users.GetOrAdd(name,_=>new SemaphoreSlim(1,1));
    if(!await userGate.WaitAsync(0,context.RequestAborted))return Results.StatusCode(429);
    if(!await requests.WaitAsync(0,context.RequestAborted)){userGate.Release();return Results.StatusCode(429);}
    var id=Guid.NewGuid();var time=DateTimeOffset.UtcNow;AiClient? ai=null;string answer="";var status="failed";string model="";
    var label=profile?.Name ?? (turn.TypeId==ChatType.GeneralId?"כללי":string.IsNullOrWhiteSpace(turn.DisplayName)?"עוזר אישי":turn.DisplayName);
    var auditType=managed||turn.TypeId==ChatType.GeneralId?turn.TypeId:"personal:"+name+":"+turn.TypeId;
    var project=profile?.Project??(turn.TypeId==ChatType.GeneralId?"כללי":"אישי");
    try
    {
        audit.Append(new(id,time,name,label,turn.Question,"","started","",0,null,null,auditType,project));
        var config=Config.Load(root); config.Gateway.Endpoint="";model=config.Ai.Model;ai=context.RequestServices.GetRequiredService<Func<Config,AiClient>>()(config);
        IDocumentSearch search;
        if(profile!=null)
        {
            config=profile.ToChatType().Apply(config);
            await indexGate.WaitAsync(context.RequestAborted);
            try
            {
                var key=profile.Id+":"+string.Join("|",profile.Paths);
                if(!indexes.TryGetValue(key,out var entry)||turn.Conversation.Messages.Count(m=>m.Role=="user")<=1||entry.Refreshed<DateTime.UtcNow.AddMinutes(-config.Documents.IntervalMinutes))
                {var index=entry.Index??new DocumentIndex(config);await index.RefreshAsync(false,null,context.RequestAborted);entry=(index,DateTime.UtcNow);if(indexes.Count>16)indexes.Clear();indexes[key]=entry;}
                search=entry.Index;
            }finally{indexGate.Release();}
        }
        else
        {
            config.GeneralChat=turn.TypeId==ChatType.GeneralId||!turn.HasSources;
            config.AssistantPrompt=turn.TypeId==ChatType.GeneralId?"":turn.Prompt;
            search=new SubmittedEvidence(turn);
        }
        var conversation=turn.Conversation;
        // Previous source IDs are metadata only; the search implementation enforces readable evidence.
        var engine=new ChatEngine(config,ai,search);
        ChatMessage result;
        if(profile!=null&&!initial.LocalDevelopment)
        {
            if(context.User.Identity is not WindowsIdentity identity)return Results.StatusCode(403);
            result=await WindowsIdentity.RunImpersonatedAsync(identity.AccessToken,()=>engine.AskAsync(turn.Question,conversation,null,context.RequestAborted));
        }
        else result=await engine.AskAsync(turn.Question,conversation,null,context.RequestAborted);
        answer=result.Text;status="completed";
        return Results.Ok(result);
    }
    finally
    {
        try
        {
            var known=ai!=null&&ai.UsageReports==ai.AttemptedRequests;
            long? input=known?ai!.InputTokens:null,output=known?ai!.OutputTokens:null;
            audit.Append(new(id,time,name,label,turn.Question,answer,status,model,ai?.AttemptedRequests??0,input,output,auditType,project));
        }finally{ai?.Dispose();requests.Release();userGate.Release();}
    }
});
app.Run();
static DateOnly ParseDate(string? value,DateTime fallback)=>DateOnly.TryParse(value,out var result)?result:DateOnly.FromDateTime(fallback);
public record PreviewRequest(string[] Groups);
public partial class Program { }

public record ProviderUpdate(string Endpoint,string Model,string Format,string Authentication,string HeaderName,string[] AllowedHosts,string InputTokensPath,string OutputTokensPath,string Key);






public record RefreshRequest(string TypeId);
