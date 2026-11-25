using System.Runtime.InteropServices;
using SharpGen.Runtime;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using ResultCode = Vortice.DXGI.ResultCode;

namespace TjViewer.Core.Video;

/// <summary>
/// DXGI Desktop Duplication 桌面采集（见 PRD FR-05）：GPU 侧取帧，画面不再合成鼠标指针，
/// 由控制端本地渲染原生光标。桌面无更新时返回上一帧，配合发送端按设定帧率持续推流，
/// 避免静止画面停止刷新导致操作反馈延迟。
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
    private byte[] _frame = Array.Empty<byte>();

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

        _frame = new byte[Width * Height * 4];
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

        if (_frame.Length == 0) return null;
        // 无桌面更新/超时也返回上一帧：由发送端按帧率持续编码推流，保证操作即时反馈
        _ = gotDesktop;
        return _frame;
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

        // 分辨率变化时同步尺寸（调用方据 Width/Height 重建编码器与通知对端）
        if (w != Width || h != Height)
        {
            Width = w;
            Height = h;
            _frame = new byte[w * h * 4];
        }
    }

    private void CopyRows(IntPtr src, int pitch)
    {
        var rowBytes = Width * 4;
        if (pitch == rowBytes)
        {
            Marshal.Copy(src, _frame, 0, _frame.Length);
            return;
        }
        for (var y = 0; y < Height; y++)
            Marshal.Copy(src + y * pitch, _frame, y * rowBytes, rowBytes);
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
