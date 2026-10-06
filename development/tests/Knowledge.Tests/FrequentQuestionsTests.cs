using Knowledge.Core;
using Xunit;
namespace Knowledge.Tests;
public sealed class FrequentQuestionsTests
{
    [Fact]
    public void UsesRepeatedUserQuestionsAndNormalizesPunctuation()
    {
        var chat = new Conversation { Messages = [
            new("user", "איך מאפסים סיסמה?", DateTimeOffset.Now, []),
            new("user", "איך מאפסים סיסמה", DateTimeOffset.Now, []),
            new("assistant", "איך מבקשים חופשה?", DateTimeOffset.Now, []),
            new("user", "איך מבקשים חופשה?", DateTimeOffset.Now, []),
            new("user", "תודה רבה", DateTimeOffset.Now, [])] };
        Assert.Single(FrequentQuestions.Select([chat]));
        Assert.Contains("סיסמה", FrequentQuestions.Select([chat])[0]);
        Assert.Empty(FrequentQuestions.Select([]));
        chat.Messages.Clear();
        Assert.Empty(FrequentQuestions.Select([chat]));
    }
}
