using System.Diagnostics;
using System.Runtime.InteropServices;

namespace TjViewer.Core;

/// <summary>
/// Windows 运行时调优：1ms 定时器精度 + 高优先级调度。
/// 远控场景下 KCP 5ms 节拍、UI 渲染与 ffmpeg 管道都依赖精细定时；
/// 默认 15.6ms 的时钟粒度会直接抬高画面端到端时延。
/// </summary>
public static class RuntimeTuning
{
    [DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
    private static extern uint TimeBeginPeriod(uint milliseconds);

    private static int _applied;

    /// <summary>幂等；非 Windows 平台直接返回。</summary>
    public static void Apply()
    {
        if (!OperatingSystem.IsWindows()) return;
        if (Interlocked.Exchange(ref _applied, 1) == 1) return;

        try { TimeBeginPeriod(1); } catch { }

        // 安装器已通过 IFEO 设置 High；绿色运行时在这里兜底提升。
        // 不提升到 Realtime：实时优先级会抢占系统线程，反而造成卡顿。
        try { Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.High; } catch { }
    }
}
