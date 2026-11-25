using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;

namespace TjViewer.Server.Services;

public class SmtpOptions
{
    public string Host { get; set; } = "";
    public int Port { get; set; } = 587;
    public string User { get; set; } = "";
    public string Password { get; set; } = "";
    public string From { get; set; } = "";
}

/// <summary>使用需求方自有 SMTP 发送找回邮件（见 PRD FR-02）。凭据来自配置，不入源码/日志。</summary>
public class SmtpService
{
    private readonly SmtpOptions _opt;
    private readonly ILogger<SmtpService> _log;

    public SmtpService(IConfiguration cfg, ILogger<SmtpService> log)
    {
        _opt = cfg.GetSection("Smtp").Get<SmtpOptions>() ?? new SmtpOptions();
        _log = log;
    }

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(_opt.Host) && !string.IsNullOrWhiteSpace(_opt.From);

    public async Task SendAsync(string to, string subject, string htmlBody, CancellationToken ct = default)
    {
        var msg = new MimeMessage();
        msg.From.Add(MailboxAddress.Parse(_opt.From));
        msg.To.Add(MailboxAddress.Parse(to));
        msg.Subject = subject;
        msg.Body = new BodyBuilder { HtmlBody = htmlBody }.ToMessageBody();

        using var client = new SmtpClient();
        await client.ConnectAsync(_opt.Host, _opt.Port, SecureSocketOptions.Auto, ct);
        if (!string.IsNullOrEmpty(_opt.User))
            await client.AuthenticateAsync(_opt.User, _opt.Password, ct);
        await client.SendAsync(msg, ct);
        await client.DisconnectAsync(true, ct);
    }
}
