using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.Extensions.Options;

namespace VarPlats.Web;

public sealed class MailjetEmailSender(HttpClient httpClient, IOptions<MailjetOptions> options)
{
    public async Task SendAccountLinkAsync(
        string recipient, string subject, string message, string link, CancellationToken cancellationToken)
    {
        var settings = options.Value;
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.mailjet.com/v3.1/send");
        var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{settings.ApiKey}:{settings.ApiSecret}"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", credentials);
        var safeLink = HtmlEncoder.Default.Encode(link);
        request.Content = JsonContent.Create(new
        {
            Messages = new[]
            {
                new
                {
                    From = new { Email = settings.SenderEmail, Name = settings.SenderName },
                    To = new[] { new { Email = recipient } },
                    Subject = subject,
                    TextPart = $"{message}\n\n{link}",
                    HTMLPart = $"<p>{HtmlEncoder.Default.Encode(message)}</p><p><a href=\"{safeLink}\">Fortsätt</a></p>"
                }
            }
        });

        using var response = await httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Mailjet rejected the message (HTTP {(int)response.StatusCode}).");

        var result = await response.Content.ReadFromJsonAsync<MailjetResponse>(cancellationToken);
        if (result?.Messages is not { Length: > 0 } messages
            || messages.Any(messageResult => !string.Equals(messageResult.Status, "success", StringComparison.OrdinalIgnoreCase)))
            throw new HttpRequestException("Mailjet did not accept the account email.");
    }

    private sealed record MailjetResponse(MailjetMessageResult[]? Messages);
    private sealed record MailjetMessageResult(string? Status);
}
