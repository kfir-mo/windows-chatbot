using Knowledge.Core;
namespace WindowsChat.Server;
// Personal evidence is provided by the authenticated user's client, never read as a server path.
public sealed class SubmittedEvidence(TurnRequest turn) : IDocumentSearch
{
    public bool HasStrongMatch(string query) => RetrievalText.HasStrongMatch(query, turn.Evidence);
    public bool CanRead(string path) => turn.Evidence.Any(p=>p.Path.Equals(path,StringComparison.OrdinalIgnoreCase)) || turn.Documents.Contains(path,StringComparer.OrdinalIgnoreCase);
    public Task RefreshAsync(bool force,IProgress<string>? progress,CancellationToken ct) => Task.CompletedTask;
    public Task<List<Passage>> SearchAsync(string query,CancellationToken ct) => Task.FromResult(turn.Evidence);
    public Task<List<Passage>> ReadAsync(string path,string query,CancellationToken ct) => Task.FromResult(turn.Evidence.Where(p=>p.Path.Equals(path,StringComparison.OrdinalIgnoreCase)).ToList());
    public Task<List<string>> ListAsync(CancellationToken ct) => Task.FromResult(turn.Documents);
}
