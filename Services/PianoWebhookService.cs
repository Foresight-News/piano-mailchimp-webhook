using Microsoft.Extensions.Options;
using piano_mailchimp_webhook.Config;
using piano_mailchimp_webhook.Models;

namespace piano_mailchimp_webhook.Services;

public sealed class PianoWebhookService(
    ILogger<PianoWebhookService> logger,
    IOptions<PianoWebhookOptions> options) : IPianoWebhookService
{
    public Task ProcessAsync(PianoWebhookPayload payload, CancellationToken cancellationToken)
    {
        logger.LogInformation(
            "Received Piano webhook event {EventType} for user {UserId}. Mailchimp audience configured: {AudienceConfigured}",
            payload.EventType ?? "unknown",
            payload.UserId ?? "unknown",
            !string.IsNullOrWhiteSpace(options.Value.MailchimpAudienceId));

        return Task.CompletedTask;
    }
}
