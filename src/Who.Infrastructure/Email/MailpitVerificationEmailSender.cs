using System.Net.Mail;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Who.Application.Authentication;

namespace Who.Infrastructure.Email;

public sealed class MailpitVerificationEmailSender(IConfiguration config, IHostEnvironment environment) : IVerificationEmailSender
{
    public async Task SendAsync(string email, string code, CancellationToken cancellationToken)
    {
        if (!environment.IsDevelopment()) throw new InvalidOperationException("Production email delivery is not configured.");
        var host = config["WHO_SMTP_HOST"] ?? "127.0.0.1";
        var port = int.TryParse(config["WHO_SMTP_PORT"], out var p) ? p : 1025;
        using var client = new SmtpClient(host, port) { EnableSsl = false, UseDefaultCredentials = false, Timeout = 5000 };
        using var message = new MailMessage(config["WHO_SMTP_FROM"] ?? "WHO? <no-reply@who.local>", email)
        {
            Subject = "WHO? — E-posta doğrulama / Email verification",
            Body = $"WHO?\n\nDoğrulama kodunuz / Your verification code: {code}\n\n10 dakika içinde sona erer. / Expires in 10 minutes.\nBu işlemi siz başlatmadıysanız yok sayın. / Ignore if you did not request this."
        };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        await client.SendMailAsync(message, timeout.Token);
    }
}
