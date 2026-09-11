using CefSharp;
using CefSharp.Handler;

namespace WatchAlong.Renderer.Policies;

/// <summary>Suppresses all JS dialogs (alert/confirm/prompt/beforeunload) and logs them, per design.md §6.3.</summary>
public sealed class JsDialogPolicy : JsDialogHandler
{
    public event Action<string>? DialogSuppressed;

    protected override bool OnJSDialog(
        IWebBrowser chromiumWebBrowser, IBrowser browser, string originUrl, CefJsDialogType dialogType,
        string messageText, string defaultPromptText, IJsDialogCallback callback, ref bool suppressMessage)
    {
        DialogSuppressed?.Invoke($"{dialogType}: {messageText}");
        suppressMessage = true;
        return false;
    }

    protected override bool OnBeforeUnloadDialog(IWebBrowser chromiumWebBrowser, IBrowser browser, string messageText, bool isReload, IJsDialogCallback callback)
    {
        DialogSuppressed?.Invoke($"BeforeUnload: {messageText}");
        callback.Continue(true); // let navigation proceed without ever showing the prompt
        return true;
    }
}
