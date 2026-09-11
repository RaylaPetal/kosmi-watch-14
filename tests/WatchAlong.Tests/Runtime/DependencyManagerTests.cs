using WatchAlong.Shared.Runtime;
using Xunit;

namespace WatchAlong.Tests.Runtime;

public class DependencyManagerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "watchalong-dep-tests-" + Guid.NewGuid());

    private sealed class FakeRuntimeSource : IRuntimeSource
    {
        public int DownloadCount { get; private set; }
        public List<double> ReportedProgress { get; } = [];

        public Task DownloadAsync(string destinationDir, IProgress<double> progress, CancellationToken cancellationToken)
        {
            DownloadCount++;
            progress.Report(0.5);
            ReportedProgress.Add(0.5);
            File.WriteAllText(Path.Combine(destinationDir, "libcef.fake"), "fake runtime bytes");
            progress.Report(1.0);
            ReportedProgress.Add(1.0);
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task First_run_with_missing_runtime_downloads_and_marks_present()
    {
        var source = new FakeRuntimeSource();
        var manager = new DependencyManager(source);

        Assert.False(manager.IsPresent(_dir));

        var progressReports = new List<double>();
        await manager.EnsureAvailableAsync(_dir, new Progress<double>(p => progressReports.Add(p)));

        Assert.True(manager.IsPresent(_dir));
        Assert.Equal(1, source.DownloadCount);
        Assert.True(File.Exists(Path.Combine(_dir, "libcef.fake")));
    }

    [Fact]
    public async Task Already_present_runtime_is_not_re_downloaded()
    {
        var source = new FakeRuntimeSource();
        var manager = new DependencyManager(source);

        await manager.EnsureAvailableAsync(_dir);
        await manager.EnsureAvailableAsync(_dir);

        Assert.Equal(1, source.DownloadCount);
    }

    [Fact]
    public async Task Wipe_and_reacquire_deletes_then_downloads_again()
    {
        var source = new FakeRuntimeSource();
        var manager = new DependencyManager(source);

        await manager.EnsureAvailableAsync(_dir);
        Assert.Equal(1, source.DownloadCount);

        await manager.WipeAndReacquireAsync(_dir);

        Assert.Equal(2, source.DownloadCount);
        Assert.True(manager.IsPresent(_dir));
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }
}
