namespace ShutterSpool.Application.Preview;

public sealed class PreviewAssetUseGate
{
    private readonly Lock _sync = new();
    private readonly Dictionary<Guid, State> _states = [];

    public IDisposable? TryEnter(Guid assetId)
    {
        lock (_sync)
        {
            State state = GetState(assetId);
            if (state.Fenced) return null;
            state.Active++;
            return new Lease(() => Exit(assetId, state));
        }
    }

    public async Task<IDisposable> FenceAsync(Guid assetId, CancellationToken cancellationToken)
    {
        State state;
        Task drained;
        lock (_sync)
        {
            state = GetState(assetId);
            if (state.Fenced) throw new InvalidOperationException("Preview use is already fenced for this Asset.");
            state.Fenced = true;
            if (state.Active == 0) drained = Task.CompletedTask;
            else
            {
                state.Drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
                drained = state.Drained.Task;
            }
        }

        try
        {
            await drained.WaitAsync(cancellationToken);
            return new Lease(() => Unfence(assetId, state));
        }
        catch
        {
            Unfence(assetId, state);
            throw;
        }
    }

    private State GetState(Guid assetId)
    {
        if (!_states.TryGetValue(assetId, out State? state)) _states.Add(assetId, state = new());
        return state;
    }

    private void Exit(Guid assetId, State state)
    {
        lock (_sync)
        {
            state.Active--;
            if (state.Active == 0)
            {
                state.Drained?.TrySetResult();
                state.Drained = null;
                if (!state.Fenced) _states.Remove(assetId);
            }
        }
    }

    private void Unfence(Guid assetId, State state)
    {
        lock (_sync)
        {
            state.Fenced = false;
            state.Drained = null;
            if (state.Active == 0) _states.Remove(assetId);
        }
    }

    private sealed class State
    {
        public int Active { get; set; }
        public bool Fenced { get; set; }
        public TaskCompletionSource? Drained { get; set; }
    }

    private sealed class Lease(Action release) : IDisposable
    {
        private Action? _release = release;
        public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
    }
}
