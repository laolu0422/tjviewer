namespace TjViewer.Shared;

/// <summary>离散画质档位（见 PRD FR-05，默认值为假设可配置）。</summary>
public sealed record QualityTier(string Name, int Width, int Height, int MaxFps, int MaxBitrateKbps);

public static class QualityProfiles
{
    public static readonly QualityTier[] Tiers =
    {
        new("高", 1920, 1080, 30, 4000),
        new("中", 1280, 720, 25, 1500),
        new("低", 854, 480, 20, 600),
        new("极低", 640, 360, 15, 300),
    };

    /// <summary>手动帧率可选值（与画质档位独立调节，上限受服务端限制）。</summary>
    public static readonly int[] FpsOptions = { 15, 30, 40, 60, 90, 120 };

    /// <summary>码率可选值，2Mbps 一档，最高 10Mbps。</summary>
    public static readonly int[] BitrateOptions = { 2048, 4096, 6144, 8192, 10240 };

    /// <summary>自适应帧率下限保护（见 PRD FR-05 / §6）。</summary>
    public const int MinAdaptiveFps = 15;

    public static QualityTier Default => Tiers[1];
}

/// <summary>
/// 服务端硬限制（客户端可通过 /api/limits 获取）：4K / 120fps / 10Mbps。
/// 媒体端与中继端均按此钳制，避免客户端提交超限参数。
/// </summary>
public static class VideoLimits
{
    public const int MaxWidth = 3840;
    public const int MaxHeight = 2160;
    public const int MaxFps = 120;
    public const int MaxBitrateKbps = 10240;

    public static int ClampFps(int fps)
        => fps <= 0 ? 30 : Math.Clamp(fps, 5, MaxFps);

    public static int ClampBitrate(int kbps)
        => kbps <= 0 ? 2048 : Math.Clamp(kbps, 512, MaxBitrateKbps);

    /// <summary>按比例缩放到不超过 4K，宽高取偶数（H.264 要求）。</summary>
    public static (int Width, int Height) ClampSize(int width, int height)
    {
        if (width <= 0 || height <= 0) return (1280, 720);
        double scale = Math.Min(1.0, Math.Min((double)MaxWidth / width, (double)MaxHeight / height));
        int w = Math.Max(2, (int)(width * scale) & ~1);
        int h = Math.Max(2, (int)(height * scale) & ~1);
        return (w, h);
    }
}
