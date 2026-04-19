using piano_mailchimp_webhook.Models;

namespace piano_mailchimp_webhook.Services;

public interface IPianoWebhookService
{
    Task ProcessAsync(PianoWebhookPayload payload, CancellationToken cancellationToken);
}
