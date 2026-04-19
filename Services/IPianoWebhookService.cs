using piano_mailchimp_webhook.Models;

namespace piano_mailchimp_webhook.Services;

public interface IPianoWebhookService
{
    Task ProcessAsync(PianoWebhookEvent webhookEvent, CancellationToken cancellationToken);
}
