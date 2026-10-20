using System.Net;
using System.Net.Mail;
using Microsoft.Extensions.Options;

namespace OpenCredential.AdminWeb.Services;

public sealed class RecoveryDeliveryOptions
{
    public bool Enabled { get; set; }
    public string Host { get; set; } = "";
    public int Port { get; set; } = 587;
    public string Sender { get; set; } = "";
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
}

public sealed class RecoveryDeliveryService(IOptions<RecoveryDeliveryOptions> options)
{
    public bool IsConfigured => options.Value.Enabled && !string.IsNullOrWhiteSpace(options.Value.Host)
        && MailAddress.TryCreate(options.Value.Sender, out _);

    public async Task SendAsync(string email, string token, DateTime? expiresAtUtc)
    {
        if (!IsConfigured) throw new InvalidOperationException("La recuperacion por correo no esta configurada.");
        var settings = options.Value;
        using var message = new MailMessage(settings.Sender, email)
        {
            Subject = "Recuperacion de acceso a OpenCredential",
            Body = $"Tu codigo temporal es: {token}\nVence: {expiresAtUtc:O}\nIntroduce el codigo en el portal institucional. Si no solicitaste el cambio, ignora este mensaje.",
            IsBodyHtml = false
        };
        using var smtp = new SmtpClient(settings.Host, settings.Port)
        {
            EnableSsl = true,
            UseDefaultCredentials = false,
            Timeout = 15000
        };
        if (!string.IsNullOrWhiteSpace(settings.Username)) smtp.Credentials = new NetworkCredential(settings.Username, settings.Password);
        await smtp.SendMailAsync(message);
    }
}
