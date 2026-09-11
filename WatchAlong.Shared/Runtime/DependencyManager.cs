namespace WatchAlong.Shared.Runtime;

/// <summary>
/// Downloads a versioned runtime (the CEF/Chromium redistributable) into the given directory,
/// per design.md §6.1/Appendix C: dependencies are fetched at runtime into the plugin's config
/// directory rather than bundled in the plugin zip.
/// </summary>
public interface IRuntimeSource
{
    /// <summary>Downloads and extracts the runtime into <paramref name="destinationDir"/>.</summary>
    Task DownloadAsync(string destinationDir, IProgress<double> progress, CancellationToken cancellationToken);
}

/// <summary>
/// Tracks whether a runtime dependency is present and lets it be wiped and re-fetched, per
/// specs/renderer-process/spec.md "Dependency acquisition".
/// </summary>
public sealed class DependencyManager(IRuntimeSource source)
{
    private const string MarkerFileName = ".watchalong-complete";

    /// <summary>
    /// A marker file written only after a download completes successfully, so a partial or
    /// corrupted download is never mistaken for a usable runtime.
    /// </summary>
    public bool IsPresent(string destinationDir) =>
        File.Exists(Path.Combine(destinationDir, MarkerFileName));

    public async Task EnsureAvailableAsync(string destinationDir, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        if (IsPresent(destinationDir))
            return;

        Directory.CreateDirectory(destinationDir);
        await source.DownloadAsync(destinationDir, progress ?? new Progress<double>(), cancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Combine(destinationDir, MarkerFileName), DateTime.UtcNow.ToString("O"), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>"Wipe and re-download" per design.md §12 — deletes everything and fetches again.</summary>
    public async Task WipeAndReacquireAsync(string destinationDir, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        if (Directory.Exists(destinationDir))
            Directory.Delete(destinationDir, recursive: true);

        await EnsureAvailableAsync(destinationDir, progress, cancellationToken).ConfigureAwait(false);
    }
}
