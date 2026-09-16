using System.Text.Json.Nodes;
using CefSharp;
using CefSharp.OffScreen;
using WatchAlong.Renderer.Policies;
using WatchAlong.Shared.Ipc;
using WatchAlong.Shared.Kosmi;

namespace WatchAlong.Renderer;

/// <summary>
/// One Kosmi browser instance: the <see cref="ChromiumWebBrowser"/> plus every policy/sink
/// wired to it (design.md §5, §6.3). Only one exists at a time (specs/renderer-process/spec.md
/// "Single active renderer") — <see cref="RendererApp"/> replaces it wholesale on a new
/// <c>OpenRoom</c> rather than ever running two side by side.
/// </summary>
public sealed class KosmiBrowser : IDisposable
{
    private static readonly string AgentSource = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Assets", "kosmi-agent.js"));
    private static readonly string DefaultProfileJson = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Assets", "default-profile.json"));

    private readonly ChromiumWebBrowser _browser;
    private readonly FrameWriter _frameWriter;
    private readonly AudioSink _audioSink;
    private readonly NavigationPolicy _navigationPolicy = new();
    private readonly PermissionPolicy _permissionPolicy = new();
    private readonly PopupPolicy _popupPolicy = new();
    private readonly DownloadPolicy _downloadPolicy = new();
    private readonly JsDialogPolicy _jsDialogPolicy = new();
    private readonly string _displayName;
    private string? _lastLoggedMediaGeometry;

    public event Action<string>? NavigationBlocked;
    public event Action<string>? JsDialogSuppressed;

    /// <summary>Page-state string reported by kosmi-agent.js (Loading/JoinGate/InRoom/etc, design.md §6.4 job 1).</summary>
    public event Action<string>? PageStateChanged;

    /// <summary>Primary-media state reported by kosmi-agent.js (design.md §6.4 jobs 3/5).</summary>
    public event Action<MediaStateMessage>? MediaStateChanged;

    public KosmiBrowser(string safeRoomUrl, string displayName, string frameMapName, ushort frameSlotCount, int maxWidth, int maxHeight)
    {
        _displayName = displayName;
        _frameWriter = new FrameWriter(frameMapName, frameSlotCount, maxWidth, maxHeight);
        _audioSink = new AudioSink();

        _navigationPolicy.NavigationBlocked += url => NavigationBlocked?.Invoke(url);
        _jsDialogPolicy.DialogSuppressed += msg => JsDialogSuppressed?.Invoke(msg);

        _browser = new ChromiumWebBrowser(safeRoomUrl)
        {
            // CefSharp.OffScreen defaults this to 1366x768 if never set explicitly — everything
            // else here (FrameWriter's shared-memory slots, the theater-mode CSS forcing the
            // player to 100vw/100vh) assumes the negotiated maxWidth/maxHeight (1920x1080), so
            // leaving the actual capture viewport at CefSharp's own default was a real mismatch.
            Size = new System.Drawing.Size(maxWidth, maxHeight),
            RequestHandler = _navigationPolicy,
            LifeSpanHandler = _popupPolicy,
            DownloadHandler = _downloadPolicy,
            JsDialogHandler = _jsDialogPolicy,
            AudioHandler = _audioSink,
        };
        _browser.Paint += _frameWriter.OnPaint;
        _browser.FrameLoadEnd += OnFrameLoadEnd;
        _browser.JavascriptMessageReceived += OnJavascriptMessageReceived;
    }

    /// <summary>
    /// ChromiumWebBrowser creates its underlying CEF browser asynchronously — calling
    /// GetBrowser() (as CreateInputRouter does) before it's ready throws. Await this first.
    /// </summary>
    public Task WaitForInitialLoadAsync() => _browser.WaitForInitialLoadAsync();

    public InputRouter CreateInputRouter() => new(_browser.GetBrowser());

    public void SetAudio(double volume, bool muted) => _audioSink.SetVolume(volume, muted);

    /// <summary>See <see cref="FrameWriter.Heartbeat"/> — call periodically, independent of paint activity.</summary>
    public void Heartbeat() => _frameWriter.Heartbeat();

