// Ported from Sebane1/XivMediaPlayer (AGPL-3.0) — XivMediaPlayer/Compositing/DepthBufferCapture.
// Trimmed: dropped GeneratePreview/LastRgbaData/DebugInfo, which only fed XivMediaPlayer's own
// settings-window debug panel — WatchAlong has no equivalent panel — per design.md Appendix C.
using System;
using FFXIVClientStructs.FFXIV.Client.Graphics.Kernel;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace WatchAlong.Screens.Ported.DepthTested;

/// <summary>
/// Captures the game's depth buffer from the RenderTargetManager every frame. The RTM depth
/// buffer retains valid scene data even during the ImGui pass, so a direct CopyResource from the
/// game's depth texture works without hooking.
/// </summary>
internal sealed unsafe class DepthBufferCapture : IDisposable
{
    private ID3D11DeviceContext _context = null!;
    private ID3D11Device _device = null!;
    private ID3D11Texture2D? _depthCopy;
    private ID3D11Texture2D? _stagingTexture;
    private ID3D11DepthStencilView? _depthCopyDSV;
    private ID3D11ShaderResourceView? _depthCopySRV;
    private int _texWidth, _texHeight;
    private Format _texFormat;
    private int _sampleCount;
    private int _sampleQuality;
    private bool _disposed;
    private bool _initialized;
    private IntPtr _gameDepthTexturePtr;

    private float[]? _depthData;
    private bool _readDepthEnabled;

    /// <summary>Enable/disable per-frame CPU depth readback. Only enable when occlusion is active.</summary>
    public bool ReadDepthEnabled { get => _readDepthEnabled; set => _readDepthEnabled = value; }

    public bool IsInitialized => _initialized;
    public int DepthWidth => _texWidth;
    public int DepthHeight => _texHeight;
    public float RenderWidth { get; private set; }
    public float RenderHeight { get; private set; }

    /// <summary>The SRV of our captured depth copy, for sampling in a pixel shader.</summary>
    public ID3D11ShaderResourceView? CapturedSRV => _depthCopySRV;

    public float[]? LastDepthData => _depthData;

