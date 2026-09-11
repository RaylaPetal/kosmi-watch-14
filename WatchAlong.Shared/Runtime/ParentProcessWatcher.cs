namespace WatchAlong.Shared.Runtime;

/// <summary>
/// Detects the parent process disappearing, per design.md §6.1.3 ("a game crash never leaves
/// a zombie Chromium playing audio"). Driven by <see cref="Tick"/> so the polling decision is
/// deterministic to test; the renderer's real loop calls it once a second against
/// <c>Process.GetProcessById</c>.
/// </summary>
public sealed class ParentProcessWatcher(int parentPid, Func<int, bool> isProcessAlive)
{
    public event Action? ParentGone;

    private bool _fired;

    public void Tick()
    {
        if (_fired)
            return;

        if (!isProcessAlive(parentPid))
        {
            _fired = true;
            ParentGone?.Invoke();
        }
    }
}
