using System.IO.MemoryMappedFiles;

namespace WatchAlong.Shared.Frames;

/// <summary>
/// Owns the memory-mapped file backing a <see cref="FrameRingWriter"/>. This is the
/// production transport design.md §6.5 describes: the renderer creates a named mapping, the
/// plugin opens the same name (from the <c>FrameRingInfo</c> handshake message) to read it —
/// both processes run as Windows-targeted .NET (native Windows or under Wine), where named
/// maps are supported. A named <see cref="MemoryMappedFile"/> is unsupported by the .NET
/// Unix PAL, so <see cref="CreateFileBacked"/> exists purely so this class's own plumbing
/// (acquire a pointer, hand it to <see cref="FrameRingWriter"/>) can be exercised by tests
/// running directly on Linux, without changing anything about the production code path.
/// </summary>
public sealed unsafe class SharedMemoryFrameRingWriter : IDisposable
{
    private readonly MemoryMappedFile _file;
    private readonly MemoryMappedViewAccessor _view;
    private byte* _basePtr;

    public FrameRingWriter Ring { get; }
    public long TotalSize { get; }

    private SharedMemoryFrameRingWriter(MemoryMappedFile file, MemoryMappedViewAccessor view, byte* basePtr, FrameRingWriter ring, long totalSize)
    {
        _file = file;
        _view = view;
        _basePtr = basePtr;
        Ring = ring;
        TotalSize = totalSize;
    }

    /// <summary>Production path: a named mapping other processes open by name (Windows / Wine only).</summary>
    public static SharedMemoryFrameRingWriter Create(string mapName, ushort slotCount, int maxWidth, int maxHeight) =>
        CreateCore(size => MemoryMappedFile.CreateNew(mapName, size), slotCount, maxWidth, maxHeight);

    /// <summary>Test path: a file-backed mapping, since named maps aren't supported by the .NET Unix PAL.</summary>
    public static SharedMemoryFrameRingWriter CreateFileBacked(string filePath, ushort slotCount, int maxWidth, int maxHeight) =>
        CreateCore(size => MemoryMappedFile.CreateFromFile(filePath, FileMode.Create, null, size), slotCount, maxWidth, maxHeight);

    private static SharedMemoryFrameRingWriter CreateCore(Func<long, MemoryMappedFile> open, ushort slotCount, int maxWidth, int maxHeight)
    {
        var totalSize = FrameRingLayout.ComputeTotalSize(slotCount, maxWidth, maxHeight);
        var file = open(totalSize);
        var view = file.CreateViewAccessor(0, totalSize);

        byte* basePtr = null;
        view.SafeMemoryMappedViewHandle.AcquirePointer(ref basePtr);

        var ring = FrameRingWriter.CreateAndInitialize(basePtr, slotCount, maxWidth, maxHeight);
        return new SharedMemoryFrameRingWriter(file, view, basePtr, ring, totalSize);
    }

    public void Dispose()
    {
        if (_basePtr is not null)
        {
            _view.SafeMemoryMappedViewHandle.ReleasePointer();
            _basePtr = null;
        }

        _view.Dispose();
        _file.Dispose();
    }
}

/// <summary>Opens an existing ring (the plugin side, once it has the writer's <c>FrameRingInfo</c>).</summary>
public sealed unsafe class SharedMemoryFrameRingReader : IDisposable
{
    private readonly MemoryMappedFile _file;
    private readonly MemoryMappedViewAccessor _view;
    private byte* _basePtr;

    public FrameRingReader Ring { get; }

    private SharedMemoryFrameRingReader(MemoryMappedFile file, MemoryMappedViewAccessor view, byte* basePtr, FrameRingReader ring)
    {
        _file = file;
        _view = view;
        _basePtr = basePtr;
        Ring = ring;
    }

    /// <summary>Production path: opens the writer's named mapping (Windows / Wine only).</summary>
    public static SharedMemoryFrameRingReader Open(string mapName, long totalSize) =>
        OpenCore(() => MemoryMappedFile.OpenExisting(mapName), totalSize);

    /// <summary>Test path: opens the same file <see cref="SharedMemoryFrameRingWriter.CreateFileBacked"/> used.</summary>
    public static SharedMemoryFrameRingReader OpenFileBacked(string filePath, long totalSize) =>
        OpenCore(() => MemoryMappedFile.CreateFromFile(filePath, FileMode.Open, null, totalSize), totalSize);

    private static SharedMemoryFrameRingReader OpenCore(Func<MemoryMappedFile> open, long totalSize)
    {
        var file = open();
        var view = file.CreateViewAccessor(0, totalSize);

        byte* basePtr = null;
        view.SafeMemoryMappedViewHandle.AcquirePointer(ref basePtr);

        return new SharedMemoryFrameRingReader(file, view, basePtr, new FrameRingReader(basePtr));
    }

    public void Dispose()
    {
        if (_basePtr is not null)
        {
            _view.SafeMemoryMappedViewHandle.ReleasePointer();
            _basePtr = null;
        }

        _view.Dispose();
        _file.Dispose();
    }
}