    /// <summary>
    /// Dumps a screenshot plus a text-stripped DOM outline (design.md §6.4 item 1) — the
    /// intended way to see exactly what the page-agent's selectors are actually matching
    /// against on a real Kosmi room, instead of guessing blind at the (placeholder,
    /// unrefined — see kosmi-agent.js's selectors) default profile.
    /// </summary>
    public async Task SaveDebugSnapshotAsync(string outputDirectory)
    {
        var domOutlineJson = "null";
        var response = await _browser.GetMainFrame().EvaluateScriptAsync(
            "JSON.stringify(WatchAlongAgent.buildDomOutline(document.body))");
        if (response.Success && response.Result is string json)
            domOutlineJson = json;

        await new DebugSnapshotWriter(outputDirectory).SaveAsync(_browser, domOutlineJson);
    }

    /// <summary>Independently re-validates before navigating anywhere, per specs/renderer-process/spec.md.</summary>
    public static bool TryCreate(OpenRoomMessage request, string frameMapName, ushort frameSlotCount, int maxWidth, int maxHeight, out KosmiBrowser? browser)
    {
        browser = null;

        if (!RoomLoadGuard.TryGetSafeLoadUrl(request.RoomUrl, out var safeUrl))
            return false;

        browser = new KosmiBrowser(safeUrl, request.DisplayName, frameMapName, frameSlotCount, maxWidth, maxHeight);
        return true;
    }

