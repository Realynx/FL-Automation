using FruityLink.Core.Abstractions;

namespace FruityLink.FlStudio.Inject;

public sealed partial class FlInjectBridge : IFlNativeCompletionScope
{
    /// <inheritdoc />
    public IDisposable RequireNativeCompletion() => InProcBridge.RequireNativeCompletion();

    private sealed class NativeCallCompletion
    {
        private readonly List<Task> _work = new();

        public void Track(Task work)
        {
            lock (_work) _work.Add(work);
        }

        public async Task DrainAsync()
        {
            Task[] pending;
            lock (_work) pending = _work.ToArray();
            try { await Task.WhenAll(pending).ConfigureAwait(false); }
            catch { /* Transport reports the original result or error; draining owns lifetime only. */ }
        }
    }
}