    public bool Initialize()
    {
        if (_initialized || _disposed) return _initialized;

        try
        {
            var ffxivDevice = Device.Instance();
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

    private void CopyDepthBuffer(ID3D11Texture2D depthTexture)
    {
        var texDesc = depthTexture.Description;

        if (_depthCopy == null ||
            _texWidth != (int)texDesc.Width ||
            _texHeight != (int)texDesc.Height ||
            _texFormat != texDesc.Format ||
            _sampleCount != texDesc.SampleDescription.Count ||
            _sampleQuality != texDesc.SampleDescription.Quality)
        {
            _depthCopy?.Dispose();
            _depthCopyDSV?.Dispose();
            _depthCopySRV?.Dispose();
            _stagingTexture?.Dispose();
            _depthCopyDSV = null;
            _depthCopySRV = null;
            _stagingTexture = null;

            _texWidth = (int)texDesc.Width;
            _texHeight = (int)texDesc.Height;
            _texFormat = texDesc.Format;
            _sampleCount = (int)texDesc.SampleDescription.Count;
            _sampleQuality = (int)texDesc.SampleDescription.Quality;

            _depthCopy = _device.CreateTexture2D(new Texture2DDescription
            {
                Width = texDesc.Width,
                Height = texDesc.Height,
                MipLevels = 1,
                ArraySize = 1,
                Format = texDesc.Format,
                SampleDescription = new SampleDescription((uint)_sampleCount, (uint)_sampleQuality),
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.DepthStencil | BindFlags.ShaderResource,
                CPUAccessFlags = CpuAccessFlags.None,
            });

            Format dsvFormat = texDesc.Format switch
            {
                Format.R24G8_Typeless => Format.D24_UNorm_S8_UInt,
                Format.R32_Typeless => Format.D32_Float,
                Format.R32G8X24_Typeless => Format.D32_Float_S8X24_UInt,
                _ => texDesc.Format,
            };
            _depthCopyDSV = _device.CreateDepthStencilView(_depthCopy, new DepthStencilViewDescription
            {
                Format = dsvFormat,
                ViewDimension = _sampleCount > 1 ? DepthStencilViewDimension.Texture2DMultisampled : DepthStencilViewDimension.Texture2D,
            });

            Format srvFormat = texDesc.Format switch
            {
                Format.R24G8_Typeless => Format.R24_UNorm_X8_Typeless,
                Format.R32_Typeless => Format.R32_Float,
                Format.R32G8X24_Typeless => Format.R32_Float_X8X24_Typeless,
                _ => texDesc.Format,
            };
            _depthCopySRV = _device.CreateShaderResourceView(_depthCopy, new ShaderResourceViewDescription
            {
                Format = srvFormat,
                ViewDimension = _sampleCount > 1 ? Vortice.Direct3D.ShaderResourceViewDimension.Texture2DMultisampled : Vortice.Direct3D.ShaderResourceViewDimension.Texture2D,
                Texture2D = new Texture2DShaderResourceView { MipLevels = 1, MostDetailedMip = 0 },
            });

            _stagingTexture = _device.CreateTexture2D(new Texture2DDescription
            {
                Width = texDesc.Width,
                Height = texDesc.Height,
                MipLevels = 1,
                ArraySize = 1,
                Format = texDesc.Format,
                SampleDescription = new SampleDescription((uint)_sampleCount, (uint)_sampleQuality),
                Usage = ResourceUsage.Staging,
                BindFlags = BindFlags.None,
                CPUAccessFlags = CpuAccessFlags.Read,
            });
        }

        _context.CopyResource(_depthCopy, depthTexture);
    }

    /// <summary>Call at the very start of OnDraw. Copies the game's depth buffer.</summary>
    public void BeginFrame()
    {
        if (!_initialized || _disposed) return;

        try
        {
            var rtm = FFXIVClientStructs.FFXIV.Client.Graphics.Render.RenderTargetManager.Instance();
            if (rtm != null && rtm->DepthStencil != null && rtm->DepthStencil->D3D11Texture2D != null)
            {
                _gameDepthTexturePtr = (IntPtr)rtm->DepthStencil->D3D11Texture2D;
                RenderWidth = rtm->Resolution_Width;
                RenderHeight = rtm->Resolution_Height;
            }
            else
            {
                var ffxivDevice = Device.Instance();
                if (ffxivDevice != null && ffxivDevice->SwapChain != null && ffxivDevice->SwapChain->DepthStencil != null && ffxivDevice->SwapChain->DepthStencil->D3D11Texture2D != null)
                {
                    _gameDepthTexturePtr = (IntPtr)ffxivDevice->SwapChain->DepthStencil->D3D11Texture2D;
                }
                else
                {
                    _gameDepthTexturePtr = IntPtr.Zero;
                }
            }

            if (_gameDepthTexturePtr == IntPtr.Zero) return;

            System.Runtime.InteropServices.Marshal.AddRef(_gameDepthTexturePtr);
            using var depthTexture = new ID3D11Texture2D(_gameDepthTexturePtr);
            CopyDepthBuffer(depthTexture);
            ReadDepthToArray();
        }
        catch
        {
            // Depth capture is best-effort — a failure here just means occlusion is skipped this frame.
        }
    }

    /// <summary>Reads the depth buffer from the staging texture into a cached float array. Unsafe pointer access for performance (up to a few million pixels per frame).</summary>
    private void ReadDepthToArray()
    {
        if (_depthCopy == null || _stagingTexture == null || _context == null) return;
        if (!_readDepthEnabled) return;

        try
        {
            _context.CopyResource(_stagingTexture, _depthCopy);
            var mapped = _context.Map(_stagingTexture, 0, MapMode.Read);
            try
            {
                if (_depthData == null || _depthData.Length != _texWidth * _texHeight)
                {
                    _depthData = new float[_texWidth * _texHeight];
                }

                bool isD24 = _texFormat == Format.R24G8_Typeless || _texFormat == Format.D24_UNorm_S8_UInt;
                const float inv24 = 1.0f / 0x00FFFFFF;

                for (int y = 0; y < _texHeight; y++)
                {
                    byte* rowPtr = (byte*)mapped.DataPointer + y * (int)mapped.RowPitch;
                    uint* row32 = (uint*)rowPtr;
                    int offset = y * _texWidth;

                    if (isD24)
                    {
                        for (int x = 0; x < _texWidth; x++)
                        {
                            _depthData[offset + x] = (row32[x] & 0x00FFFFFF) * inv24;
                        }
                    }
                    else
                    {
                        float* rowF = (float*)rowPtr;
                        for (int x = 0; x < _texWidth; x++)
                        {
                            _depthData[offset + x] = rowF[x];
                        }
                    }
                }
            }
            finally
            {
                _context.Unmap(_stagingTexture, 0);
            }
        }
        catch
        {
            // Best-effort; a stale/missing depth array just means occlusion is skipped this frame.
        }
    }

    /// <summary>Get the depth value at a screen coordinate. Returns 0 if out of bounds.</summary>
    public float GetDepthAt(int screenX, int screenY)
    {
        if (_depthData == null || screenX < 0 || screenY < 0 || screenX >= _texWidth || screenY >= _texHeight)
            return 0;
        return _depthData[screenY * _texWidth + screenX];
    }

    /// <summary>Quickly finds the min and max depth in the current depth buffer, ignoring the skybox. Used for auto-ranging.</summary>
    public void GetMinMaxDepth(out float minDepth, out float maxDepth)
    {
        minDepth = 0.001f;
        maxDepth = 1.0f;

        if (_depthData == null) return;

        float min = 1.0f;
        float max = 0.001f;

        // Scan a grid of points instead of every pixel to save CPU
        int step = 16;
        for (int i = 0; i < _depthData.Length; i += step)
        {
            float d = _depthData[i];
            if (d > 0.0001f)
            {
                if (d < min) min = d;
                if (d > max) max = d;
            }
        }

        if (max >= min)
        {
            minDepth = min;
            maxDepth = max;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _depthCopyDSV?.Dispose();
        _depthCopySRV?.Dispose();
        _depthCopy?.Dispose();
        _stagingTexture?.Dispose();

        _device?.Dispose();
        _context?.Dispose();
        _device = null!;
        _context = null!;
    }
}
