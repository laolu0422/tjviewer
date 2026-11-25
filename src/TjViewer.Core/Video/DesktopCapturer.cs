using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace TjViewer.Core.Video;

/// <summary>桌面采集接口（BGRA 帧）。DXGI 为低延迟优化方向，GDI 为可靠基线（见 PRD FR-05）。</summary>
public interface IDesktopCapturer : IDisposable
{
    int Width { get; }
    int Height { get; }
    /// <summary>采集一帧 BGRA；无变化或失败可返回 null。</summary>
    byte[]? CaptureFrame();
}

/// <summary>GDI 桌面采集（CopyFromScreen）。可靠、全托管；高帧率/低延迟场景建议升级 DXGI Desktop Duplication。</summary>
public sealed class GdiCapturer : IDesktopCapturer
{
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int nIndex);
    private const int SM_CXSCREEN = 0, SM_CYSCREEN = 1;

    public int Width { get; }
    public int Height { get; }
    private readonly Bitmap _bmp;
    private readonly Graphics _gfx;
    private readonly byte[] _buffer;

    public GdiCapturer()
    {
        Width = GetSystemMetrics(SM_CXSCREEN);
        Height = GetSystemMetrics(SM_CYSCREEN);
        if (Width <= 0 || Height <= 0) { Width = 1280; Height = 720; }
        _bmp = new Bitmap(Width, Height, PixelFormat.Format32bppArgb);
        _gfx = Graphics.FromImage(_bmp);
        _buffer = new byte[Width * Height * 4];
    }

    public byte[]? CaptureFrame()
    {
        try
        {
            _gfx.CopyFromScreen(0, 0, 0, 0, new Size(Width, Height));
            var rect = new Rectangle(0, 0, Width, Height);
            var data = _bmp.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                Marshal.Copy(data.Scan0, _buffer, 0, _buffer.Length);
                return _buffer;
            }
            finally { _bmp.UnlockBits(data); }
        }
        catch { return null; }
    }

    public void Dispose()
    {
        _gfx.Dispose();
        _bmp.Dispose();
    }
}
