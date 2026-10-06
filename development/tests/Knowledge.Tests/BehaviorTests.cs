using Knowledge.Core;
using System.IO.Compression;
using Xunit;
namespace Knowledge.Tests;
public sealed class BehaviorTests : IDisposable
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), "ChatTests-" + Guid.NewGuid());
    public BehaviorTests() => Directory.CreateDirectory(dir);
    private string Doc(string name, string content)
    {
        var path = Path.Combine(dir, name);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        using var writer = new StreamWriter(zip.CreateEntry("word/document.xml").Open());
        writer.Write("<w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"><w:body><w:p><w:r><w:t>" + content + "</w:t></w:r></w:p></w:body></w:document>");
        return path;
    }
    private Config Configuration() => new() { Documents = new() { Root = dir } };
    [Fact]
    public void CitationMustExist()
    {
        var path = Doc("source.docx", "hello");
        var p = new List<Passage> { new(path, "heading", "hello") };
        Assert.Empty(ChatEngine.Attribute("invented", [2], p).Sources);
        Assert.Single(ChatEngine.Attribute("supported", [1], p).Sources);
        File.Delete(path);
        Assert.Empty(ChatEngine.Attribute("cached", [1], p).Sources);
    }
    [Fact]
    public async Task SearchTracksChangesDeletionAndSubfolders()
    {
        var c = Configuration();
        var sub = Directory.CreateDirectory(Path.Combine(dir, "nested"));
        var path = Doc("first.docx", "איפוס סיסמה password");
        File.Move(path, Path.Combine(sub.FullName, "first.docx"));
        path = Path.Combine(sub.FullName, "first.docx");
        var index = new DocumentIndex(c);
        await index.RefreshAsync(false, null, default);
        Assert.Single(await index.SearchAsync("סיסמה", default));
        Assert.Empty(await index.SearchAsync("חופשה", default));
        File.Delete(path);
        Assert.Empty(await index.SearchAsync("סיסמה", default));
        await index.RefreshAsync(false, null, default);
        Assert.Empty(await index.SearchAsync("סיסמה", default));
        Doc("second.docx", "חופשה annual leave");
        await index.RefreshAsync(false, null, default);
        Assert.Single(await index.SearchAsync("leave", default));
    }
    [Fact]
    public async Task LockedSourcesAreNeverReturned()
    {
        var c = Configuration();
        var path = Doc("locked.docx", "סיסמה");
        var index = new DocumentIndex(c);
        await index.RefreshAsync(false, null, default);
        using var locked = File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.Empty(await index.SearchAsync("סיסמה", default));
    }
    [Fact]
    public async Task RevokedReadPermissionExcludesIndexedSource()
    {
        var path = Doc("restricted.docx", "סיסמה");
        var index = new DocumentIndex(Configuration());
        await index.RefreshAsync(false, null, default);
        var file = new FileInfo(path);
        var original = System.IO.FileSystemAclExtensions.GetAccessControl(file);
        var restricted = System.IO.FileSystemAclExtensions.GetAccessControl(file);
        var sid = System.Security.Principal.WindowsIdentity.GetCurrent().User!;
        restricted.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(sid,
            System.Security.AccessControl.FileSystemRights.ReadData, System.Security.AccessControl.AccessControlType.Deny));
        try
        {
            System.IO.FileSystemAclExtensions.SetAccessControl(file, restricted);
            Assert.False(Access.CanRead(path));
            Assert.Empty(await index.SearchAsync("סיסמה", default));
            Assert.Empty(await index.ListAsync(default));
            Assert.Empty(await index.ReadAsync(path, default));
        }
        finally { System.IO.FileSystemAclExtensions.SetAccessControl(file, original); }
    }
    [Fact]
    public async Task CanceledRefreshKeepsPreviousSearchableSnapshot()
    {
        for (int i = 0; i < 20; i++) Doc($"cancel-{i}.docx", "סיסמה " + i);
        var index = new DocumentIndex(Configuration());
        await index.RefreshAsync(false, null, default);
        using var cancellation = new CancellationTokenSource();
        var progress = new ImmediateProgress(() => cancellation.Cancel());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => index.RefreshAsync(true, progress, cancellation.Token));
        Assert.Equal(20, (await index.ListAsync(default)).Count);
        Assert.NotEmpty(await index.SearchAsync("סיסמה", default));
    }
    private sealed class ImmediateProgress(Action action) : IProgress<string>
    { public void Report(string value) => action(); }
    [Fact]
    public void RejectExternalEndpointWhenHostNotAllowed()
    {
        var c = Configuration();
        c.Ai.AllowedHosts = ["internal.example"];
        Assert.Throws<InvalidDataException>(c.Validate);
    }
    [Fact]
    public async Task ChangedDocumentReplacesCachedText()
    {
        var c = Configuration();
        var path = Doc("changed.docx", "password original");
        var index = new DocumentIndex(c);
        await index.RefreshAsync(false, null, default);
        File.Delete(path);
        Doc("changed.docx", "vacation replacement content");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(2));
        Assert.Empty(await index.SearchAsync("password", default));
        await index.RefreshAsync(false, null, default);
        Assert.Empty(await index.SearchAsync("password", default));
        Assert.Single(await index.SearchAsync("vacation", default));
    }

    [Fact]
    public void SlideCitationsFollowPresentationOrder()
    {
        var path = Path.Combine(dir, "reordered.pptx");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            void Write(string name, string text)
            {
                using var writer = new StreamWriter(zip.CreateEntry(name).Open());
                writer.Write(text);
            }
            Write("ppt/presentation.xml", "<p:presentation xmlns:p='urn:p' xmlns:r='urn:r'><p:sldIdLst><p:sldId id='256' r:id='second'/><p:sldId id='257' r:id='first'/></p:sldIdLst></p:presentation>");
            Write("ppt/_rels/presentation.xml.rels", "<Relationships><Relationship Id='first' Target='slides/slide1.xml'/><Relationship Id='second' Target='slides/slide2.xml'/></Relationships>");
            Write("ppt/slides/slide1.xml", "<slide><p><t>original first</t></p></slide>");
            Write("ppt/slides/slide2.xml", "<slide><p><t>moved second</t></p></slide>");
        }
        var passages = DocumentIndex.Extract(path, 1800, default);
        Assert.Equal("שקופית 1", passages[0].Location);
        Assert.Equal("moved second", passages[0].Text);
    }

    public void Dispose() => Directory.Delete(dir, true);
    private sealed class FakeAi(string result) : IAiClient
    {
        public Task<string> CompleteAsync(string instruction, string input, CancellationToken cancellation) => Task.FromResult(result);
    }
    private sealed class FakeSearch : IDocumentSearch
    {
        public bool Called; public Task RefreshAsync(bool force, IProgress<string>? p, CancellationToken c) => Task.CompletedTask; public Task<List<Passage>> SearchAsync(string query, CancellationToken c)
        {
            Called = true;
            return Task.FromResult(new List<Passage>());
        }
    }
}

