namespace Knowledge.Core;

/// <summary>One shared, paced rolling-window budget across all local models and AI client instances.</summary>
public sealed class AiRequestGate(TimeSpan? window = null, string? stateFile = null)
{
    public sealed class State
    {
        public List<DateTimeOffset> Sent { get; set; } = [];
        public DateTimeOffset BlockedUntil { get; set; }
    }
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly TimeSpan window = window ?? TimeSpan.FromSeconds(61);
    private State state = new();
    public static AiRequestGate Local { get; } = new(stateFile: "ai-request-budget.bin");
    public async Task WaitAsync(int limit, CancellationToken cancellation, Action<TimeSpan>? waiting = null)
    {
        if (limit < 1) throw new ArgumentOutOfRangeException(nameof(limit));
        while (true)
        {
            TimeSpan delay;
            await gate.WaitAsync(cancellation);
            try
            {
                if (stateFile != null) state = Storage.Load<State>(stateFile) ?? state;
                cancellation.ThrowIfCancellationRequested();
                var now = DateTimeOffset.UtcNow;
                state.Sent.RemoveAll(t => t <= now - window);
                var due = state.BlockedUntil;
                if (state.Sent.Count >= limit) due = Max(due, state.Sent[state.Sent.Count - limit] + window);
                if (state.Sent.Count > 0) due = Max(due, state.Sent[^1] + window / limit);
                if (due <= now)
                {
                    state.Sent.Add(now);
                    Save();
                    return;
                }
                delay = due - now;
            }
            finally { gate.Release(); }
            waiting?.Invoke(delay);
            await Task.Delay(delay > TimeSpan.FromMinutes(1) ? TimeSpan.FromMinutes(1) : delay, cancellation);
        }
    }
    public async Task BackOffAsync(TimeSpan delay)
    {
        await gate.WaitAsync();
        try { state.BlockedUntil = Max(state.BlockedUntil, DateTimeOffset.UtcNow + delay); Save(); }
        finally { gate.Release(); }
    }
    private void Save() { if (stateFile != null) Storage.Save(stateFile, state); }
    private static DateTimeOffset Max(DateTimeOffset a, DateTimeOffset b) => a > b ? a : b;
}
