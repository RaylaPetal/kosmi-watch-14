// Ported from Sebane1/XivMediaPlayer (AGPL-3.0) — Direct3D11VideoTexture.
// Changes: copies from IntPtr + stride (no intermediate byte[]), tracks a ContentUv rect,
// and recovers from DXGI_ERROR_DEVICE_REMOVED/RESET by dropping the texture for the caller
// to recreate next frame, per design.md Appendix C and §7.1.
using System;
using TerraFX.Interop.DirectX;
using WatchAlong.Shared.Frames;
using static TerraFX.Interop.DirectX.DXGI_FORMAT;
using static TerraFX.Interop.DirectX.D3D11_USAGE;
using static TerraFX.Interop.DirectX.D3D11_BIND_FLAG;
using static TerraFX.Interop.DirectX.D3D11_CPU_ACCESS_FLAG;
using static TerraFX.Interop.DirectX.D3D11_MAP;
using static TerraFX.Interop.Windows.Windows;

namespace WatchAlong.Screens.Ported;

/// <summary>
/// A dynamic D3D11 texture updated via <c>Map(WriteDiscard)</c> from the frame ring, on the
/// game's own device — created once, resized only when the source dimensions change.
/// </summary>
public sealed unsafe class Direct3D11VideoTexture : IDisposable
{
    private readonly ID3D11Device* _device;
    private readonly ID3D11DeviceContext* _context;
    private ID3D11Texture2D* _texture;
    private ID3D11ShaderResourceView* _srv;
    private int _width;
    private int _height;

    public Direct3D11VideoTexture(nint device, nint context)
    {
        _device = (ID3D11Device*)device;
        _context = (ID3D11DeviceContext*)context;
    }

    public nint ShaderResourceView => (nint)_srv;
    public ContentUv ContentUv { get; private set; } = ContentUv.FullFrame;
    public bool HasTexture => _texture is not null;
    public int Width => _width;
    public int Height => _height;

    /// <summary>
    /// Copies a BGRA frame into the texture, (re)creating it if the size changed. Returns
    /// false on a device-removed/reset error — the texture is dropped and the caller should
    /// simply try again next frame (a game-side device reset recreates everything anyway).
    /// </summary>
    public bool Upload(ReadOnlySpan<byte> bgraPixels, int width, int height, int stride, ContentUv contentUv)
    {
        if (width <= 0 || height <= 0 || stride < width * 4)
            return false;

        // Last line of defense before the unsafe copy below: `stride`/`width`/`height` come
        // from a shared-memory header written by a separate process. The copy loop walks
        // `bgraPixels` via raw pointer arithmetic (`fixed`), which bypasses .NET's normal
        // bounds checks entirely — a mismatched stride/height here would read past the end of
        // the pinned buffer into unrelated memory, an access violation in the game's own
        // process, not just the renderer's.
        if ((long)(height - 1) * stride + width * 4 > bgraPixels.Length)
            return false;

        if (!EnsureTexture(width, height))
            return false;

        ContentUv = contentUv;

        var mapped = new D3D11_MAPPED_SUBRESOURCE();
        var hr = _context->Map((ID3D11Resource*)_texture, 0, D3D11_MAP_WRITE_DISCARD, 0, &mapped);

        if (hr == DXGI.DXGI_ERROR_DEVICE_REMOVED || hr == DXGI.DXGI_ERROR_DEVICE_RESET)
        {
            ReleaseTexture();
            return false;
        }

        if (FAILED(hr))
            return false;

        var rowBytes = width * 4;
        fixed (byte* src = bgraPixels)
        {
            var dst = (byte*)mapped.pData;
            for (var y = 0; y < height; y++)
                Buffer.MemoryCopy(src + (long)y * stride, dst + (long)y * mapped.RowPitch, rowBytes, rowBytes);
        }

        _context->Unmap((ID3D11Resource*)_texture, 0);
        return true;
    }

    private bool EnsureTexture(int width, int height)
    {
        if (_texture is not null && _width == width && _height == height)
            return true;

        ReleaseTexture();

        var desc = new D3D11_TEXTURE2D_DESC
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = 1,
            ArraySize = 1,
            Format = DXGI_FORMAT_B8G8R8A8_UNORM,
            SampleDesc = new DXGI_SAMPLE_DESC { Count = 1, Quality = 0 },
            Usage = D3D11_USAGE_DYNAMIC,
            BindFlags = (uint)D3D11_BIND_SHADER_RESOURCE,
            CPUAccessFlags = (uint)D3D11_CPU_ACCESS_WRITE,
        };

        ID3D11Texture2D* texture;
        var hr = _device->CreateTexture2D(&desc, null, &texture);
        if (FAILED(hr))
            return false;

        ID3D11ShaderResourceView* srv;
        hr = _device->CreateShaderResourceView((ID3D11Resource*)texture, null, &srv);
        if (FAILED(hr))
        {
            texture->Release();
            return false;
        }

        _texture = texture;
        _srv = srv;
        _width = width;
        _height = height;
        return true;
    }

    private void ReleaseTexture()
    {
        if (_srv is not null)
        {
            _srv->Release();
            _srv = null;
        }

        if (_texture is not null)
        {
            _texture->Release();
            _texture = null;
        }

        _width = 0;
        _height = 0;
    }

    public void Dispose() => ReleaseTexture();
}
