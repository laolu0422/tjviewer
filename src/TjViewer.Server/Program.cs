using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using TjViewer.Server;
using TjViewer.Server.Data;
using TjViewer.Server.Endpoints;
using TjViewer.Server.Logging;
using TjViewer.Server.Services;
using TjViewer.Server.Signaling;
using TjViewer.Shared.Logging;

var builder = WebApplication.CreateBuilder(args);

// ---- 本地文件日志：%ProgramData%\TjViewer\logs\server-yyyyMMdd.log（不可写时自动降级并告警）----
// 与设备端 client-yyyyMMdd.log 使用同一套目录/切割/格式规则，便于现场按日期取证排查。
builder.Logging.ClearProviders();
builder.Logging.SetMinimumLevel(LogLevel.Debug);
builder.Logging.AddSimpleConsole(o =>
{
    o.SingleLine = true;
    o.TimestampFormat = "yyyy-MM-dd HH:mm:ss.fff ";
});
builder.Logging.AddDebug();
builder.Logging.AddProvider(new FileLoggerProvider("server"));
// 框架自身的调试输出量大（EF SQL、Kestrel 连接细节），限制到告警/信息级，
// 保留应用与业务日志的 DEBUG 级别，避免运行日志被无意义内容淹没。
builder.Logging.AddFilter("Microsoft.EntityFrameworkCore", LogLevel.Warning);
builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Information);

// ---- 数据库（EF Core 参数化，禁止拼接 SQL，见 PRD §7.2）----
builder.Services.AddDbContext<AppDbContext>(o =>
    o.UseSqlite(builder.Configuration.GetConnectionString("Default") ?? "Data Source=tjviewer.db"));

// ---- JWT 签名密钥：来自配置或本地随机生成持久化，禁止硬编码（见 PRD §7.1）----
byte[] jwtKey;
var cfgKey = builder.Configuration["Jwt:Key"];
if (!string.IsNullOrWhiteSpace(cfgKey))
{
    jwtKey = Convert.FromBase64String(cfgKey);
}
else
{
    var keyFile = Path.Combine(builder.Environment.ContentRootPath, "jwt.key");
    if (File.Exists(keyFile))
    {
        jwtKey = Convert.FromBase64String(File.ReadAllText(keyFile).Trim());
    }
    else
    {
        jwtKey = RandomNumberGenerator.GetBytes(32);
        File.WriteAllText(keyFile, Convert.ToBase64String(jwtKey));
    }
}

builder.Services.AddSingleton(new TokenService(jwtKey));
builder.Services.AddSingleton<SmtpService>();
builder.Services.AddSingleton<ConnectionManager>();
builder.Services.AddSingleton<SignalHub>();
builder.Services.AddSingleton<RelayHub>();
builder.Services.AddSingleton<DeviceCodeRegistry>();

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(o =>
    {
        o.TokenValidationParameters = new TokenValidationParameters
        {
            ValidIssuer = "tjviewer",
            ValidAudience = "tjviewer",
            IssuerSigningKey = new SymmetricSecurityKey(jwtKey),
            ValidateIssuerSigningKey = true,
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromSeconds(30),
            NameClaimType = JwtRegisteredClaimNames.Sub,
            RoleClaimType = ClaimTypes.Role,
        };
    });
builder.Services.AddAuthorization();

// ---- 限流：认证接口防暴力破解，API 防滥用（见 PRD §7.1）----
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.AddPolicy("auth", httpCtx => RateLimitPartition.GetFixedWindowLimiter(
        httpCtx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 20, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
    o.AddPolicy("api", httpCtx => RateLimitPartition.GetFixedWindowLimiter(
        ClaimsHelper.UserId(httpCtx.User) ?? httpCtx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 120, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
});

// ---- CORS：限定可信来源，禁止通配符+凭据（见 PRD §7.7）----
var origins = builder.Configuration.GetSection("Cors:Origins").Get<string[]>() ?? Array.Empty<string>();
builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
{
    if (origins.Length > 0) p.WithOrigins(origins).AllowAnyHeader().AllowAnyMethod().AllowCredentials();
}));

