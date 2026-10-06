using Knowledge.Core;
using Xunit;
using System.IO.Compression;
namespace Knowledge.Tests;
public sealed class ChatTypeTests
{
    [Fact]
    public void PathsValidateDeduplicateAndPreserveUnc()
    {
        Assert.Equal(2, ChatType.NormalizePaths([@"\\server\share\docs", @"\\SERVER\share\docs\", @"D:\docs"]).Count);
        Assert.Throws<InvalidDataException>(() => ChatType.NormalizePaths(["relative/path"]));
        Assert.Throws<InvalidDataException>(() => ChatType.NormalizePaths(["https://example.com"]));
    }
    [Fact]
    public async Task MultipleFoldersAreSearchedAndOtherChatTypesStaySeparate()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        try
        {
            foreach (var name in new[] { "a", "b", "c" })
            {
                Directory.CreateDirectory(Path.Combine(root, name));
                using var zip = ZipFile.Open(Path.Combine(root, name, name + ".docx"), ZipArchiveMode.Create);
                using var writer = new StreamWriter(zip.CreateEntry("word/document.xml").Open());
                writer.Write("<document><p><r><t>unique" + name + " password</t></r></p></document>");
            }
            var type = new ChatType { Paths = [Path.Combine(root, "a"), Path.Combine(root, "b")] };
            var index = new DocumentIndex(type.Apply(new()));
            await index.RefreshAsync(false, null, default);
            Assert.Equal(2, (await index.ListAsync(default)).Count);
            Assert.Single(await index.SearchAsync("uniquea", default));
            Assert.Single(await index.SearchAsync("uniqueb", default));
            Assert.Empty(await index.SearchAsync("uniquec", default));
            var other = new DocumentIndex(new ChatType { Paths = [Path.Combine(root, "c")] }.Apply(new()));
            await other.RefreshAsync(false, null, default);
            Assert.Empty(await other.SearchAsync("uniquea", default));
            var chat = new Conversation { Messages = [new("assistant", "old answer", DateTimeOffset.Now, [new(Path.Combine(root, "c", "c.docx"), "section")])] };
            ChatType.RedactOutsideFolders(chat, type.Apply(new()).Documents);
            Assert.Empty(chat.Messages[0].Sources);
            Assert.DoesNotContain("old answer", chat.Messages[0].Text);
        }
        finally { Directory.Delete(root, true); }
    }
    [Fact]
    public async Task SingleDocumentCanBeASource()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        try
        {
            Directory.CreateDirectory(root);
            File.WriteAllText(Path.Combine(root, "chosen.txt"), "uniquechosen vacation policy");
            File.WriteAllText(Path.Combine(root, "sibling.txt"), "uniquesibling vacation policy");
            var type = new ChatType { Paths = ChatType.NormalizePaths([Path.Combine(root, "chosen.txt")]) };
            var index = new DocumentIndex(type.Apply(new()));
            await index.RefreshAsync(false, null, default);
            Assert.Single(await index.ListAsync(default));
            Assert.Single(await index.SearchAsync("uniquechosen", default));
            Assert.Empty(await index.SearchAsync("uniquesibling", default));
            var chat = new Conversation { Messages = [
                new("assistant", "kept", DateTimeOffset.Now, [new(Path.Combine(root, "chosen.txt"), "section")]),
                new("assistant", "removed", DateTimeOffset.Now, [new(Path.Combine(root, "sibling.txt"), "section")])] };
            ChatType.RedactOutsideFolders(chat, type.Apply(new()).Documents);
            Assert.Equal("kept", chat.Messages[0].Text);
            Assert.Empty(chat.Messages[1].Sources);
        }
        finally { Directory.Delete(root, true); }
    }
}
