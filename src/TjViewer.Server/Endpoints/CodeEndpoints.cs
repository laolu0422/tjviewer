using Microsoft.EntityFrameworkCore;
using TjViewer.Server.Data;
using TjViewer.Server.Services;
using TjViewer.Shared;

namespace TjViewer.Server.Endpoints;

/// <summary>服务端画质硬限制与陌生设备连接码（见需求 1 / 10）。</summary>
public static class CodeEndpoints
{
    public static void MapCodes(this IEndpointRouteBuilder app)
    {
        app.MapGet(ApiRoutes.Limits, () => Results.Ok(new VideoLimitsDto(
                VideoLimits.MaxWidth, VideoLimits.MaxHeight, VideoLimits.MaxFps, VideoLimits.MaxBitrateKbps)))
            .RequireAuthorization()
            .RequireRateLimiting("api");

        var g = app.MapGroup(ApiRoutes.Codes).RequireAuthorization().RequireRateLimiting("api");

        g.MapPost("/", async (CodesRequest req, AppDbContext db, DeviceCodeRegistry registry, System.Security.Claims.ClaimsPrincipal cp) =>
        {
            var uid = ClaimsHelper.UserId(cp);
            var dev = await db.Devices.FirstOrDefaultAsync(d => d.Id == req.DeviceId && d.OwnerId == uid);
            if (dev is null) return Results.NotFound(new { error = "设备不存在" });
            var codes = await registry.EnsureAsync(dev.Id, req.FixedCode);
            return Results.Ok(codes);
        });
    }
}

public sealed record VideoLimitsDto(int MaxWidth, int MaxHeight, int MaxFps, int MaxBitrateKbps);