var app = builder.Build();

// ---- 启动/停止与未处理异常记录（便于按日期日志定位线上问题）----
FileLog.Info($"TjViewer 服务端启动：版本={System.Reflection.Assembly.GetExecutingAssembly().GetName().Version} " +
             $"环境={app.Environment.EnvironmentName} 内容根={app.Environment.ContentRootPath} 日志目录={FileLog.ActiveDirectory}");
app.Lifetime.ApplicationStarted.Register(() =>
    FileLog.Info("服务端已开始监听：" + string.Join(", ", app.Urls)));
app.Lifetime.ApplicationStopping.Register(() => FileLog.Info("服务端正在停止"));

// ---- 初始化数据库 ----
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Database.EnsureCreated();

    // 引导首个管理员：优先配置，否则随机生成强口令写入本地 admin.credentials（已 gitignore，不入库/不入日志）
    if (!db.Users.Any(u => u.IsAdmin))
    {
        var au = builder.Configuration["Admin:Username"] ?? "admin";
        var ap = builder.Configuration["Admin:Password"];
        if (string.IsNullOrWhiteSpace(ap))
        {
            ap = Convert.ToBase64String(RandomNumberGenerator.GetBytes(18));
            var credFile = Path.Combine(builder.Environment.ContentRootPath, "admin.credentials");
            File.WriteAllText(credFile, $"username={au}\npassword={ap}\n");
        }
        db.Users.Add(new User
        {
            Username = au,
            Email = builder.Configuration["Admin:Email"] ?? $"{au}@tjviewer.local",
            PasswordHash = PasswordHasher.Hash(ap),
            IsAdmin = true,
        });
        db.SaveChanges();
    }
}

// ---- 未处理异常：写入文件日志后继续按框架默认行为处理 ----
app.Use(async (ctx, next) =>
{
    try
    {
        await next();
    }
    catch (Exception ex)
    {
        FileLog.Error($"请求处理异常：{ctx.Request.Method} {ctx.Request.Path}{ctx.Request.QueryString}", ex);
        throw;
    }
});

// HTTPS 重定向仅在服务端自身启用 HTTPS 时生效。
// 推荐的部署方式是 Nginx/Caddy 终止 TLS、后端走 HTTP；此时若强行启用重定向，
// 会因找不到 HTTPS 端口而在每次启动时输出 “Failed to determine the https port” 告警。
var httpsEnabled = builder.Configuration.GetValue<bool>("Https:Enabled");
if (!app.Environment.IsDevelopment() && httpsEnabled)
{
    app.UseHsts();
    app.UseHttpsRedirection();
}

// ---- 安全响应头（见 PRD §7.7）----
app.Use(async (ctx, next) =>
{
    ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
    ctx.Response.Headers["X-Frame-Options"] = "DENY";
    ctx.Response.Headers["Referrer-Policy"] = "no-referrer";
    await next();
});

app.UseCors();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();
app.UseWebSockets();
app.UseStaticFiles();

app.MapAuth();
app.MapDevices();
app.MapCodes();
app.MapAdmin();
app.MapRelay();

app.MapGet("/api/health", () => Results.Ok(new { status = "ok", time = DateTimeOffset.UtcNow }));

app.Map("/ws/signal", async (HttpContext ctx, SignalHub hub) =>
{
    if (!ctx.WebSockets.IsWebSocketRequest)
    {
        ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
        return;
    }
    using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
    await hub.HandleAsync(ctx, ws);
});

app.Map("/ws/relay", async (HttpContext ctx, RelayHub relay) =>
{
    if (!ctx.WebSockets.IsWebSocketRequest)
    {
        ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
        return;
    }
    using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
    await relay.HandleAsync(ctx, ws);
});

app.Run();
