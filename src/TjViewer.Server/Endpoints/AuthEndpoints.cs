using Microsoft.EntityFrameworkCore;
using TjViewer.Server.Data;
using TjViewer.Server.Services;
using TjViewer.Shared;

namespace TjViewer.Server.Endpoints;

public static class AuthEndpoints
{
    public static void MapAuth(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/auth").RequireRateLimiting("auth");

        g.MapPost("/register", async (RegisterRequest req, AppDbContext db, TokenService tokens) =>
        {
            if (!Validation.ValidUsername(req.Username)) return Results.BadRequest(new { error = "用户名格式无效（3–32 位字母/数字/下划线）" });
            if (!Validation.ValidEmail(req.Email)) return Results.BadRequest(new { error = "邮箱格式无效" });
            if (!Validation.ValidPassword(req.Password)) return Results.BadRequest(new { error = "密码至少 8 位且含字母与数字" });

            var uname = req.Username.Trim();
            var email = req.Email.Trim().ToLowerInvariant();
            if (await db.Users.AnyAsync(u => u.Username == uname)) return Results.Conflict(new { error = "用户名已存在" });
            if (await db.Users.AnyAsync(u => u.Email == email)) return Results.Conflict(new { error = "邮箱已存在" });

            var user = new User { Username = uname, Email = email, PasswordHash = PasswordHasher.Hash(req.Password) };
            db.Users.Add(user);
            await db.SaveChangesAsync();
            return Results.Ok(await IssueTokens(user, db, tokens));
        });

        g.MapPost("/login", async (LoginRequest req, AppDbContext db, TokenService tokens) =>
        {
            var account = (req.Account ?? "").Trim();
            var email = account.ToLowerInvariant();
            var user = await db.Users.FirstOrDefaultAsync(u => u.Username == account || u.Email == email);
            // 防用户枚举：账号不存在与密码错误返回一致措辞
            if (user is null || !PasswordHasher.Verify(req.Password ?? "", user.PasswordHash))
                return Results.Json(new { error = "账号或密码错误" }, statusCode: StatusCodes.Status401Unauthorized);
            if (user.Status == AccountStatus.Disabled)
                return Results.Json(new { error = "账户已被禁用，请联系管理员" }, statusCode: StatusCodes.Status403Forbidden);
            return Results.Ok(await IssueTokens(user, db, tokens));
        });

        g.MapPost("/refresh", async (RefreshRequest req, AppDbContext db, TokenService tokens) =>
        {
            var hash = TokenService.HashToken(req.RefreshToken ?? "");
            var rt = await db.RefreshTokens.FirstOrDefaultAsync(t => t.TokenHash == hash && !t.Revoked);
            if (rt is null || rt.ExpiresAt < DateTimeOffset.UtcNow)
                return Results.Json(new { error = "刷新令牌无效或已过期" }, statusCode: StatusCodes.Status401Unauthorized);
            var user = await db.Users.FindAsync(rt.UserId);
            if (user is null || user.Status == AccountStatus.Disabled)
                return Results.Json(new { error = "账户不可用" }, statusCode: StatusCodes.Status401Unauthorized);
            rt.Revoked = true; // 刷新轮换
            await db.SaveChangesAsync();
            return Results.Ok(await IssueTokens(user, db, tokens));
        });

        g.MapPost("/reset-request", async (PasswordResetRequest req, AppDbContext db, SmtpService smtp, IConfiguration cfg, ILoggerFactory lf) =>
        {
            var log = lf.CreateLogger("Auth.Reset");
            var email = (req.Email ?? "").Trim().ToLowerInvariant();
            var user = await db.Users.FirstOrDefaultAsync(u => u.Email == email);
            if (user is not null && smtp.IsConfigured)
            {
                var token = TokenService.NewOpaqueToken();
                db.PasswordResetTokens.Add(new PasswordResetToken
                {
                    UserId = user.Id,
                    TokenHash = TokenService.HashToken(token),
                    ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(30),
                });
                await db.SaveChangesAsync();

                var baseUrl = (cfg["PublicBaseUrl"] ?? "http://localhost:5000").TrimEnd('/');
                var link = $"{baseUrl}/reset?token={Uri.EscapeDataString(token)}";
                var body = $"<p>点击链接重置密码（30 分钟内有效，仅可使用一次）：</p><p><a href=\"{link}\">{link}</a></p><p>若非本人操作请忽略此邮件。</p>";
                try { await smtp.SendAsync(user.Email, "重置你的 TjViewer 密码", body); }
                catch (Exception ex) { log.LogWarning(ex, "SMTP 发送失败"); }
            }
            // 防用户枚举：无论邮箱是否注册，响应一致
            return Results.Ok(new { message = "若该邮箱已注册，将收到重置邮件" });
        });

        g.MapPost("/reset-confirm", async (PasswordResetConfirm req, AppDbContext db) =>
        {
            if (!Validation.ValidPassword(req.NewPassword)) return Results.BadRequest(new { error = "密码至少 8 位且含字母与数字" });
            var hash = TokenService.HashToken(req.Token ?? "");
            var rt = await db.PasswordResetTokens.FirstOrDefaultAsync(t => t.TokenHash == hash);
            if (rt is null || rt.UsedAt is not null || rt.ExpiresAt < DateTimeOffset.UtcNow)
                return Results.BadRequest(new { error = "重置链接无效或已过期，请重新发起" });
            var user = await db.Users.FindAsync(rt.UserId);
            if (user is null) return Results.BadRequest(new { error = "重置链接无效" });

            user.PasswordHash = PasswordHasher.Hash(req.NewPassword);
            rt.UsedAt = DateTimeOffset.UtcNow;
            await db.RefreshTokens.Where(t => t.UserId == user.Id && !t.Revoked)
                .ExecuteUpdateAsync(s => s.SetProperty(t => t.Revoked, true));
            await db.SaveChangesAsync();
            return Results.Ok(new { message = "密码已重置，请重新登录" });
        });

        g.MapPost("/change-password", async (ChangePasswordRequest req, AppDbContext db, System.Security.Claims.ClaimsPrincipal cp) =>
        {
            var uid = ClaimsHelper.UserId(cp);
            var user = await db.Users.FindAsync(uid);
            if (user is null) return Results.Unauthorized();
            if (!PasswordHasher.Verify(req.OldPassword ?? "", user.PasswordHash)) return Results.BadRequest(new { error = "旧密码错误" });
            if (!Validation.ValidPassword(req.NewPassword)) return Results.BadRequest(new { error = "密码至少 8 位且含字母与数字" });
            user.PasswordHash = PasswordHasher.Hash(req.NewPassword);
            await db.SaveChangesAsync();
            return Results.Ok(new { message = "密码已修改" });
        }).RequireAuthorization();
    }

    private static async Task<AuthResponse> IssueTokens(User user, AppDbContext db, TokenService tokens)
    {
        var access = tokens.CreateAccessToken(user);
        var refresh = TokenService.NewOpaqueToken();
        db.RefreshTokens.Add(new RefreshToken
        {
            UserId = user.Id,
            TokenHash = TokenService.HashToken(refresh),
            ExpiresAt = DateTimeOffset.UtcNow.Add(tokens.RefreshLifetime),
        });
        await db.SaveChangesAsync();
        return new AuthResponse(access, refresh, user.Username, user.Email, DateTimeOffset.UtcNow.Add(tokens.AccessLifetime).ToUnixTimeSeconds());
    }
}
