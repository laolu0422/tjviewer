using System.Runtime.InteropServices;
using SharpGen.Runtime;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using ResultCode = Vortice.DXGI.ResultCode;

namespace TjViewer.Core.Video;

/// <summary>
/// DXGI Desktop Duplication 桌面采集（见 PRD FR-05）：GPU 侧取帧，鼠标指针由 Win32 光标信息合成
/// （桌面复制不含光标；部分远程会话下 DXGI 指针信息不可用，故统一走 GetCursorInfo）。
/// 初始化失败时由调用方回退 <see cref="GdiCapturer"/>。
/// </summary>
public sealed class DxgiCapturer : IDesktopCapturer
{
    private readonly object _lock = new();
    private IDXGIAdapter1? _adapter;
    private IDXGIOutput1? _output;
    private ID3D11Device? _device;
    private ID3D11DeviceContext? _context;
    private IDXGIOutputDuplication? _duplication;
    private ID3D11Texture2D? _staging;
    private int _stagingWidth;
    private int _stagingHeight;
    private byte[] _raw = Array.Empty<byte>();   // 桌面原图（不含指针）
    private byte[] _frame = Array.Empty<byte>(); // 合成指针后的输出帧

    private IntPtr _cursorHandle;
    private CursorShape? _cursor;
    private int _lastCursorX = int.MinValue;
    private int _lastCursorY = int.MinValue;
    private bool _lastCursorVisible;

    public int Width { get; private set; }
    public int Height { get; private set; }
    public string? LastError { get; private set; }

