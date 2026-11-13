using FiveSafesTes.Core.Models;
using MailKit.Net.Smtp;
using MimeKit;

namespace DARE_Egress.Services
{
    public interface IDareEmailService
    {
        Task EmailTo(string emailTo, string Subject, string body, bool IsHtml);
    }

    /// <summary>Sends reviewer notifications through MailKit's SMTP client, without authentication.</summary>
    public class DareEmailService : IDareEmailService
    {
        private readonly EmailSettings _EmailSettings;

        public DareEmailService(EmailSettings emailSettings)
        {
            _EmailSettings = emailSettings;
        }

        public async Task EmailTo(string emailTo, string Subject, string body, bool IsHtml)
        {
            if (!_EmailSettings.Enabled)
            {
                return;
            }

            var message = new MimeMessage();
            message.From.Add(new MailboxAddress(_EmailSettings.FromDisplayName, _EmailSettings.FromAddress));
            message.To.Add(MailboxAddress.Parse(emailTo));
            message.Subject = Subject;
            message.Body = new TextPart(IsHtml ? "html" : "plain") { Text = body };

            using var client = new SmtpClient();
            // As before: EnableSSL means TLS from the start, otherwise STARTTLS when the server offers it.
            await client.ConnectAsync(_EmailSettings.Host, _EmailSettings.Port, _EmailSettings.EnableSSL);
            await client.SendAsync(message);
            await client.DisconnectAsync(true);
        }
    }
}
