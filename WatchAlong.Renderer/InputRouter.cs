using CefSharp;
using WatchAlong.Shared.Ipc;

namespace WatchAlong.Renderer;

/// <summary>
/// Translates <see cref="InputMessage"/>s from the plugin into CEF host input calls, per
/// design.md §6.7/§8.3. Coordinate scaling is done by the caller via
/// <c>WatchAlong.Shared.Input.InputCoordinateMapper</c> before the message is built — this
/// class just forwards already-view-space coordinates into CefSharp.
/// </summary>
public sealed class InputRouter(IBrowser browser)
{
    public void Handle(InputMessage input)
    {
        var host = browser.GetHost();

        switch (input.Kind)
        {
            case InputKind.MouseMove:
                host.SendMouseMoveEvent(new MouseEvent((int)input.X, (int)input.Y, CefEventFlags.None), mouseLeave: false);
                break;

            case InputKind.MouseButton:
                host.SendMouseClickEvent(
                    new MouseEvent((int)input.X, (int)input.Y, CefEventFlags.None),
                    MapButton(input.Button),
                    input.Up,
                    input.Clicks);
                break;

            case InputKind.Wheel:
                host.SendMouseWheelEvent(new MouseEvent((int)input.X, (int)input.Y, CefEventFlags.None), (int)input.Dx, (int)input.Dy);
                break;

            case InputKind.Key:
                host.SendKeyEvent(new KeyEvent
                {
                    Type = input.Up ? KeyEventType.KeyUp : KeyEventType.RawKeyDown,
                    WindowsKeyCode = input.Vk,
                    NativeKeyCode = input.Vk,
                    Modifiers = MapModifiers(input.Mods),
                });
                break;

            case InputKind.Text:
                SendText(host, input.Text ?? "");
                break;
        }
    }

    private static void SendText(IBrowserHost host, string text)
    {
        foreach (var ch in text)
        {
            host.SendKeyEvent(new KeyEvent
            {
                Type = KeyEventType.Char,
                WindowsKeyCode = ch,
                NativeKeyCode = ch,
            });
        }
    }

    private static MouseButtonType MapButton(string? button) => button switch
    {
        "Right" => MouseButtonType.Right,
        "Middle" => MouseButtonType.Middle,
        _ => MouseButtonType.Left,
    };

    /// <summary>Bit 0: Shift, bit 1: Ctrl, bit 2: Alt — matches the plugin-side `mods` bitmask (design.md §7 Input message).</summary>
    private static CefEventFlags MapModifiers(int mods)
    {
        var flags = CefEventFlags.None;
        if ((mods & 0x1) != 0) flags |= CefEventFlags.ShiftDown;
        if ((mods & 0x2) != 0) flags |= CefEventFlags.ControlDown;
        if ((mods & 0x4) != 0) flags |= CefEventFlags.AltDown;
        return flags;
    }
}