    public DxgiCapturer()
    {
        try
        {
            using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
            for (uint ai = 0; _adapter is null; ai++)
            {
                if (factory.EnumAdapters1(ai, out IDXGIAdapter1? a).Failure || a is null) break;
                IDXGIOutput? found = null;
                for (uint oi = 0; ; oi++)
                {
                    if (a.EnumOutputs(oi, out IDXGIOutput? o).Failure || o is null) break;
                    var d = o.Description;
                    // 以主显示器（桌面坐标原点）为采集目标
                    if (d.AttachedToDesktop && d.DesktopCoordinates.Left == 0 && d.DesktopCoordinates.Top == 0) { found = o; break; }
                    o.Dispose();
                }
                if (found is null) { a.Dispose(); continue; }
                _adapter = a;
                _output = found.QueryInterface<IDXGIOutput1>();
                found.Dispose();
            }
            if (_adapter is null || _output is null) throw new NotSupportedException("未找到可复制的显示输出");

            var r = D3D11.D3D11CreateDevice(_adapter, DriverType.Unknown, DeviceCreationFlags.BgraSupport, null!,
                out ID3D11Device? dev, out ID3D11DeviceContext? ctx);
            if (r.Failure || dev is null || ctx is null) throw new NotSupportedException("D3D11 设备创建失败：" + r.Description);
            _device = dev;
            _context = ctx;

            RealizeDuplication();
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    private void RealizeDuplication()
    {
        _duplication?.Dispose();
        _duplication = null;
        _staging?.Dispose();
        _staging = null;

        _duplication = _output!.DuplicateOutput(_device!);
        var desc = _duplication.Description;
        if (desc.Rotation is not (ModeRotation.Identity or ModeRotation.Unspecified))
            throw new NotSupportedException("暂不支持旋转的显示输出");

        Width = (int)desc.ModeDescription.Width;
        Height = (int)desc.ModeDescription.Height;
        if (Width <= 0 || Height <= 0) throw new NotSupportedException("显示输出尺寸无效");

        _raw = new byte[Width * Height * 4];
        _frame = new byte[_raw.Length];
    }

    public byte[]? CaptureFrame(int timeoutMs)
    {
        lock (_lock)
        {
            if (_duplication is null) return null;
            try
            {
                return CaptureOnce(timeoutMs);
            }
            catch (SharpGenException ex) when (ex.ResultCode.Code == ResultCode.AccessLost.Code)
            {
                // 锁屏、切换分辨率/UAC 等会重置桌面复制，需要重建
                LastError = "桌面复制被重置，重建中";
                try { RealizeDuplication(); } catch (Exception e) { LastError = e.Message; }
                return null;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                return null;
            }
        }
    }

    private byte[]? CaptureOnce(int timeoutMs)
    {
        var gotDesktop = false;
        var r = _duplication!.AcquireNextFrame((uint)Math.Max(0, timeoutMs), out var info, out var resource);
        var timedOut = r.Code == ResultCode.WaitTimeout.Code;
        if (!timedOut)
        {
            if (r.Failure) r.CheckError();
            try
            {
                if (info.AccumulatedFrames > 0 && resource is not null)
                {
                    using (resource)
                    using (var tex = resource!.QueryInterface<ID3D11Texture2D>())
                    {
                        EnsureStaging(tex);
                        _context!.CopyResource(_staging!, tex);
                        var map = _context.Map(_staging!, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
                        try { CopyRows(map.DataPointer, (int)map.RowPitch); }
                        finally { _context.Unmap(_staging!, 0); }
                    }
                    gotDesktop = true;
                }
            }
            finally
            {
                try { _duplication.ReleaseFrame(); } catch (SharpGenException) { }
            }
        }

        // 指针：光标形态/位置变化时也需要补发一帧，否则远程看不到指针移动
        var (hCursor, posX, posY, visible) = CursorShape.Query();
        var shapeChanged = UpdateCursorShape(hCursor);
        var cursorMoved = posX != _lastCursorX || posY != _lastCursorY || visible != _lastCursorVisible;
        _lastCursorX = posX;
        _lastCursorY = posY;
        _lastCursorVisible = visible;

        if (!gotDesktop && !cursorMoved && !shapeChanged) return null;
        if (_raw.Length == 0) return null;
        if (!visible || _cursor is null) return _raw;

        Buffer.BlockCopy(_raw, 0, _frame, 0, _raw.Length);
        DrawCursor(_frame, posX, posY);
        return _frame;
    }

    private bool UpdateCursorShape(IntPtr hCursor)
    {
        if (hCursor == _cursorHandle && _cursor is not null) return false;
        _cursorHandle = hCursor;
        _cursor = CursorShape.Read(hCursor);
        return true;
    }

    private void EnsureStaging(ID3D11Texture2D tex)
    {
        var d = tex.Description;
        var w = (int)d.Width;
        var h = (int)d.Height;
        if (_staging is not null && _stagingWidth == w && _stagingHeight == h) return;

        _staging?.Dispose();
        var sd = d;
        sd.Usage = ResourceUsage.Staging;
        sd.BindFlags = BindFlags.None;
        sd.CPUAccessFlags = CpuAccessFlags.Read;
        sd.MiscFlags = ResourceOptionFlags.None;
        sd.MipLevels = 1;
        sd.ArraySize = 1;
        _staging = _device!.CreateTexture2D(sd);
        _stagingWidth = w;
        _stagingHeight = h;

        // 分辨率变化时同步尺寸（调用方据 Width/Height 重建编码器）
        if (w != Width || h != Height)
        {
            Width = w;
            Height = h;
            _raw = new byte[w * h * 4];
            _frame = new byte[_raw.Length];
        }
    }

    private void CopyRows(IntPtr src, int pitch)
    {
        var rowBytes = Width * 4;
        if (pitch == rowBytes)
        {
            Marshal.Copy(src, _raw, 0, _raw.Length);
            return;
        }
        for (var y = 0; y < Height; y++)
            Marshal.Copy(src + y * pitch, _raw, y * rowBytes, rowBytes);
    }

    private void DrawCursor(byte[] dst, int posX, int posY)
    {
        var cur = _cursor!;
        var w = cur.Width;
        var h = cur.Height;
        if (w <= 0 || h <= 0) return;

        // 光标坐标为热点位置，左上角需减去热点偏移
        var left = posX - cur.HotspotX;
        var top = posY - cur.HotspotY;

        for (var y = 0; y < h; y++)
        {
            var dy = top + y;
            if (dy < 0 || dy >= Height) continue;
            for (var x = 0; x < w; x++)
            {
                var dx = left + x;
                if (dx < 0 || dx >= Width) continue;
                var si = (y * w + x) * 4;
                if (si + 3 >= cur.Pixels.Length) return;
                var di = (dy * Width + dx) * 4;

                if (!cur.Mono)
                {
                    int a = cur.Pixels[si + 3];
                    if (a == 0) continue;
                    if (a == 255)
                    {
                        dst[di] = cur.Pixels[si];
                        dst[di + 1] = cur.Pixels[si + 1];
                        dst[di + 2] = cur.Pixels[si + 2];
                    }
                    else
                    {
                        dst[di] = (byte)((cur.Pixels[si] * a + dst[di] * (255 - a)) / 255);
                        dst[di + 1] = (byte)((cur.Pixels[si + 1] * a + dst[di + 1] * (255 - a)) / 255);
                        dst[di + 2] = (byte)((cur.Pixels[si + 2] * a + dst[di + 2] * (255 - a)) / 255);
                    }
                }
                else
                {
                    // 单色光标：上半为 AND 掩码、下半为 XOR 掩码
                    var ai = ((y + cur.Height) * w + x) * 4;
                    if (ai + 3 >= cur.Pixels.Length) return;
                    var andOn = cur.Pixels[ai] != 0;
                    var xorOn = cur.Pixels[si] != 0;
                    if (andOn && !xorOn) continue;                       // 透明
                    if (!andOn && !xorOn) { dst[di] = dst[di + 1] = dst[di + 2] = 0; }
                    else if (!andOn) { dst[di] = dst[di + 1] = dst[di + 2] = 255; }
                    else { dst[di] ^= 255; dst[di + 1] ^= 255; dst[di + 2] ^= 255; }
                }
            }
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _duplication?.Dispose();
            _duplication = null;
            _staging?.Dispose();
            _staging = null;
            _output?.Dispose();
            _output = null;
            _context?.Dispose();
            _context = null;
            _device?.Dispose();
            _device = null;
            _adapter?.Dispose();
            _adapter = null;
        }
    }
}

/// <summary>Win32 光标读取（GetCursorInfo / GetIconInfo + GetDIBits），转为 32 位 BGRA 供合成。</summary>
internal sealed class CursorShape
{
    public int Width;
    public int Height;
    public int HotspotX;
    public int HotspotY;
    public bool Mono;
    public byte[] Pixels = Array.Empty<byte>();

    private const int CURSOR_SHOWING = 0x00000001;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct CURSORINFO
    {
        public int cbSize;
        public int flags;
        public IntPtr hCursor;
        public POINT ptScreenPos;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ICONINFO
    {
        public bool fIcon;
        public int xHotspot;
        public int yHotspot;
        public IntPtr hbmMask;
        public IntPtr hbmColor;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAP
    {
        public int bmType;
        public int bmWidth;
        public int bmHeight;
        public int bmWidthBytes;
        public ushort bmPlanes;
        public ushort bmBitsPixel;
        public IntPtr bmBits;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public int biSize;
        public int biWidth;
        public int biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public int biCompression;
        public int biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public int biClrUsed;
        public int biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RGBQUAD { public byte Blue, Green, Red, Reserved; }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFO
    {
        public BITMAPINFOHEADER bmiHeader;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 1)]
        public RGBQUAD[] bmiColors;
    }

    [DllImport("user32.dll")] private static extern bool GetCursorInfo(ref CURSORINFO pci);
    [DllImport("user32.dll")] private static extern bool GetIconInfo(IntPtr hIcon, out ICONINFO piconinfo);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
    [DllImport("gdi32.dll")] private static extern bool GetObject(IntPtr hgdiobj, int cbBuffer, out BITMAP lpvObject);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr ho);
    [DllImport("gdi32.dll")]
    private static extern int GetDIBits(IntPtr hdc, IntPtr hbm, uint start, uint cLines, byte[] lpvBits, ref BITMAPINFO lpbmi, uint usage);

    /// <summary>返回当前光标句柄、屏幕坐标（热点）与可见性。</summary>
    public static (IntPtr Handle, int X, int Y, bool Visible) Query()
    {
        var ci = new CURSORINFO { cbSize = Marshal.SizeOf<CURSORINFO>() };
        if (!GetCursorInfo(ref ci) || ci.hCursor == IntPtr.Zero) return (IntPtr.Zero, 0, 0, false);
        return (ci.hCursor, ci.ptScreenPos.X, ci.ptScreenPos.Y, ci.flags == CURSOR_SHOWING);
    }

    /// <summary>读取光标位图（彩色 32bpp；单色为 AND+XOR 两段）。</summary>
    public static CursorShape? Read(IntPtr hCursor)
    {
        if (hCursor == IntPtr.Zero) return null;
        if (!GetIconInfo(hCursor, out var ii)) return null;
        var hdc = GetDC(IntPtr.Zero);
        try
        {
            if (!GetObject(ii.hbmMask, Marshal.SizeOf<BITMAP>(), out var bmMask)) return null;
            var maskWidth = bmMask.bmWidth;
            var maskHeight = bmMask.bmHeight;
            if (maskWidth <= 0 || maskHeight <= 0) return null;

            var shape = new CursorShape { HotspotX = ii.xHotspot, HotspotY = ii.yHotspot };

            if (ii.hbmColor != IntPtr.Zero)
            {
                if (!GetObject(ii.hbmColor, Marshal.SizeOf<BITMAP>(), out var bmColor)) return null;
                var w = bmColor.bmWidth > 0 ? bmColor.bmWidth : maskWidth;
                var h = bmColor.bmHeight > 0 ? bmColor.bmHeight : maskHeight;
                var pixels = new byte[w * h * 4];
                if (!ReadBits(hdc, ii.hbmColor, w, h, pixels)) return null;

                var hasAlpha = false;
                for (var i = 3; i < pixels.Length; i += 4)
                    if (pixels[i] != 0) { hasAlpha = true; break; }
                if (!hasAlpha)
                    for (var i = 3; i < pixels.Length; i += 4) pixels[i] = 255; // 旧式光标无 alpha

                shape.Width = w;
                shape.Height = h;
                shape.Pixels = pixels;
                return shape;
            }

            // 单色光标：掩码位图为 AND（上半）+ XOR（下半）
            if (maskHeight % 2 != 0) return null;
            var mono = new byte[maskWidth * maskHeight * 4];
            if (!ReadBits(hdc, ii.hbmMask, maskWidth, maskHeight, mono)) return null;
            shape.Width = maskWidth;
            shape.Height = maskHeight / 2;
            shape.Mono = true;
            shape.Pixels = mono;
            return shape;
        }
        finally
        {
            if (ii.hbmColor != IntPtr.Zero) DeleteObject(ii.hbmColor);
            if (ii.hbmMask != IntPtr.Zero) DeleteObject(ii.hbmMask);
            if (hdc != IntPtr.Zero) ReleaseDC(IntPtr.Zero, hdc);
        }
    }

    private static bool ReadBits(IntPtr hdc, IntPtr hbm, int w, int h, byte[] dst)
    {
        var bi = new BITMAPINFO
        {
            bmiHeader = new BITMAPINFOHEADER
            {
                biSize = Marshal.SizeOf<BITMAPINFOHEADER>(),
                biWidth = w,
                biHeight = -h, // 负数表示自上而下，与屏幕坐标一致
                biPlanes = 1,
                biBitCount = 32,
                biCompression = 0,
                biSizeImage = w * h * 4,
            },
            bmiColors = new RGBQUAD[1],
        };
        return GetDIBits(hdc, hbm, 0, (uint)h, dst, ref bi, 0) != 0;
    }
}
