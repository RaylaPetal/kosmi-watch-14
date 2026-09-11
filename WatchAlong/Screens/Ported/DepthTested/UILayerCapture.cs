// Ported from Sebane1/XivMediaPlayer (AGPL-3.0) — XivMediaPlayer/Compositing/UILayerCapture.
// Trimmed: dropped IsPixelOccluding, RestoreUIRegions, and GeneratePreview. RestoreUIRegions
// patches game UI back over a raw backbuffer blit — WatchAlong's DepthTestedRenderer instead
// composites the UI back on *inside the shader* from this same captured back-buffer SRV, so a
// separate copy-back pass is never needed. IsPixelOccluding/GeneratePreview only fed
// XivMediaPlayer's own debug/settings panel, which WatchAlong has no equivalent of. Also drops
// the unused IAddonLifecycle constructor parameter. Per design.md Appendix C.
using System;
using System.Collections.Generic;
using Vortice.Direct3D11;

namespace WatchAlong.Screens.Ported.DepthTested;

/// <summary>
/// Captures the game's back buffer (Scene + UI) at the start of each ImGui frame, so
/// <see cref="DepthTestedRenderer"/> can composite game UI back over an occluded screen.
/// </summary>
internal sealed unsafe class UILayerCapture : IDisposable
{
    private ID3D11DeviceContext _context = null!;
    private ID3D11Device _device = null!;
    private ID3D11Texture2D? _backBufferCopy;
    private ID3D11ShaderResourceView? _backBufferSRV;
    private int _width, _height;
    private Vortice.DXGI.Format _format;
    private bool _disposed;
    private bool _initialized;

    public bool IsInitialized => _initialized;
    public ID3D11ShaderResourceView? BackBufferSRV => _backBufferSRV;

    public bool Initialize()
    {
        if (_initialized || _disposed) return _initialized;

        try
        {
            var ffxivDevice = FFXIVClientStructs.FFXIV.Client.Graphics.Kernel.Device.Instance();
            if (ffxivDevice == null || ffxivDevice->D3D11DeviceContext == null)
                return false;

            var contextPtr = (IntPtr)ffxivDevice->D3D11DeviceContext;
            System.Runtime.InteropServices.Marshal.AddRef(contextPtr);
            _context = new ID3D11DeviceContext(contextPtr);
            _device = _context.Device;

            _initialized = true;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private bool CaptureToTexture()
    {
        try
        {
            var rtm = FFXIVClientStructs.FFXIV.Client.Graphics.Render.RenderTargetManager.Instance();
            if (rtm == null || rtm->SwapChainBackBuffer == null || rtm->SwapChainBackBuffer->D3D11Texture2D == null)
                return false;

            var texPtr = (IntPtr)rtm->SwapChainBackBuffer->D3D11Texture2D;
            System.Runtime.InteropServices.Marshal.AddRef(texPtr);
            var backBuffer = new ID3D11Texture2D(texPtr);
            var desc = backBuffer.Description;

            if (_backBufferCopy == null || _width != (int)desc.Width || _height != (int)desc.Height || _format != desc.Format)
            {
                _backBufferCopy?.Dispose();
                _backBufferSRV?.Dispose();
                _backBufferSRV = null;
                _width = (int)desc.Width;
                _height = (int)desc.Height;
                _format = desc.Format;

                _backBufferCopy = _device.CreateTexture2D(new Texture2DDescription
                {
                    Width = desc.Width,
                    Height = desc.Height,
                    MipLevels = 1,
                    ArraySize = 1,
                    Format = desc.Format,
                    SampleDescription = new Vortice.DXGI.SampleDescription(1, 0),
                    Usage = ResourceUsage.Default,
                    BindFlags = BindFlags.ShaderResource,
                    CPUAccessFlags = CpuAccessFlags.None,
                });

                _backBufferSRV = _device.CreateShaderResourceView(_backBufferCopy);
            }

            if (desc.SampleDescription.Count > 1)
                _context.ResolveSubresource(_backBufferCopy, 0, backBuffer, 0, desc.Format);
            else
                _context.CopyResource(_backBufferCopy, backBuffer);

            backBuffer.Dispose();
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Call at the very start of OnDraw, before any ImGui rendering. Captures the current back buffer (Scene + Game UI, alpha channel carries the UI mask) and the visible addon rects.</summary>
    public void CaptureFrame()
    {
        if (_disposed || !_initialized) return;

        if (CaptureToTexture())
        {
            LastAddonRects = GetVisibleAddonRects();
        }
    }

    /// <summary>The last set of addon rectangles found, fed to the shader as UI-culling regions.</summary>
    public List<(int X, int Y, int W, int H, string Name)> LastAddonRects { get; private set; } = new();

    private List<(int X, int Y, int W, int H, string Name)> GetVisibleAddonRects()
    {
        var rects = new List<(int X, int Y, int W, int H, string Name)>();

        try
        {
            var unitManager = FFXIVClientStructs.FFXIV.Client.UI.RaptureAtkUnitManager.Instance();
            if (unitManager == null)
                return rects;

            var unitList = &unitManager->AllLoadedUnitsList;
            for (int i = 0; i < unitList->Count; i++)
            {
                var unitBase = unitList->Entries[i].Value;
                if (unitBase == null) continue;
                if (!unitBase->IsVisible || unitBase->RootNode == null) continue;

                var x = (int)unitBase->X;
                var y = (int)unitBase->Y;
                var w = (int)(unitBase->RootNode->Width * unitBase->Scale);
                var h = (int)(unitBase->RootNode->Height * unitBase->Scale);
                var name = unitBase->NameString;

                if (w <= 0 || h <= 0) continue;

                rects.Add((x, y, w, h, name));
            }
        }
        catch
        {
            // Best-effort enumeration — a failure here just means UI culling is skipped this frame.
        }

        return rects;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _backBufferSRV?.Dispose();
        _backBufferCopy?.Dispose();
        _context?.Dispose();
        _device?.Dispose();
        _device = null!;
        _context = null!;
    }
}
