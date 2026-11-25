using System.Text.RegularExpressions;

namespace TjViewer.Server;

/// <summary>服务端输入校验（见 PRD §7.2，前端校验不可信）。</summary>
public static class Validation
{
    private static readonly Regex UsernameRx = new("^[A-Za-z0-9_]{3,32}$", RegexOptions.Compiled);

    public static bool ValidUsername(string? s) => !string.IsNullOrWhiteSpace(s) && UsernameRx.IsMatch(s.Trim());

    public static bool ValidEmail(string? s)
    {
        if (string.IsNullOrWhiteSpace(s) || s.Length > 254) return false;
        return System.Net.Mail.MailAddress.TryCreate(s.Trim(), out _);
    }

    public static bool ValidPassword(string? s)
        => !string.IsNullOrEmpty(s) && s.Length >= 8 && s.Any(char.IsLetter) && s.Any(char.IsDigit);

    public static bool ValidDeviceName(string? s) => !string.IsNullOrWhiteSpace(s) && s.Trim().Length is >= 1 and <= 64;

    public static bool ValidNote(string? s) => s is null || s.Length <= 256;
}
