using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace TjViewer.Core.Video;

/// <summary>桌面采集接口（BGRA 帧）。默认实现为 DXGI Desktop Duplication，失败回退 GDI（见 PRD FR-05）。</summary>
public interface IDesktopCapturer : IDisposable
{
    int Width { get; }
    int Height { get; }
    /// <summary>
    /// 采集一帧 BGRA 直接写入 <paramref name="target"/>（长度须不小于 Width*Height*4）。
    /// 无画面更新、尺寸变化或采集失败返回 false；调用方按帧率节拍重试。
    /// timeouts 仅对 DXGI 生效。
    /// </summary>
    bool CaptureFrameInto(byte[] target, int timeoutMs);
}

/// <summary>GDI 桌面采集（CopyFromScreen）。DXGI 不可用及安全桌面（UAC/锁屏）下的可靠实现。</summary>
public sealed class GdiCapturer : IDesktopCapturer
{
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int nIndex);
    private const int SM_CXSCREEN = 0, SM_CYSCREEN = 1;

    /// <summary>仅安全桌面代理需要：把采集线程绑定到当前输入桌面（winlogon）。</summary>
    [ThreadStatic] private static int _attachState; // 0=未尝试 1=已绑定 2=尝试失败

    private readonly bool _attachInputDesktop;
    private readonly Bitmap _bmp;
    private readonly Graphics _gfx;

    public int Width { get; }
    public int Height { get; }

    public GdiCapturer(bool attachInputDesktop = false)
    {
        _attachInputDesktop = attachInputDesktop;
        Width = GetSystemMetrics(SM_CXSCREEN);
        Height = GetSystemMetrics(SM_CYSCREEN);
        if (Width <= 0 || Height <= 0) { Width = 1280; Height = 720; }
        _bmp = new Bitmap(Width, Height, PixelFormat.Format32bppArgb);
        _gfx = Graphics.FromImage(_bmp);
    }

    public bool CaptureFrameInto(byte[] target, int timeoutMs)
    {
        var len = Width * Height * 4;
        if (target.Length < len) return false;
        try
        {
            if (_attachInputDesktop && _attachState == 0)
                _attachState = SecureDesktop.TryAttachThreadToInputDesktop(out _) ? 1 : 2;

            _gfx.CopyFromScreen(0, 0, 0, 0, new Size(Width, Height));
            var rect = new Rectangle(0, 0, Width, Height);
            var data = _bmp.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                var rowBytes = Width * 4;
                if (data.Stride == rowBytes)
                {
                    Marshal.Copy(data.Scan0, target, 0, len);
                }
                else
                {
                    for (var y = 0; y < Height; y++)
                        Marshal.Copy(data.Scan0 + y * data.Stride, target, y * rowBytes, rowBytes);
                }
                return true;
            }
            finally { _bmp.UnlockBits(data); }
        }
        catch { return false; }
    }

    public void Dispose()
    {
        _gfx.Dispose();
        _bmp.Dispose();
    }
}
