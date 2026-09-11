using FFXIVClientStructs.FFXIV.Client.Graphics.Kernel;
using TerraFX.Interop.DirectX;

namespace WatchAlong.Core;

/// <summary>
/// Obtains the game's own D3D11 device/context, per design.md's approach for
/// <c>Direct3D11VideoTexture</c> (Appendix C: "obtained via
/// FFXIVClientStructs...Kernel.Device.Instance()-&gt;D3D11DeviceContext").
/// </summary>
public static unsafe class GameDevice
{
    public static (nint Device, nint Context) GetD3D11DeviceAndContext()
    {
        var context = (ID3D11DeviceContext*)Device.Instance()->D3D11DeviceContext;

        // GetDevice AddRefs the returned device (standard D3D11 COM semantics) — the game
        // already holds its own long-lived reference via Device.Instance(), so this extra ref
        // is never released; it's an intentional, bounded leak (one AddRef for the plugin's
        // whole lifetime), not a per-frame one.
        ID3D11Device* device;
        context->GetDevice(&device);

        return ((nint)device, (nint)context);
    }
}
