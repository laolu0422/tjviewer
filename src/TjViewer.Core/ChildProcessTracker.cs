using System.Collections.Concurrent;
using System.Diagnostics;

namespace TjViewer.Core;

/// <summary>
/// 跟踪本进程派生的子进程（ffmpeg 编解码器等）。
/// 「完全关闭」时统一终止，确保无残留后台进程；正常会话释放也会各自 Kill，这里兜底。
/// </summary>
public static class ChildProcessTracker
{
    private static readonly ConcurrentDictionary<int, Process> Children = new();

    public static void Track(Process p)
    {
        try { Children[p.Id] = p; } catch { }
    }

    /// <summary>终止所有仍在运行的子进程（含其子进程树）。幂等、可重复调用。</summary>
    public static void KillAll()
    {
        foreach (var kv in Children)
        {
            var p = kv.Value;
            try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { }
            try { p.Dispose(); } catch { }
        }
        Children.Clear();
    }
}
