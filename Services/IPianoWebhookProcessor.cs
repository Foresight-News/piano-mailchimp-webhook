using piano_mailchimp_webhook.Models;

namespace piano_mailchimp_webhook.Services;

public interface IPianoWebhookProcessor
{
    Task ProcessAsync(PianoWebhookEvent webhookEvent, CancellationToken cancellationToken = default);
}
