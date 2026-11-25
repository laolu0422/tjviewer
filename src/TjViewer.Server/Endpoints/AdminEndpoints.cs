using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using TjViewer.Server.Data;
using TjViewer.Server.Services;
using TjViewer.Shared;

namespace TjViewer.Server.Endpoints;

/// <summary>网页管理后台 API（见 PRD FR-11）。仅管理员角色可访问；所有写操作审计。</summary>
public static class AdminEndpoints
{
    public static void MapAdmin(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/admin").RequireAuthorization(p => p.RequireRole("admin")).RequireRateLimiting("api");

        g.MapGet("/users", async Task<IResult> (AppDbContext db) =>
        {
            var users = await db.Users.OrderBy(u => u.CreatedAt).ToListAsync();
            var counts = await db.Devices.GroupBy(d => d.OwnerId).Select(x => new { x.Key, C = x.Count() }).ToListAsync();
            var map = counts.ToDictionary(x => x.Key, x => x.C);
            return Results.Ok(users.Select(u => new
            {
                u.Id, u.Username, u.Email, status = u.Status.ToString(), u.IsAdmin, u.CreatedAt,
                deviceCount = map.GetValueOrDefault(u.Id, 0),
            }));
        });

        g.MapPost("/users/{id}/status", async Task<IResult> (string id, StatusUpdate req, AppDbContext db, System.Security.Claims.ClaimsPrincipal cp) =>
        {
            var actor = ClaimsHelper.UserId(cp);
            var u = await db.Users.FindAsync(id);
            if (u is null) return Results.NotFound(new { error = "用户不存在" });
            if (u.Id == actor) return Results.BadRequest(new { error = "不能禁用当前登录的管理员自己" });
            u.Status = req.Disabled ? AccountStatus.Disabled : AccountStatus.Active;
            if (req.Disabled)
                await db.RefreshTokens.Where(t => t.UserId == u.Id && !t.Revoked).ExecuteUpdateAsync(s => s.SetProperty(t => t.Revoked, true));
            db.AuditEvents.Add(new AuditEvent { ActorId = actor, Action = req.Disabled ? "user.disable" : "user.enable", Target = u.Id, Result = "ok" });
            await db.SaveChangesAsync();
            return Results.Ok(new { message = "已更新", status = u.Status.ToString() });
        });

        g.MapDelete("/users/{id}", async Task<IResult> (string id, AppDbContext db, System.Security.Claims.ClaimsPrincipal cp) =>
        {
            var actor = ClaimsHelper.UserId(cp);
            var u = await db.Users.FindAsync(id);
            if (u is null) return Results.NotFound(new { error = "用户不存在" });
            if (u.Id == actor) return Results.BadRequest(new { error = "不能删除当前登录的管理员自己" });
            db.Devices.RemoveRange(db.Devices.Where(d => d.OwnerId == u.Id));
            db.RefreshTokens.RemoveRange(db.RefreshTokens.Where(t => t.UserId == u.Id));
            db.PasswordResetTokens.RemoveRange(db.PasswordResetTokens.Where(t => t.UserId == u.Id));
            db.Users.Remove(u);
            db.AuditEvents.Add(new AuditEvent { ActorId = actor, Action = "user.delete", Target = u.Id, Result = "ok" });
            await db.SaveChangesAsync();
            return Results.Ok(new { message = "用户已删除" });
        });

        g.MapPost("/users/{id}/reset-password", async Task<IResult> (string id, AppDbContext db, SmtpService smtp, IConfiguration cfg, System.Security.Claims.ClaimsPrincipal cp) =>
        {
            var actor = ClaimsHelper.UserId(cp);
            var u = await db.Users.FindAsync(id);
            if (u is null) return Results.NotFound(new { error = "用户不存在" });
            var token = TokenService.NewOpaqueToken();
            db.PasswordResetTokens.Add(new PasswordResetToken { UserId = u.Id, TokenHash = TokenService.HashToken(token), ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(30) });
            db.AuditEvents.Add(new AuditEvent { ActorId = actor, Action = "user.reset-password", Target = u.Id, Result = "ok" });
            await db.SaveChangesAsync();

            if (smtp.IsConfigured)
            {
                var baseUrl = (cfg["PublicBaseUrl"] ?? "http://localhost:5000").TrimEnd('/');
                var link = $"{baseUrl}/reset?token={Uri.EscapeDataString(token)}";
                try { await smtp.SendAsync(u.Email, "管理员重置了你的 TjViewer 密码", $"<p>点击链接重置密码（30 分钟有效）：</p><p><a href=\"{link}\">{link}</a></p>"); }
                catch { }
                return Results.Ok(new { message = "重置邮件已发送", sentEmail = true });
            }
            // SMTP 未配置时返回一次性重置令牌，由管理员转交用户
            return Results.Ok(new { message = "SMTP 未配置，请转交重置令牌", sentEmail = false, resetToken = token });
        });

        g.MapGet("/devices", async Task<IResult> (AppDbContext db) =>
        {
            var list = await db.Devices.ToListAsync();
            return Results.Ok(list.OrderByDescending(d => d.LastSeen).Select(d => new { d.Id, d.Name, d.OwnerId, status = d.Status.ToString(), d.LastSeen, d.OsSummary }));
        });

        g.MapGet("/sessions", async Task<IResult> (AppDbContext db) =>
        {
            var list = await db.Sessions.ToListAsync();
            return Results.Ok(list.OrderByDescending(s => s.CreatedAt).Take(200).Select(s => new { s.Id, s.ControllerDeviceId, s.HostDeviceId, state = s.State.ToString(), s.CreatedAt, s.EndedAt }));
        });

        g.MapGet("/audit", async Task<IResult> (AppDbContext db) =>
        {
            var list = await db.AuditEvents.ToListAsync();
            return Results.Ok(list.OrderByDescending(a => a.CreatedAt).Take(200));
        });

        g.MapGet("/config", async Task<IResult> (AppDbContext db) =>
        {
            var list = await db.SystemConfigs.ToListAsync();
            return Results.Ok(list.ToDictionary(c => c.Key, c => c.Value));
        });

        g.MapPut("/config", async Task<IResult> (ConfigUpdate req, AppDbContext db, System.Security.Claims.ClaimsPrincipal cp) =>
        {
            var actor = ClaimsHelper.UserId(cp);
            if (req.Values is null) return Results.BadRequest(new { error = "缺少配置项" });
            foreach (var kv in req.Values)
            {
                if (string.IsNullOrWhiteSpace(kv.Key) || kv.Key.Length > 64) continue;
                var existing = await db.SystemConfigs.FindAsync(kv.Key);
                if (existing is null) db.SystemConfigs.Add(new SystemConfig { Key = kv.Key, Value = kv.Value ?? "" });
                else existing.Value = kv.Value ?? "";
            }
            db.AuditEvents.Add(new AuditEvent { ActorId = actor, Action = "config.update", Target = "system", Result = "ok" });
            await db.SaveChangesAsync();
            return Results.Ok(new { message = "配置已保存" });
        });
    }

    public sealed record StatusUpdate(bool Disabled);
    public sealed record ConfigUpdate(Dictionary<string, string>? Values);
}