    /// <summary>
    /// Injects kosmi-agent.js plus a small polling driver on every navigation (design.md §6.4:
    /// "re-injected on every navigation") — page-state detection, join-gate automation, and
    /// primary-media/theater-mode all run from this one script via a 500ms setInterval, posting
    /// JSON back through <c>CefSharp.PostMessage</c>.
    /// </summary>
    private async void OnFrameLoadEnd(object? sender, FrameLoadEndEventArgs e)
    {
        // An `async void` handler that throws is fatal to the whole process — it bypasses
        // every surrounding try/catch, including Program.Main's. The frame/browser can be
        // disposed while the evaluate below is in flight (Kosmi is an SPA that redirects/soft-
        // navigates right after its initial load, invalidating this FrameLoadEnd's frame before
        // the round trip completes), so every failure here must be swallowed: it just means
        // this injection round is skipped, and the next FrameLoadEnd (for whatever page the SPA
        // settles on) retries it. Everything is one script/one EvaluateScriptAsync call — not
        // three sequential ones — specifically to minimize that race window: three round trips
        // per attempt meant three chances to lose to the next navigation, and in practice every
        // single attempt was losing.
        try
        {
            if (!e.Frame.IsMain)
                return;

            var displayNameJson = System.Text.Json.JsonSerializer.Serialize(_displayName);
            var combinedScript =
                AgentSource + "\n" +
                $"window.WatchAlongProfile = {DefaultProfileJson}; window.WatchAlongDisplayName = {displayNameJson};\n" +
                DriverScript;

            var response = await e.Frame.EvaluateScriptAsync(combinedScript).ConfigureAwait(false);
            if (!response.Success)
                Console.Error.WriteLine($"WatchAlong.Renderer: page-agent script failed: {response.Message}");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"WatchAlong.Renderer: page-agent injection failed (frame/browser disposed mid-navigation?): {ex.Message}");
        }
    }

    private void OnJavascriptMessageReceived(object? sender, JavascriptMessageReceivedEventArgs e)
    {
        // This parses whatever the page's polling driver happened to post, and runs on
        // CefSharp's own callback thread — a malformed/unexpected payload (or a browser/frame
        // that's mid-disposal) must never throw out of here uncaught.
        try
        {
            if (e.Message is not string json)
                return;

            if (JsonNode.Parse(json) is not JsonObject payload)
                return;

            switch (payload["type"]?.GetValue<string>())
            {
                case "page":
                    PageStateChanged?.Invoke(payload["state"]?.GetValue<string>() ?? "Loading");
                    break;

                case "media":
                    var rect = payload["rect"] is JsonArray rectArray
                        ? rectArray.Select(n => (int)(n?.GetValue<double>() ?? 0)).ToArray()
                        : [0, 0, 0, 0];

                    // Confirmed via logged geometry (kind=html5 rect=[2,59,1917,1080] against a
                    // 1080-tall capture): Kosmi's page renders a real header/toolbar above the
                    // player that theater mode's position:fixed;inset:0 isn't suppressing for this
                    // element, so content genuinely starts partway down the page. FrameWriter's
                    // capture now has headroom above 1080 for exactly this (see RendererApp's
                    // MaxHeight) — crop to what the page agent reports actually contains the player.
                    if (rect.Length == 4)
                        _frameWriter.SetContentRect(rect[0], rect[1], rect[2], rect[3]);

                    var kindVal = payload["kind"]?.GetValue<string>() ?? "none";
                    var wVal = (int?)payload["w"]?.GetValue<double>() ?? 0;
                    var hVal = (int?)payload["h"]?.GetValue<double>() ?? 0;
                    var geometry = $"kind={kindVal} reportedWH={wVal}x{hVal} rect=[{string.Join(",", rect)}]";
                    if (geometry != _lastLoggedMediaGeometry)
                    {
                        _lastLoggedMediaGeometry = geometry;
                        // stdout, not stderr: a geometry change is routine (it fires every time
                        // media starts or the page layout shifts), not a failure.
                        Console.WriteLine($"WatchAlong.Renderer: media geometry changed: {geometry}");
                    }

                    MediaStateChanged?.Invoke(new MediaStateMessage(
                        payload["kind"]?.GetValue<string>() ?? "none",
                        (int?)payload["w"]?.GetValue<double>() ?? 0,
                        (int?)payload["h"]?.GetValue<double>() ?? 0,
                        payload["paused"]?.GetValue<bool>() ?? false,
                        payload["currentTime"]?.GetValue<double?>(),
                        payload["src"]?.GetValue<string>(),
                        (int?)payload["error"]?.GetValue<double?>(),
                        rect));
                    break;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"WatchAlong.Renderer: failed to handle a page-agent message: {ex.Message}");
        }
    }

    // Kept deliberately as a plain polling loop (setInterval) rather than the render-process
    // context-created hook design.md §6.4 describes — that needs a custom CEF subprocess
    // (IRenderProcessHandler), which is a lot more plumbing for the same observable behavior at
    // this frame rate. FrameLoadEnd + a 500ms poll is the pragmatic version of the same "detect
    // state, auto-join, pick primary media, apply theater mode" pipeline.
    private const string DriverScript = """
    (function () {
      if (window.__watchAlongDriverRunning) return;
      window.__watchAlongDriverRunning = true;

      var agent = window.WatchAlongAgent;
      var profile = window.WatchAlongProfile;
      var displayName = window.WatchAlongDisplayName || 'Guest';
      var lastPageState = null;
      var primary = new agent.PrimarySelector();
      var lastAppliedPrimary = null;

      function post(payload) {
        try { CefSharp.PostMessage(JSON.stringify(payload)); } catch (e) {}
      }

      function tick() {
        var state = agent.detectPageState(document, profile, document.readyState);
        if (state !== lastPageState) {
          lastPageState = state;
          post({ type: 'page', state: state });
        }

        if (state === 'JoinGate') {
          agent.tryJoinGate(window, document, profile, displayName);
        }

        if (state === 'InRoom') {
          var candidate = agent.pickBestCandidate(document, profile, window.innerWidth, window.innerHeight);
          var current = primary.consider(candidate, Date.now());

          if (current) {
            // applyTheaterMode/muteAllExceptPrimary do a full querySelectorAll plus removing
            // and re-adding data-wa-* attributes up the whole ancestor chain — forcing a full
            // style recalc/layout on every call. Running that unconditionally every 500ms
            // thrashed the page (and, since this all happens in the renderer's own CEF
            // process, measurably hurt the game's own FPS by competing for CPU). Only reapply
            // when the primary element actually changes; media state (paused/currentTime/rect)
            // still gets reported every tick below regardless.
            if (current !== lastAppliedPrimary) {
              agent.applyTheaterMode(document, current);
              agent.muteAllExceptPrimary(document, current);
              lastAppliedPrimary = current;
            }
            var rect = agent.computeContentRect(current);
            var kind = current.tagName === 'VIDEO' ? 'html5' : current.tagName === 'IFRAME' ? 'iframe' : 'canvas';
            var media = agent.buildMediaState(kind, current, rect, null);
            media.type = 'media';
            post(media);
          } else {
            post({ type: 'media', kind: 'none', rect: [0, 0, 0, 0] });
          }
        }
      }

      setInterval(tick, 500);
      tick();
    })();
    """;

    public void Dispose()
    {
        _browser.Paint -= _frameWriter.OnPaint;
        _browser.FrameLoadEnd -= OnFrameLoadEnd;
        _browser.JavascriptMessageReceived -= OnJavascriptMessageReceived;
        _browser.Dispose();
        _frameWriter.Dispose();
        _audioSink.Dispose();
    }
}
