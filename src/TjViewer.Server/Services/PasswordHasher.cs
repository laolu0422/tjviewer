using Isopoh.Cryptography.Argon2;

namespace TjViewer.Server.Services;

/// <summary>密码哈希：argon2id（见 PRD §7.1，禁止明文/弱哈希）。</summary>
public static class PasswordHasher
{
    public static string Hash(string password) => Argon2.Hash(password);

    public static bool Verify(string password, string hash)
    {
        try { return Argon2.Verify(hash, password); }
        catch { return false; }
    }
}
