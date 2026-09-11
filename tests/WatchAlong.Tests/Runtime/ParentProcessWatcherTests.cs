using WatchAlong.Shared.Runtime;
using Xunit;

namespace WatchAlong.Tests.Runtime;

public class ParentProcessWatcherTests
{
    [Fact]
    public void Alive_parent_never_fires()
    {
        var watcher = new ParentProcessWatcher(1234, _ => true);
        var fired = false;
        watcher.ParentGone += () => fired = true;

        for (var i = 0; i < 10; i++)
            watcher.Tick();

        Assert.False(fired);
    }

    [Fact]
    public void Dead_parent_fires_exactly_once()
    {
        var watcher = new ParentProcessWatcher(1234, _ => false);
        var fireCount = 0;
        watcher.ParentGone += () => fireCount++;

        watcher.Tick();
        watcher.Tick();
        watcher.Tick();

        Assert.Equal(1, fireCount);
    }

    [Fact]
    public void Queries_the_configured_parent_pid()
    {
        int? queriedPid = null;
        var watcher = new ParentProcessWatcher(4242, pid => { queriedPid = pid; return true; });

        watcher.Tick();

        Assert.Equal(4242, queriedPid);
    }
}
