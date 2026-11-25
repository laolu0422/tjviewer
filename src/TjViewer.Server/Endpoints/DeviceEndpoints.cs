using Microsoft.EntityFrameworkCore;
using TjViewer.Server.Data;
using TjViewer.Shared;

namespace TjViewer.Server.Endpoints;

public static class DeviceEndpoints
{
    public static void MapDevices(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/devices").RequireAuthorization().RequireRateLimiting("api");

        g.MapPost("/", async (RegisterDeviceRequest req, AppDbContext db, System.Security.Claims.ClaimsPrincipal cp) =>
        {
            var uid = ClaimsHelper.UserId(cp);
            if (uid is null) return Results.Unauthorized();
            if (string.IsNullOrWhiteSpace(req.DeviceKey) || req.DeviceKey.Length > 128) return Results.BadRequest(new { error = "设备标识无效" });
            if (!Validation.ValidDeviceName(req.DeviceName)) return Results.BadRequest(new { error = "设备名无效" });
            if (!Validation.ValidNote(req.Note)) return Results.BadRequest(new { error = "备注过长" });

            var existing = await db.Devices.FirstOrDefaultAsync(d => d.DeviceKey == req.DeviceKey);
            if (existing is not null && existing.OwnerId != uid)
                return Results.Conflict(new { error = "该设备已绑定其他账户" });

            if (existing is null)
            {
                existing = new Device { DeviceKey = req.DeviceKey, OwnerId = uid };
                db.Devices.Add(existing);
            }
            existing.Name = req.DeviceName.Trim();
            existing.Note = req.Note;
            existing.OsSummary = req.OsSummary ?? "";
            existing.Status = DeviceStatus.Online;
            existing.LastSeen = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
            return Results.Ok(ToDto(existing));
        });

        g.MapGet("/", async (AppDbContext db, System.Security.Claims.ClaimsPrincipal cp) =>
        {
            var uid = ClaimsHelper.UserId(cp);
            // SQLite 不支持 DateTimeOffset 的 ORDER BY 翻译；设备量小，取出后内存排序
            var list = await db.Devices.Where(d => d.OwnerId == uid).ToListAsync();
            return Results.Ok(list.OrderByDescending(d => d.LastSeen).Select(ToDto));
        });

        g.MapPatch("/{id}", async (string id, UpdateDeviceRequest req, AppDbContext db, System.Security.Claims.ClaimsPrincipal cp) =>
        {
            var uid = ClaimsHelper.UserId(cp);
            var d = await db.Devices.FirstOrDefaultAsync(x => x.Id == id && x.OwnerId == uid);
            if (d is null) return Results.NotFound(new { error = "设备不存在" });
            if (req.Name is not null)
            {
                if (!Validation.ValidDeviceName(req.Name)) return Results.BadRequest(new { error = "设备名无效" });
                d.Name = req.Name.Trim();
            }
            if (req.Note is not null)
            {
                if (!Validation.ValidNote(req.Note)) return Results.BadRequest(new { error = "备注过长" });
                d.Note = req.Note;
            }
            if (req.UnattendedEnabled is not null) d.UnattendedEnabled = req.UnattendedEnabled.Value;
            if (req.AutoStart is not null) d.AutoStart = req.AutoStart.Value;
            await db.SaveChangesAsync();
            return Results.Ok(ToDto(d));
        });

        g.MapDelete("/{id}", async (string id, AppDbContext db, System.Security.Claims.ClaimsPrincipal cp) =>
        {
            var uid = ClaimsHelper.UserId(cp);
            var d = await db.Devices.FirstOrDefaultAsync(x => x.Id == id && x.OwnerId == uid);
            if (d is null) return Results.NotFound(new { error = "设备不存在" });
            db.Devices.Remove(d);
            await db.SaveChangesAsync();
            return Results.Ok(new { message = "设备已解绑" });
        });

        g.MapPost("/heartbeat", async (HeartbeatRequest req, AppDbContext db, System.Security.Claims.ClaimsPrincipal cp) =>
        {
            var uid = ClaimsHelper.UserId(cp);
            var d = await db.Devices.FirstOrDefaultAsync(x => x.Id == req.DeviceId && x.OwnerId == uid);
            if (d is null) return Results.NotFound(new { error = "设备不存在" });
            d.LastSeen = DateTimeOffset.UtcNow;
            if (d.Status == DeviceStatus.Offline) d.Status = DeviceStatus.Online;
            await db.SaveChangesAsync();
            return Results.Ok(new { message = "ok" });
        });
    }

    private static DeviceDto ToDto(Device d)
        => new(d.Id, d.DeviceKey, d.Name, d.Note, d.Status, d.LastSeen, d.OsSummary, d.UnattendedEnabled, d.AutoStart);
}
