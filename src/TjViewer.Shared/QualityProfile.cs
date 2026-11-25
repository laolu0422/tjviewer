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

    /// <summary>手动帧率可选值（与画质档位独立调节）。</summary>
    public static readonly int[] FpsOptions = { 30, 25, 20, 15 };

    /// <summary>自适应帧率下限保护（见 PRD FR-05 / §6）。</summary>
    public const int MinAdaptiveFps = 15;

    public static QualityTier Default => Tiers[1];
}
