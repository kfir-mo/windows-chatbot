using Knowledge.Core;

// Only emits status. Never print credentials, environment contents, or raw server error bodies.
var config = Config.Load(Path.GetFullPath(args.Length > 0 ? args[0] : "publish/Chat"));
if (config.Ai.Authentication is "bearer" or "header" && string.IsNullOrWhiteSpace(AiCredentials.Resolve(config.Ai)))
{
    Console.WriteLine("No API key is available in this app's .env or Windows environment. Live test not sent.");
    return 2;
}
try
{
    using var ai = new AiClient(config);
    var reply = await ai.TestConnectionAsync(null, default);
    if (string.IsNullOrWhiteSpace(reply)) throw new InvalidDataException();
    Console.WriteLine("Live AI request succeeded and returned text.");
    if (args.Contains("--conversation"))
    {
        var planner = new RequestPlanner(ai);
        var greeting = await planner.PlanAsync("היי", "", default);
        Console.WriteLine($"Greeting decision: search={greeting.SearchKnowledge}, replyPresent={!string.IsNullOrWhiteSpace(greeting.Reply)}");
        if (greeting.SearchKnowledge || string.IsNullOrWhiteSpace(greeting.Reply)) throw new InvalidDataException();
        var policy = await planner.PlanAsync("מה נוהל בקשת חופשה בחברה שלנו?", "", default);
        Console.WriteLine($"Policy decision: search={policy.SearchKnowledge}");
        if (!policy.SearchKnowledge) throw new InvalidDataException();
        Console.WriteLine("Live conversation routing passed: Hebrew greeting replies directly; company policy requests document search.");
    }
    if (args.Contains("--scenarios"))
    {
        config.Documents.Root = Path.GetFullPath("samples/test-documents");

        var index = new DocumentIndex(config);
        await index.RefreshAsync(false, null, default);
        var engine = new ChatEngine(config, ai, index);
        var conversation = new Conversation();
        foreach (var question in new[] { "היי", "איך מאפסים סיסמה?", "מה יש במאגר מידע", "הראשון" })
        {
            var beforeCalls = ai.SuccessfulRequests; var beforeInput = ai.InputTokens; var beforeOutput = ai.OutputTokens;
            conversation.Messages.Add(new("user", question, DateTimeOffset.Now, []));
            var answer = await engine.AskAsync(question, conversation, null, default);
            conversation.Messages.Add(answer);
            Console.WriteLine($"Scenario: {question}\n{answer.Text}\nSources: {answer.Sources.Count}");
            Console.WriteLine($"AI calls: {ai.SuccessfulRequests - beforeCalls}; reported input tokens: {ai.InputTokens - beforeInput}; output tokens: {ai.OutputTokens - beforeOutput}");
            if (question != "היי" && answer.Sources.Count == 0) throw new InvalidDataException("Scenario missing evidence.");
        }
    }
    return 0;
}
catch (Exception error)
{
    Console.WriteLine(AiErrors.Describe(error));
    return 1;
}

