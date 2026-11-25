using System.Collections.Concurrent;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Http;

namespace TjViewer.Server.Endpoints;

/// <summary>
/// 小文件中转（P2P 失败回退，见 PRD FR-08/§5.3/§7.5）。
/// 内存暂存 AES-GCM 加密分块（不落明文盘），单文件≤50MB，TTL 清理，须鉴权。
/// </summary>
public static class RelayEndpoints
{
    private const long MaxSize = 50L * 1024 * 1024;
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(10);
    private static readonly ConcurrentDictionary<string, RelayItem> Items = new();
    private static int _cleanupStarted;

    private sealed record RelayItem(byte[] Cipher, byte[] Key, byte[] Nonce, byte[] Tag, string Name, DateTime Expiry);

    public static void MapRelay(this IEndpointRouteBuilder app)
    {
        if (Interlocked.Exchange(ref _cleanupStarted, 1) == 0)
            _ = Task.Run(CleanupLoop);

        var g = app.MapGroup("/api/relay").RequireAuthorization().RequireRateLimiting("api");

        g.MapPost("/upload", async (HttpContext ctx) =>
        {
            var name = ctx.Request.Headers["X-File-Name"].ToString();
            if (string.IsNullOrWhiteSpace(name)) name = "file";
            using var ms = new MemoryStream();
            var buf = new byte[81920];
            long total = 0;
            int n;
            while ((n = await ctx.Request.Body.ReadAsync(buf)) > 0)
            {
                total += n;
                if (total > MaxSize) return Results.Json(new { error = "文件超过中转上限（50MB）" }, statusCode: StatusCodes.Status413PayloadTooLarge);
                ms.Write(buf, 0, n);
            }
            var plain = ms.ToArray();
            var key = RandomNumberGenerator.GetBytes(32);
            var nonce = RandomNumberGenerator.GetBytes(12);
            var cipher = new byte[plain.Length];
            var tag = new byte[16];
            using (var aes = new AesGcm(key, 16))
                aes.Encrypt(nonce, plain, cipher, tag);
            var id = Guid.NewGuid().ToString("N");
            Items[id] = new RelayItem(cipher, key, nonce, tag, Path.GetFileName(name), DateTime.UtcNow.Add(Ttl));
            return Results.Ok(new { id, size = plain.Length });
        });

        g.MapGet("/{id}", (string id) =>
        {
            if (!Items.TryRemove(id, out var item)) return Results.NotFound(new { error = "中转文件不存在或已过期" });
            if (item.Expiry < DateTime.UtcNow) return Results.NotFound(new { error = "中转文件已过期" });
            var plain = new byte[item.Cipher.Length];
            using (var aes = new AesGcm(item.Key, 16))
                aes.Decrypt(item.Nonce, item.Cipher, item.Tag, plain);
            return Results.File(plain, "application/octet-stream", item.Name);
        });
    }

    private static async Task CleanupLoop()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        while (await timer.WaitForNextTickAsync())
        {
            var now = DateTime.UtcNow;
            foreach (var kv in Items)
                if (kv.Value.Expiry < now) Items.TryRemove(kv.Key, out _);
        }
    }
}
