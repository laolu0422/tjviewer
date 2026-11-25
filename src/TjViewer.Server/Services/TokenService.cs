using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.IdentityModel.Tokens;
using TjViewer.Server.Data;

namespace TjViewer.Server.Services;

/// <summary>JWT 访问令牌 + 不透明刷新令牌（见 PRD §5.1 / §7.1）。</summary>
public class TokenService
{
    private readonly SymmetricSecurityKey _key;
    private readonly JwtSecurityTokenHandler _handler = new();

    public TimeSpan AccessLifetime { get; } = TimeSpan.FromMinutes(30);
    public TimeSpan RefreshLifetime { get; } = TimeSpan.FromDays(7);

    public TokenService(byte[] key) => _key = new SymmetricSecurityKey(key);

    public string CreateAccessToken(User user)
    {
        var claims = new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, user.Id),
            new Claim("username", user.Username),
            new Claim(ClaimTypes.Role, user.IsAdmin ? "admin" : "user"),
            new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N")),
        };
        var creds = new SigningCredentials(_key, SecurityAlgorithms.HmacSha256);
        var token = new JwtSecurityToken(
            issuer: "tjviewer",
            audience: "tjviewer",
            claims: claims,
            notBefore: DateTime.UtcNow,
            expires: DateTime.UtcNow.Add(AccessLifetime),
            signingCredentials: creds);
        return _handler.WriteToken(token);
    }

    /// <summary>加密随机不透明令牌（刷新/重置共用）。</summary>
    public static string NewOpaqueToken() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32));

    /// <summary>令牌仅以 SHA-256 哈希入库，避免明文落库。</summary>
    public static string HashToken(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    /// <summary>校验访问令牌（WebSocket 信令握手用）。失败返回 null。</summary>
    public ClaimsPrincipal? Validate(string token)
    {
        try
        {
            return _handler.ValidateToken(token, new TokenValidationParameters
            {
                ValidIssuer = "tjviewer",
                ValidAudience = "tjviewer",
                IssuerSigningKey = _key,
                ValidateIssuerSigningKey = true,
                ValidateLifetime = true,
                ClockSkew = TimeSpan.FromSeconds(30),
                NameClaimType = JwtRegisteredClaimNames.Sub,
                RoleClaimType = ClaimTypes.Role,
            }, out _);
        }
        catch { return null; }
    }
}
