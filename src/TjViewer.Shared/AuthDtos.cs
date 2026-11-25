namespace TjViewer.Shared;

/// <summary>注册：邮箱必填唯一（见 PRD FR-01）。</summary>
public sealed record RegisterRequest(string Username, string Email, string Password);

/// <summary>登录：Account 可为用户名或邮箱（见 PRD FR-01）。</summary>
public sealed record LoginRequest(string Account, string Password);

public sealed record AuthResponse(string Token, string RefreshToken, string Username, string Email, long ExpiresAt);

public sealed record RefreshRequest(string RefreshToken);

/// <summary>发起密码找回（见 PRD FR-02）。</summary>
public sealed record PasswordResetRequest(string Email);

/// <summary>确认重置：限时单次令牌（见 PRD FR-02）。</summary>
public sealed record PasswordResetConfirm(string Token, string NewPassword);

public sealed record ChangePasswordRequest(string OldPassword, string NewPassword);
